using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HybridCLR.Editor;
using UnityEditor;
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
        internal RemoteExecutionBundle(string target, IReadOnlyList<RemoteExecutionArtifact> artifacts)
        {
            BundleId = Guid.NewGuid();
            Generation = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Target = target;
            Artifacts = artifacts;
        }
        internal Guid BundleId { get; }
        internal string Generation { get; }
        internal string Target { get; }
        internal IReadOnlyList<RemoteExecutionArtifact> Artifacts { get; }
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
        internal static RemoteExecutionBundle Compile(BuildTarget target)
        {
            if (!HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable)
                throw new InvalidOperationException("HybridCLR is not enabled.");
            string outputDirectory = Path.Combine("Temp/RemoteHybridCLR", target.ToString());
            Directory.CreateDirectory(outputDirectory);
            var settings = new ScriptCompilationSettings
            {
                group = BuildPipeline.GetBuildTargetGroup(target),
                target = target,
                extraScriptingDefines = new[] { "UNITY_COMPILE" },
                options = EditorUserBuildSettings.development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None
            };
            PlayerBuildInterface.CompilePlayerScripts(settings, outputDirectory);
#if UNITY_2022
            EditorUtility.ClearProgressBar();
#endif
            var artifacts = new List<RemoteExecutionArtifact>();
            foreach (string name in SettingsUtil.HotUpdateAssemblyNamesExcludePreserved.Distinct(StringComparer.Ordinal))
            {
                string dllPath = Path.Combine(outputDirectory, name + ".dll");
                if (!File.Exists(dllPath)) throw new FileNotFoundException("Compiled hot-update DLL was not found.", dllPath);
                string pdbPath = Path.Combine(outputDirectory, name + ".pdb");
                artifacts.Add(new RemoteExecutionArtifact(name, File.ReadAllBytes(dllPath), File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null));
            }
            if (artifacts.Count == 0) throw new InvalidOperationException("HybridCLR has no configured hot-update assemblies.");
            return new RemoteExecutionBundle(target.ToString(), artifacts);
        }
    }
}
