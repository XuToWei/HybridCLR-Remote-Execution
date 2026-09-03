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
        private static readonly SemaphoreSlim s_ApplyLock = new SemaphoreSlim(1, 1);
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
                    "Apply and execute HybridCLR bundle",
                    "Validates and loads a HybridCLR assembly bundle, then executes its entry.",
                    "HybridCLR",
                    timeoutSeconds: 180,
                    maxRequestBytes: HybridCLRBundleCodec.MaxEnvelopeBytes,
                    maxResponseBytes: 0,
                    requestContentType: HybridCLRBundleCodec.ContentType,
                    responseContentType: string.Empty,
                    requiresMainThread: true),
                ApplyAsync);
        }

        private static async Task<RemoteCommandResult> ApplyAsync(
            RemoteCommandContext context,
            CancellationToken cancellationToken)
        {
            await s_ApplyLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (s_Poisoned)
                    return RemoteCommandResult.Failure(
                        "PARTIAL_APPLY_RESTART_REQUIRED",
                        "A previous HybridCLR load failed after changing the AppDomain. Restart the Player.");

                HybridCLRBundle bundle;
                try { bundle = HybridCLRBundleCodec.Decode(context.Payload); }
                catch (Exception exception)
                {
                    return RemoteCommandResult.Failure("INVALID_BUNDLE", exception.Message);
                }
                if (!string.Equals(bundle.Target, GetRuntimeTarget(),
                    StringComparison.OrdinalIgnoreCase))
                    return RemoteCommandResult.Failure(
                        "TARGET_MISMATCH",
                        $"Bundle target '{bundle.Target}' does not match Player target '{GetRuntimeTarget()}'.");

                if (!TryPrepareEntry(bundle, cancellationToken,
                    out IHybridCLRRemoteExecutionEntry entry,
                    out RemoteCommandResult failure))
                    return failure;

                try
                {
                    Task execution = entry.ExecuteAsync(cancellationToken);
                    if (execution == null)
                        throw new InvalidOperationException("HybridCLR entry returned no Task.");
                    await execution;
                    cancellationToken.ThrowIfCancellationRequested();
                    return RemoteCommandResult.Success(
                        "HybridCLR bundle applied and entry executed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return RemoteCommandResult.Failure(
                        "ENTRY_EXECUTION_FAILED",
                        exception.Message + " The assemblies remain loaded in the Player.");
                }
            }
            finally { s_ApplyLock.Release(); }
        }

        private static bool TryPrepareEntry(HybridCLRBundle bundle,
            CancellationToken cancellationToken,
            out IHybridCLRRemoteExecutionEntry entry,
            out RemoteCommandResult failure)
        {
            entry = null;
            failure = null;
            Dictionary<string, Assembly> loadedByName = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly != null && !string.IsNullOrEmpty(assembly.GetName().Name))
                .GroupBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            byte[] bundleSignature = ComputeBundleSignature(bundle);
            if (s_AppliedBundleContents.TryGetValue(bundle.BundleId,
                    out byte[] appliedBundleSignature) &&
                !RemoteExecutionProtocol.FixedTimeEquals(appliedBundleSignature,
                    bundleSignature))
            {
                failure = RemoteCommandResult.Failure(
                    "BUNDLE_ID_CONFLICT",
                    "The bundle ID was already applied with different content.");
                return false;
            }

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
                    {
                        failure = RemoteCommandResult.Failure(
                            "ASSEMBLY_VERSION_CONFLICT",
                            $"Assembly '{artifact.Name}' is already loaded with another version. Restart the Player.");
                        return false;
                    }
                    continue;
                }
                if (loadedByName.ContainsKey(artifact.Name))
                {
                    failure = RemoteCommandResult.Failure(
                        "ASSEMBLY_NAME_CONFLICT",
                        $"Assembly '{artifact.Name}' was already loaded outside this adapter.");
                    return false;
                }
                pending.Add(artifact);
            }

            var newlyLoaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
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
                    if (!string.Equals(assembly.GetName().Name, artifact.Name,
                        StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Loaded assembly name '{assembly.GetName().Name}' does not match '{artifact.Name}'.");
                    newlyLoaded.Add(artifact.Name, assembly);
                }

                Assembly[] bundleAssemblies = bundle.Artifacts.Select(artifact =>
                {
                    if (newlyLoaded.TryGetValue(artifact.Name, out Assembly loaded))
                        return loaded;
                    if (s_AppliedAssemblies.TryGetValue(artifact.Name, out Assembly applied))
                        return applied;
                    throw new InvalidOperationException(
                        $"Applied assembly is unavailable: {artifact.Name}");
                }).Distinct().ToArray();
                entry = CreateEntry(bundleAssemblies);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (KeyValuePair<string, byte[]> item in hashes)
                    s_AppliedHashes[item.Key] = item.Value;
                foreach (KeyValuePair<string, Assembly> item in newlyLoaded)
                    s_AppliedAssemblies[item.Key] = item.Value;
                s_AppliedBundleContents[bundle.BundleId] = bundleSignature;
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (changedAppDomain) s_Poisoned = true;
                throw;
            }
            catch (Exception exception)
            {
                if (changedAppDomain)
                {
                    s_Poisoned = true;
                    failure = RemoteCommandResult.Failure(
                        "PARTIAL_APPLY_RESTART_REQUIRED",
                        exception.Message + " Restart the Player before applying another bundle.");
                }
                else
                {
                    failure = RemoteCommandResult.Failure(
                        "ENTRY_RESOLUTION_FAILED", exception.Message);
                }
                return false;
            }
        }

        private static IHybridCLRRemoteExecutionEntry CreateEntry(
            IEnumerable<Assembly> assemblies)
        {
            Type[] entryTypes = (assemblies ?? Array.Empty<Assembly>())
                .Where(assembly => assembly != null)
                .SelectMany(GetLoadableTypes)
                .Where(type => type != null && type.IsClass && type.IsPublic &&
                    !type.IsAbstract && !type.ContainsGenericParameters &&
                    typeof(IHybridCLRRemoteExecutionEntry).IsAssignableFrom(type) &&
                    !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(type => type.Assembly.FullName, StringComparer.Ordinal)
                .ThenBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            if (entryTypes.Length != 1)
                throw new InvalidOperationException(
                    entryTypes.Length == 0
                        ? "The HybridCLR bundle must contain one public IHybridCLRRemoteExecutionEntry implementation with a public parameterless constructor."
                        : "The HybridCLR bundle contains multiple IHybridCLRRemoteExecutionEntry implementations.");
            return (IHybridCLRRemoteExecutionEntry)Activator.CreateInstance(entryTypes[0]);
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }

        private static byte[] ComputeBundleSignature(HybridCLRBundle bundle)
        {
            using (var sha = SHA256.Create())
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream,
                System.Text.Encoding.UTF8, true))
            {
                WriteSignatureString(writer, bundle.Target);
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

        private static void WriteSignatureString(System.IO.BinaryWriter writer,
            string value)
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
            // Loading this type makes the optional adapter visible to provider discovery.
        }
    }
}
