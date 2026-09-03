using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HybridCLR.Editor;
using UnityEditor;
using UnityEditor.Compilation;

namespace RemoteExecution.HybridCLR
{
    internal sealed class HybridCLRRemoteBuildRequest
    {
        internal HybridCLRRemoteBuildRequest(string target,
            string source,
            string entryTypeName,
            string entryMethodName)
        {
            Target = target;
            Source = source;
            EntryTypeName = entryTypeName;
            EntryMethodName = entryMethodName;
        }

        internal string Target { get; }
        internal string Source { get; }
        internal string EntryTypeName { get; }
        internal string EntryMethodName { get; }
    }

    internal sealed class HybridCLRRemoteBuildOutput
    {
        internal HybridCLRRemoteBuildOutput(string target,
            IReadOnlyList<HybridCLRBundleArtifact> artifacts,
            string entryCommandId)
        {
            Target = target;
            Artifacts = artifacts;
            EntryCommandId = entryCommandId;
        }

        internal string Target { get; }
        internal IReadOnlyList<HybridCLRBundleArtifact> Artifacts { get; }
        internal string EntryCommandId { get; }
    }

    internal static class HybridCLRRemoteExecutionCompiler
    {
        internal const string DynamicAssemblyName = "RemoteExecution.Dynamic";
        internal const int MaxSourceBytes = 512 * 1024;
        private const int DynamicCompileTimeoutSeconds = 120;

        internal static async Task<HybridCLRRemoteBuildOutput> BuildAsync(
            HybridCLRRemoteBuildRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            ValidateInput(request);
            BuildTarget target = ParseTarget(request.Target);
            if (!SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.Contains(
                    DynamicAssemblyName, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Configure '{DynamicAssemblyName}' as a HybridCLR hot-update assembly before using custom source.");

            string outputDirectory = Path.Combine("Temp/RemoteHybridCLR", request.Target,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDirectory);
            HybridCLRBundleArtifact dynamicArtifact = await CompileDynamicSourceAsync(
                target, outputDirectory, DynamicAssemblyName, request.Source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string entryCommandId =
                $"{DynamicAssemblyName}::{request.EntryTypeName}::{request.EntryMethodName}";
            return new HybridCLRRemoteBuildOutput(request.Target,
                new[] { dynamicArtifact }, entryCommandId);
        }

        private static void ValidateInput(HybridCLRRemoteBuildRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Source)) throw new InvalidOperationException("Custom source code is required.");
            if (Encoding.UTF8.GetByteCount(request.Source) > MaxSourceBytes)
                throw new InvalidOperationException($"Custom source code exceeds {MaxSourceBytes} bytes.");
            if (request.Source.IndexOf("RemoteCommand", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("Custom source must contain a RemoteCommand attribute on its entry method.");
            if (string.IsNullOrWhiteSpace(request.EntryTypeName) ||
                string.IsNullOrWhiteSpace(request.EntryMethodName))
                throw new InvalidOperationException("Entry type and method are required.");
            if (request.EntryTypeName.Contains("::") || request.EntryMethodName.Contains("::"))
                throw new InvalidOperationException("Entry type and method must not contain '::'.");
            if (request.EntryTypeName.Length > 1024 || request.EntryMethodName.Length > 1024)
                throw new InvalidOperationException("Entry type and method are too long.");
            if (!Enum.TryParse(request.Target, true, out BuildTarget _))
                throw new InvalidOperationException($"Unsupported Player target '{request.Target}'.");
        }

        private static BuildTarget ParseTarget(string target)
        {
            if (!Enum.TryParse(target, true, out BuildTarget result))
                throw new InvalidOperationException($"Unsupported Player target '{target}'.");
            return result;
        }

        private static async Task<HybridCLRBundleArtifact> CompileDynamicSourceAsync(BuildTarget target,
            string outputDirectory, string assemblyName, string source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dynamicDirectory = Path.Combine(outputDirectory, "Dynamic");
            Directory.CreateDirectory(dynamicDirectory);
            string sourcePath = Path.Combine(dynamicDirectory, assemblyName + ".cs");
            string dllPath = Path.Combine(dynamicDirectory, assemblyName + ".dll");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
#pragma warning disable 0618
            var builder = new AssemblyBuilder(dllPath, sourcePath)
            {
                buildTarget = target,
                buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target),
                additionalReferences = GetPlayerReferences()
            };
            var completion = new TaskCompletionSource<CompilerMessage[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            builder.buildFinished += (path, messages) => completion.TrySetResult(messages ?? Array.Empty<CompilerMessage>());
            if (!builder.Build()) throw new InvalidOperationException("The dynamic source compiler is already busy.");
#pragma warning restore 0618
            Task finished = completion.Task;
            Task timeout = Task.Delay(TimeSpan.FromSeconds(DynamicCompileTimeoutSeconds));
            if (await Task.WhenAny(finished, timeout) != finished)
                throw new TimeoutException("Dynamic source compilation timed out.");
            cancellationToken.ThrowIfCancellationRequested();
            CompilerMessage[] diagnostics = await completion.Task;
            if (diagnostics.Any(item => item.type == CompilerMessageType.Error))
                throw new InvalidOperationException("Dynamic source compilation failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, diagnostics.Select(FormatDiagnostic)));
            if (!File.Exists(dllPath)) throw new FileNotFoundException("Dynamic source DLL was not produced.", dllPath);
            cancellationToken.ThrowIfCancellationRequested();
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            return new HybridCLRBundleArtifact(assemblyName, File.ReadAllBytes(dllPath), File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null);
        }

        private static string[] GetPlayerReferences()
        {
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                foreach (string reference in assembly.compiledAssemblyReferences ?? Array.Empty<string>())
                    if (File.Exists(reference)) references.Add(reference);
                if (!string.IsNullOrEmpty(assembly.outputPath) && File.Exists(assembly.outputPath)) references.Add(assembly.outputPath);
            }
            return references.ToArray();
        }

        private static string FormatDiagnostic(CompilerMessage diagnostic)
        {
            string location = string.IsNullOrEmpty(diagnostic.file) ? string.Empty : $" {diagnostic.file}({diagnostic.line},{diagnostic.column})";
            return $"{diagnostic.type}{location}: {diagnostic.message}";
        }
    }
}
