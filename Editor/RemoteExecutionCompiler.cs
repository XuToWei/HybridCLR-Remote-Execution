using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HybridCLR.Editor;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Build.Player;
using UnityEngine;

namespace HybridCLR.RemoteExecution
{
    internal sealed class RemoteExecutionArtifact
    {
        internal RemoteExecutionArtifact(string name, byte[] dll, byte[] pdb)
        {
            Name = name;
            Dll = dll;
            Pdb = pdb;
            DllSha256 = ComputeHash(dll);
            PdbSha256 = pdb == null || pdb.Length == 0 ? Array.Empty<byte>() : ComputeHash(pdb);
        }
        internal string Name { get; }
        internal byte[] Dll { get; }
        internal byte[] Pdb { get; }
        internal byte[] DllSha256 { get; }
        internal byte[] PdbSha256 { get; }
        private static byte[] ComputeHash(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
    }

    internal sealed class RemoteExecutionBundle
    {
        internal RemoteExecutionBundle(string target, IReadOnlyList<RemoteExecutionArtifact> artifacts, string entryMethodId)
        {
            BundleId = Guid.NewGuid();
            Generation = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Target = target;
            Artifacts = artifacts;
            EntryMethodId = entryMethodId;
        }
        internal Guid BundleId { get; }
        internal string Generation { get; }
        internal string Target { get; }
        internal IReadOnlyList<RemoteExecutionArtifact> Artifacts { get; }
        internal string EntryMethodId { get; }
        internal RemoteBundleManifest ToManifest()
        {
            return new RemoteBundleManifest
            {
                BundleId = BundleId,
                Generation = Generation,
                Target = Target,
                Assemblies = Artifacts.Select(item => new RemoteAssemblyInfo
                {
                    Name = item.Name,
                    DllLength = item.Dll.LongLength,
                    PdbLength = item.Pdb?.LongLength ?? 0,
                    DllSha256 = item.DllSha256,
                    PdbSha256 = item.PdbSha256
                }).ToArray()
            };
        }
    }

    internal static class RemoteExecutionCompiler
    {
        internal const string DynamicAssemblyName = "RemoteExecution.Dynamic";
        internal const int MaxSourceBytes = 512 * 1024;
        private const int DynamicCompileTimeoutSeconds = 120;

