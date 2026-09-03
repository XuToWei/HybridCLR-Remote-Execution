using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RemoteExecution.HybridCLR
{
    public sealed class HybridCLRBundleArtifact
    {
        public HybridCLRBundleArtifact(string name, byte[] dll, byte[] pdb = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Dll = dll ?? throw new ArgumentNullException(nameof(dll));
            Pdb = pdb ?? Array.Empty<byte>();
        }

        public string Name { get; }
        public byte[] Dll { get; }
        public byte[] Pdb { get; }
    }

    public sealed class HybridCLRBundle
    {
        public HybridCLRBundle(Guid bundleId, string target, string entryCommandId,
            IReadOnlyList<HybridCLRBundleArtifact> artifacts)
        {
            BundleId = bundleId;
            Target = target ?? throw new ArgumentNullException(nameof(target));
            EntryCommandId = entryCommandId ?? throw new ArgumentNullException(nameof(entryCommandId));
            Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        }

        public Guid BundleId { get; }
        public string Target { get; }
        public string EntryCommandId { get; }
        public IReadOnlyList<HybridCLRBundleArtifact> Artifacts { get; }
    }

    public static class HybridCLRBundleCodec
    {
        public const string ApplyCommandId = "hybridclr.apply-bundle";
        public const string ContentType = "application/vnd.remote-execution.hybridclr-bundle";
        public const int MaxEnvelopeBytes = RemoteExecutionProtocol.MaxCommandRequestBytes;
        public const int MaxAssemblyCount = 256;
        private const uint Magic = 0x31424348; // HCB1 in little-endian form.
        private const ushort Version = 1;
        private const ushort Flags = 0;
        private const int MaxStringBytes = 32 * 1024;
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(HybridCLRBundle bundle)
        {
            ValidateBundle(bundle);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, s_Utf8, true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(Flags);
                writer.Write(bundle.BundleId.ToByteArray());
                WriteString(writer, bundle.Target);
                WriteString(writer, bundle.EntryCommandId);
                writer.Write((ushort)bundle.Artifacts.Count);
                foreach (HybridCLRBundleArtifact artifact in bundle.Artifacts)
                {
                    WriteString(writer, artifact.Name);
                    writer.Write(artifact.Dll.Length);
                    writer.Write(artifact.Pdb.Length);
                    writer.Write(ComputeHash(artifact.Dll));
                    if (artifact.Pdb.Length > 0) writer.Write(ComputeHash(artifact.Pdb));
                    writer.Write(artifact.Dll);
                    if (artifact.Pdb.Length > 0) writer.Write(artifact.Pdb);
                    EnsureEnvelopeLimit(stream.Length);
                }
                byte[] bytes = stream.ToArray();
                EnsureEnvelopeLimit(bytes.LongLength);
                return bytes;
            }
        }

        public static HybridCLRBundle Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            try { return DecodeCore(payload); }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("HybridCLR bundle is truncated.", exception);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("HybridCLR bundle contains overflowing lengths.", exception);
            }
        }

        private static HybridCLRBundle DecodeCore(byte[] payload)
        {
            EnsureEnvelopeLimit(payload.LongLength);
            using (var stream = new MemoryStream(payload, false))
            using (var reader = new BinaryReader(stream, s_Utf8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid HybridCLR bundle magic.");
                if (reader.ReadUInt16() != Version) throw new InvalidDataException("Unsupported HybridCLR bundle version.");
                if (reader.ReadUInt16() != Flags) throw new InvalidDataException("Unsupported HybridCLR bundle flags.");
                byte[] id = ReadExactly(reader, 16);
                Guid bundleId = new Guid(id);
                string target = ReadString(reader);
                string entryCommandId = ReadString(reader);
                int count = reader.ReadUInt16();
                if (count < 1 || count > MaxAssemblyCount) throw new InvalidDataException("Invalid HybridCLR assembly count.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                var artifacts = new List<HybridCLRBundleArtifact>(count);
                for (int i = 0; i < count; i++)
                {
                    string name = ReadString(reader);
                    if (!IsValidAssemblyName(name) || !names.Add(name))
                        throw new InvalidDataException("HybridCLR assembly names must be valid, simple and unique.");
                    int dllLength = reader.ReadInt32();
                    int pdbLength = reader.ReadInt32();
                    if (dllLength <= 0 || pdbLength < 0)
                        throw new InvalidDataException("Invalid HybridCLR artifact length.");
                    long artifactLength = checked((long)dllLength + pdbLength);
                    int hashLength = pdbLength > 0 ? 64 : 32;
                    long remaining = stream.Length - stream.Position;
                    long required = checked(artifactLength + hashLength);
                    if (remaining < 0 || required > remaining)
                        throw new InvalidDataException("HybridCLR bundle is truncated.");
                    byte[] dllHash = ReadExactly(reader, 32);
                    byte[] pdbHash = pdbLength > 0 ? ReadExactly(reader, 32) : Array.Empty<byte>();
                    byte[] dll = ReadExactly(reader, dllLength);
                    byte[] pdb = pdbLength > 0 ? ReadExactly(reader, pdbLength) : Array.Empty<byte>();
                    if (!RemoteExecutionProtocol.FixedTimeEquals(ComputeHash(dll), dllHash) ||
                        (pdbLength > 0 && !RemoteExecutionProtocol.FixedTimeEquals(ComputeHash(pdb), pdbHash)))
                        throw new InvalidDataException($"HybridCLR artifact hash does not match: {name}");
                    artifacts.Add(new HybridCLRBundleArtifact(name, dll, pdb));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing HybridCLR bundle bytes.");
                var bundle = new HybridCLRBundle(bundleId, target, entryCommandId, artifacts);
                ValidateBundle(bundle);
                return bundle;
            }
        }

        private static void ValidateBundle(HybridCLRBundle bundle)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            if (string.IsNullOrWhiteSpace(bundle.Target)) throw new InvalidDataException("HybridCLR target is required.");
            if (string.IsNullOrWhiteSpace(bundle.EntryCommandId)) throw new InvalidDataException("HybridCLR entry command is required.");
            if (bundle.Artifacts == null || bundle.Artifacts.Count < 1 || bundle.Artifacts.Count > MaxAssemblyCount)
                throw new InvalidDataException($"HybridCLR bundle must contain 1..{MaxAssemblyCount} assemblies.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (HybridCLRBundleArtifact artifact in bundle.Artifacts)
            {
                if (artifact == null || !IsValidAssemblyName(artifact.Name) || !names.Add(artifact.Name) ||
                    artifact.Dll == null || artifact.Dll.Length == 0 || artifact.Pdb == null)
                    throw new InvalidDataException("Invalid HybridCLR bundle artifact.");
                ValidateString(artifact.Name);
            }
            ValidateString(bundle.Target);
            ValidateString(bundle.EntryCommandId);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = s_Utf8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxStringBytes) throw new InvalidDataException("HybridCLR bundle string is too long.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new InvalidDataException("HybridCLR bundle string is too long.");
            return s_Utf8.GetString(ReadExactly(reader, length));
        }

        private static byte[] ReadExactly(BinaryReader reader, int length)
        {
            if (length < 0) throw new InvalidDataException("Invalid HybridCLR bundle length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("HybridCLR bundle is truncated.");
            return bytes;
        }

        private static byte[] ComputeHash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        private static bool IsValidAssemblyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == "..") return false;
            foreach (char character in value)
            {
                if (char.IsControl(character) || character == '/' || character == '\\' ||
                    character == ':' || character == ',' || character == '=') return false;
            }
            return true;
        }

        private static void ValidateString(string value)
        {
            if (s_Utf8.GetByteCount(value ?? string.Empty) > MaxStringBytes)
                throw new InvalidDataException("HybridCLR bundle string is too long.");
        }

        private static void EnsureEnvelopeLimit(long length)
        {
            if (length < 0 || length > MaxEnvelopeBytes)
                throw new InvalidDataException($"HybridCLR bundle exceeds {MaxEnvelopeBytes} bytes.");
        }
    }
}
