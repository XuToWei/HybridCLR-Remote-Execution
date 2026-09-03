using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace RemoteExecution.HybridCLR
{
    [Preserve]
    public sealed class HybridCLRRemoteExecutionCommandProvider : IRemoteCommandProvider
    {
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<string, byte[]> s_AppliedHashes =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Assembly> s_AppliedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private static readonly Dictionary<Guid, byte[]> s_AppliedBundleContents =
            new Dictionary<Guid, byte[]>();
        private static bool s_Poisoned;

        public HybridCLRRemoteExecutionCommandProvider()
        {
        }

        public void RegisterCommands(IRemoteCommandRegistry registry)
        {
            registry.Register(
                new RemoteCommandDefinition(
                    HybridCLRBundleCodec.ApplyCommandId,
                    "Apply HybridCLR bundle",
                    "Validates and loads a HybridCLR assembly bundle.",
                    "HybridCLR",
                    timeoutSeconds: 180,
                    maxRequestBytes: HybridCLRBundleCodec.MaxEnvelopeBytes,
                    maxResponseBytes: 0,
                    requestContentType: HybridCLRBundleCodec.ContentType,
                    responseContentType: string.Empty,
                    requiresMainThread: true),
                ApplyAsync);
        }

        private static Task<RemoteCommandResult> ApplyAsync(
            RemoteCommandContext context,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<RemoteCommandResult>(cancellationToken);
            lock (s_Lock)
            {
                if (s_Poisoned)
                    return Task.FromResult(RemoteCommandResult.Failure(
                        "PARTIAL_APPLY_RESTART_REQUIRED",
                        "A previous HybridCLR load failed after changing the AppDomain. Restart the Player."));
                HybridCLRBundle bundle;
                try { bundle = HybridCLRBundleCodec.Decode(context.Payload); }
                catch (Exception exception)
                {
                    return Task.FromResult(RemoteCommandResult.Failure("INVALID_BUNDLE", exception.Message));
                }
                if (!string.Equals(bundle.Target, GetRuntimeTarget(), StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(RemoteCommandResult.Failure(
                        "TARGET_MISMATCH",
                        $"Bundle target '{bundle.Target}' does not match Player target '{GetRuntimeTarget()}'."));
                return Task.FromResult(ApplyValidatedBundle(bundle, cancellationToken));
            }
        }

        private static RemoteCommandResult ApplyValidatedBundle(
            HybridCLRBundle bundle,
            CancellationToken cancellationToken)
        {
            Dictionary<string, Assembly> loadedByName = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly != null && !string.IsNullOrEmpty(assembly.GetName().Name))
                .GroupBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            byte[] bundleSignature = ComputeBundleSignature(bundle);
            if (s_AppliedBundleContents.TryGetValue(bundle.BundleId, out byte[] appliedBundleSignature) &&
                !RemoteExecutionProtocol.FixedTimeEquals(appliedBundleSignature, bundleSignature))
                return RemoteCommandResult.Failure("BUNDLE_ID_CONFLICT",
                    "The bundle ID was already applied with different content.");
            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var pending = new List<HybridCLRBundleArtifact>();
            foreach (HybridCLRBundleArtifact artifact in bundle.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] hash = ComputeHash(artifact.Dll);
                hashes.Add(artifact.Name, hash);
                if (s_AppliedHashes.TryGetValue(artifact.Name, out byte[] appliedHash))
                {
                    if (!RemoteExecutionProtocol.FixedTimeEquals(appliedHash, hash))
                        return RemoteCommandResult.Failure("ASSEMBLY_VERSION_CONFLICT",
                            $"Assembly '{artifact.Name}' is already loaded with another version. Restart the Player.");
                    continue;
                }
                if (loadedByName.ContainsKey(artifact.Name))
                    return RemoteCommandResult.Failure("ASSEMBLY_NAME_CONFLICT",
                        $"Assembly '{artifact.Name}' was already loaded outside this adapter.");
                pending.Add(artifact);
            }

            if (pending.Count == 0)
            {
                if (!RemoteCommandRegistry.TryGet(bundle.EntryCommandId, out RemoteCommandDescriptor existingEntry))
                {
                    try
                    {
                        Assembly[] appliedAssemblies = bundle.Artifacts
                            .Select(artifact => s_AppliedAssemblies[artifact.Name])
                            .Distinct()
                            .ToArray();
                        RemoteCommandRegistry.RegisterAttributeCommands(appliedAssemblies);
                        RemoteCommandRegistry.TryGet(bundle.EntryCommandId, out existingEntry);
                    }
                    catch (Exception exception)
                    {
                        return RemoteCommandResult.Failure("COMMAND_REGISTRATION_FAILED", exception.Message);
                    }
                }
                if (existingEntry != null && existingEntry.IsExecutable && existingEntry.MaxRequestBytes == 0)
                {
                    s_AppliedBundleContents[bundle.BundleId] = bundleSignature;
                    return RemoteCommandResult.Success("HybridCLR bundle was already applied.");
                }
                return RemoteCommandResult.Failure("ENTRY_COMMAND_NOT_FOUND",
                    $"Applied bundle entry command is not registered: {bundle.EntryCommandId}");
            }

            var loaded = new List<Assembly>(pending.Count);
            bool changedAppDomain = false;
            try
            {
                foreach (HybridCLRBundleArtifact artifact in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assembly assembly = artifact.Pdb.Length > 0
                        ? Assembly.Load(artifact.Dll, artifact.Pdb)
                        : Assembly.Load(artifact.Dll);
                    changedAppDomain = true;
                    if (!string.Equals(assembly.GetName().Name, artifact.Name, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Loaded assembly name '{assembly.GetName().Name}' does not match '{artifact.Name}'.");
                    loaded.Add(assembly);
                }
                IReadOnlyList<RemoteCommandDescriptor> registered =
                    RemoteCommandRegistry.RegisterAttributeCommands(loaded);
                try
                {
                    if (!RemoteCommandRegistry.TryGet(bundle.EntryCommandId, out RemoteCommandDescriptor entry) ||
                        !entry.IsExecutable || entry.MaxRequestBytes != 0)
                        throw new InvalidOperationException(
                            $"Expected zero-payload entry command was not registered: {bundle.EntryCommandId}");
                    foreach (KeyValuePair<string, byte[]> item in hashes)
                        s_AppliedHashes[item.Key] = item.Value;
                    foreach (Assembly assembly in loaded)
                        s_AppliedAssemblies[assembly.GetName().Name] = assembly;
                    s_AppliedBundleContents[bundle.BundleId] = bundleSignature;
                    return RemoteCommandResult.Success("HybridCLR bundle applied.");
                }
                catch
                {
                    RemoteCommandRegistry.Unregister(registered);
                    throw;
                }
            }
            catch (Exception exception)
            {
                if (changedAppDomain)
                {
                    s_Poisoned = true;
                    return RemoteCommandResult.Failure(
                        "PARTIAL_APPLY_RESTART_REQUIRED",
                        exception.Message + " Restart the Player before applying another bundle.");
                }
                return RemoteCommandResult.Failure("LOAD_FAILED", exception.Message);
            }
        }

        private static byte[] ComputeBundleSignature(HybridCLRBundle bundle)
        {
            using (var sha = SHA256.Create())
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                WriteSignatureString(writer, bundle.Target);
                WriteSignatureString(writer, bundle.EntryCommandId);
                writer.Write(bundle.Artifacts.Count);
                foreach (HybridCLRBundleArtifact artifact in bundle.Artifacts)
                {
                    WriteSignatureString(writer, artifact.Name);
                    writer.Write(artifact.Dll.Length);
                    writer.Write(ComputeHash(artifact.Dll));
                    writer.Write(artifact.Pdb.Length);
                    writer.Write(ComputeHash(artifact.Pdb));
                }
                writer.Flush();
                return sha.ComputeHash(stream.ToArray());
            }
        }

        private static void WriteSignatureString(System.IO.BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static byte[] ComputeHash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        private static string GetRuntimeTarget()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return "Android";
#elif UNITY_IOS && !UNITY_EDITOR
            return "iOS";
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return "StandaloneWindows64";
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
            return "StandaloneOSX";
#elif UNITY_STANDALONE_LINUX && !UNITY_EDITOR
            return "StandaloneLinux64";
#else
            return Application.platform.ToString();
#endif
        }
    }

    internal static class HybridCLRRemoteExecutionStartup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            // Loading this type makes the optional adapter visible to the core provider scan.
        }
    }
}