        internal static async Task<RemoteExecutionBundle> CompileAsync(BuildTarget target,
            IEnumerable<string> selectedAssemblyNames, IEnumerable<string> selectedDefines,
            string source, string entryTypeName, string entryMethodName)
        {
            ValidateInput(selectedAssemblyNames, source, entryTypeName, entryMethodName);
            string[] defines = (selectedDefines ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (!HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable)
                throw new InvalidOperationException("HybridCLR is not enabled.");

            string[] hotUpdateNames = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved
                .Distinct(StringComparer.Ordinal).ToArray();
            var available = new HashSet<string>(hotUpdateNames, StringComparer.Ordinal);
            if (!available.Contains(DynamicAssemblyName))
                throw new InvalidOperationException($"Configure '{DynamicAssemblyName}' as a HybridCLR hot-update assembly before using custom source.");
            var requested = new HashSet<string>(selectedAssemblyNames, StringComparer.Ordinal);
            if (requested.Contains(DynamicAssemblyName))
                throw new InvalidOperationException($"'{DynamicAssemblyName}' is reserved for the custom source assembly.");
            string invalid = requested.FirstOrDefault(name => !available.Contains(name));
            if (invalid != null) throw new InvalidOperationException($"'{invalid}' is not a configured hot-update assembly.");

            string outputDirectory = Path.Combine("Temp/RemoteHybridCLR", target.ToString(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDirectory);
            var settings = new ScriptCompilationSettings
            {
                group = BuildPipeline.GetBuildTargetGroup(target),
                target = target,
                extraScriptingDefines = defines,
                options = EditorUserBuildSettings.development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None
            };
            PlayerBuildInterface.CompilePlayerScripts(settings, outputDirectory);
#if UNITY_2022
            EditorUtility.ClearProgressBar();
#endif

            string[] orderedNames = GetDependencyClosure(requested, hotUpdateNames);
            var artifacts = new List<RemoteExecutionArtifact>();
            foreach (string name in orderedNames)
            {
                string dllPath = Path.Combine(outputDirectory, name + ".dll");
                if (!File.Exists(dllPath)) throw new FileNotFoundException("Compiled hot-update DLL was not found.", dllPath);
                string pdbPath = Path.Combine(outputDirectory, name + ".pdb");
                artifacts.Add(new RemoteExecutionArtifact(name, File.ReadAllBytes(dllPath),
                    File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null));
            }

            // A generated assembly requires HybridCLR hot-update configuration before an IL2CPP Player build.
            string dynamicAssemblyName = DynamicAssemblyName;
            RemoteExecutionArtifact dynamicArtifact = await CompileDynamicSourceAsync(target, outputDirectory,
                dynamicAssemblyName, source, defines);
            artifacts.Add(dynamicArtifact);
            string entryMethodId = $"{dynamicAssemblyName}::{entryTypeName}::{entryMethodName}";
            return new RemoteExecutionBundle(target.ToString(), artifacts, entryMethodId);
        }

        private static void ValidateInput(IEnumerable<string> selectedAssemblyNames, string source,
            string entryTypeName, string entryMethodName)
        {
            if (selectedAssemblyNames == null || !selectedAssemblyNames.Any(name => !string.IsNullOrWhiteSpace(name)))
                throw new InvalidOperationException("Select at least one hot-update assembly.");
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Custom source code is required.");
            if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
                throw new InvalidOperationException($"Custom source code exceeds {MaxSourceBytes} bytes.");
            if (source.IndexOf("RemoteCallable", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Custom source must contain a RemoteCallable attribute on its entry method.");
            if (string.IsNullOrWhiteSpace(entryTypeName) || string.IsNullOrWhiteSpace(entryMethodName))
                throw new InvalidOperationException("Entry type and method are required.");
            if (entryTypeName.IndexOf("::", StringComparison.Ordinal) >= 0 || entryMethodName.IndexOf("::", StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException("Entry type and method cannot contain '::'.");
        }

        private static string[] GetDependencyClosure(HashSet<string> requested, string[] hotUpdateNames)
        {
            var available = new HashSet<string>(hotUpdateNames, StringComparer.Ordinal);
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var byName = assemblies.ToDictionary(item => item.name, StringComparer.Ordinal);
            var result = new List<string>();
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);

            void Visit(string name)
            {
                if (visited.Contains(name)) return;
                if (!visiting.Add(name)) throw new InvalidOperationException($"Circular hot-update dependency detected at '{name}'.");
                if (byName.TryGetValue(name, out Assembly assembly))
                {
                    foreach (string dependency in assembly.assemblyReferences ?? Array.Empty<string>())
                        if (available.Contains(dependency)) Visit(dependency);
                }
                visiting.Remove(name);
                visited.Add(name);
                result.Add(name);
            }

            foreach (string name in hotUpdateNames)
                if (requested.Contains(name)) Visit(name);
            return result.ToArray();
        }

        private static async Task<RemoteExecutionArtifact> CompileDynamicSourceAsync(BuildTarget target,
            string outputDirectory, string assemblyName, string source, string[] defines)
        {
            string dynamicDirectory = Path.Combine(outputDirectory, "Dynamic");
            Directory.CreateDirectory(dynamicDirectory);
            string sourcePath = Path.Combine(dynamicDirectory, assemblyName + ".cs");
            string dllPath = Path.Combine(dynamicDirectory, assemblyName + ".dll");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));

            var builder = new AssemblyBuilder(dllPath, sourcePath)
            {
                buildTarget = target,
                buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target),
                additionalDefines = defines,
                additionalReferences = GetPlayerReferences(outputDirectory)
            };
            var completion = new TaskCompletionSource<CompilerMessage[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            builder.buildFinished += (path, messages) => completion.TrySetResult(messages ?? Array.Empty<CompilerMessage>());
            if (!builder.Build()) throw new InvalidOperationException("The dynamic source compiler is already busy.");

            Task finished = completion.Task;
            Task timeout = Task.Delay(TimeSpan.FromSeconds(DynamicCompileTimeoutSeconds));
            if (await Task.WhenAny(finished, timeout) != finished)
                throw new TimeoutException("Dynamic source compilation timed out.");
            CompilerMessage[] diagnostics = await completion.Task;
            if (diagnostics.Any(item => item.type == CompilerMessageType.Error))
            {
                string detail = string.Join(Environment.NewLine, diagnostics.Select(FormatDiagnostic));
                throw new InvalidOperationException("Dynamic source compilation failed:" + Environment.NewLine + detail);
            }
            if (!File.Exists(dllPath)) throw new FileNotFoundException("Dynamic source DLL was not produced.", dllPath);
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            return new RemoteExecutionArtifact(assemblyName, File.ReadAllBytes(dllPath),
                File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null);
        }

        private static string[] GetPlayerReferences(string outputDirectory)
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                foreach (string reference in assembly.compiledAssemblyReferences ?? Array.Empty<string>())
                    if (File.Exists(reference)) references.Add(reference);
                if (!string.IsNullOrEmpty(assembly.outputPath) && File.Exists(assembly.outputPath))
                    references.Add(assembly.outputPath);
            }
            foreach (string reference in Directory.GetFiles(outputDirectory, "*.dll", SearchOption.AllDirectories))
                references.Add(reference);
            return references.ToArray();
        }

        private static string FormatDiagnostic(CompilerMessage diagnostic)
        {
            string location = string.IsNullOrEmpty(diagnostic.file) ? string.Empty :
                $" {diagnostic.file}({diagnostic.line},{diagnostic.column})";
            return $"{diagnostic.type}{location}: {diagnostic.message}";
        }
    }
}
