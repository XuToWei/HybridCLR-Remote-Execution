using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HybridCLR.RemoteExecution
{
    public enum RemoteMessageKind : byte
    {
        Hello = 1,
        Challenge = 2,
        Authenticate = 3,
        Ready = 4,
        Error = 5,
        ListMethods = 6,
        Methods = 7,
        Invoke = 8,
        InvokeResult = 9,
        LoadManifest = 10,
        AssemblyBegin = 11,
        AssemblyChunk = 12,
        AssemblyEnd = 13,
        ApplyResult = 14,
        Ping = 15,
        Pong = 16
    }

    public sealed class RemoteFrame
    {
        public RemoteFrame(RemoteMessageKind kind, Guid requestId, byte[] payload)
        {
            Kind = kind;
            RequestId = requestId;
            Payload = payload ?? Array.Empty<byte>();
        }

        public RemoteMessageKind Kind { get; }
        public Guid RequestId { get; }
        public byte[] Payload { get; }
    }

    public sealed class RemoteHello
    {
        public string ClientId;
        public string Target;
        public string UnityVersion;
        public string HybridCLRVersion;
    }

    public sealed class RemoteAssemblyInfo
    {
        public string Name;
        public long DllLength;
        public long PdbLength;
        public byte[] DllSha256;
        public byte[] PdbSha256;
    }

    public sealed class RemoteBundleManifest
    {
        public Guid BundleId;
        public string Generation;
        public string Target;
        public RemoteAssemblyInfo[] Assemblies = Array.Empty<RemoteAssemblyInfo>();
    }

    public sealed class RemoteMethodInfo
    {
        public string Id;
        public string Description;
        public int TimeoutSeconds;
    }

    public sealed class RemoteError
    {
        public string Code;
        public string Message;
    }

    public static class RemoteExecutionProtocol
    {
        public const ushort Version = 1;
        public const int HeaderLength = 28;
        public const int MaxFramePayload = 1024 * 1024;
        public const int DefaultMaxBundleBytes = 128 * 1024 * 1024;
        public const int MaxChunkBytes = 60 * 1024;
        public const int MaxStringBytes = 32 * 1024;
        private const uint Magic = 0x31524847; // GHR1 in little-endian form.

        public static async Task<RemoteFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[HeaderLength];
            await ReadExactlyAsync(stream, header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            if (ReadUInt32(header, 0) != Magic)
                throw new InvalidDataException("Invalid remote execution frame magic.");
            if (ReadUInt16(header, 4) != Version)
                throw new InvalidDataException("Unsupported remote execution protocol version.");
            if (header[7] != 0)
                throw new InvalidDataException("Unsupported remote execution frame flags.");

            int payloadLength = checked((int)ReadUInt32(header, 24));
            if (payloadLength < 0 || payloadLength > MaxFramePayload)
                throw new InvalidDataException($"Frame payload exceeds {MaxFramePayload} bytes.");
            byte[] requestId = new byte[16];
            Buffer.BlockCopy(header, 8, requestId, 0, requestId.Length);
            byte[] payload = new byte[payloadLength];
            await ReadExactlyAsync(stream, payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
            return new RemoteFrame((RemoteMessageKind)header[6], new Guid(requestId), payload);
        }

        public static async Task WriteFrameAsync(Stream stream, RemoteFrame frame, CancellationToken cancellationToken)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Payload.Length > MaxFramePayload)
                throw new InvalidDataException($"Frame payload exceeds {MaxFramePayload} bytes.");
            byte[] header = new byte[HeaderLength];
            WriteUInt32(header, 0, Magic);
            WriteUInt16(header, 4, Version);
            header[6] = (byte)frame.Kind;
            Buffer.BlockCopy(frame.RequestId.ToByteArray(), 0, header, 8, 16);
            WriteUInt32(header, 24, checked((uint)frame.Payload.Length));
            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            if (frame.Payload.Length != 0)
                await stream.WriteAsync(frame.Payload, 0, frame.Payload.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static byte[] EncodeHello(RemoteHello hello)
        {
            if (hello == null) throw new ArgumentNullException(nameof(hello));
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                WriteString(writer, hello.ClientId);
                WriteString(writer, hello.Target);
                WriteString(writer, hello.UnityVersion);
                WriteString(writer, hello.HybridCLRVersion);
                return stream.ToArray();
            }
        }

        public static RemoteHello DecodeHello(byte[] payload)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                var hello = new RemoteHello
                {
                    ClientId = ReadString(reader),
                    Target = ReadString(reader),
                    UnityVersion = ReadString(reader),
                    HybridCLRVersion = ReadString(reader)
                };
                EnsureEnd(stream);
                return hello;
            }
        }

        public static byte[] EncodeChallenge(byte[] nonce)
        {
            if (nonce == null || nonce.Length < 16 || nonce.Length > 64)
                throw new InvalidDataException("Challenge nonce must contain 16..64 bytes.");
            return (byte[])nonce.Clone();
        }

        public static byte[] DecodeChallenge(byte[] payload)
        {
            if (payload == null || payload.Length < 16 || payload.Length > 64)
                throw new InvalidDataException("Invalid challenge nonce.");
            return (byte[])payload.Clone();
        }

        public static byte[] ComputeAuthentication(byte[] nonce, string token)
        {
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (string.IsNullOrEmpty(token)) throw new InvalidDataException("Authentication token is required.");
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token)))
                return hmac.ComputeHash(nonce);
        }

        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        public static byte[] EncodeManifest(RemoteBundleManifest manifest)
        {
            if (manifest == null || manifest.Assemblies == null || manifest.Assemblies.Length == 0 || manifest.Assemblies.Length > 256)
                throw new InvalidDataException("A bundle must contain 1..256 assemblies.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write(manifest.BundleId.ToByteArray());
                WriteString(writer, manifest.Generation);
                WriteString(writer, manifest.Target);
                writer.Write((ushort)manifest.Assemblies.Length);
                long total = 0;
                foreach (var assembly in manifest.Assemblies)
                {
                    ValidateAssemblyInfo(assembly);
                    total = checked(total + assembly.DllLength + assembly.PdbLength);
                    if (total > DefaultMaxBundleBytes) throw new InvalidDataException("Bundle is too large.");
                    WriteString(writer, assembly.Name);
                    writer.Write(assembly.DllLength);
                    writer.Write(assembly.PdbLength);
                    writer.Write(assembly.DllSha256);
                    if (assembly.PdbLength > 0) writer.Write(assembly.PdbSha256);
                }
                return stream.ToArray();
            }
        }

        public static RemoteBundleManifest DecodeManifest(byte[] payload)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                byte[] bundleId = reader.ReadBytes(16);
                if (bundleId.Length != 16) throw new EndOfStreamException();
                var manifest = new RemoteBundleManifest
                {
                    BundleId = new Guid(bundleId),
                    Generation = ReadString(reader),
                    Target = ReadString(reader)
                };
                int count = reader.ReadUInt16();
                if (count < 1 || count > 256) throw new InvalidDataException("Invalid assembly count.");
                manifest.Assemblies = new RemoteAssemblyInfo[count];
                long total = 0;
                for (int i = 0; i < count; i++)
                {
                    var assembly = new RemoteAssemblyInfo
                    {
                        Name = ReadString(reader),
                        DllLength = reader.ReadInt64(),
                        PdbLength = reader.ReadInt64(),
                        DllSha256 = reader.ReadBytes(32)
                    };
                    if (assembly.DllSha256.Length != 32) throw new InvalidDataException("Invalid DLL hash.");
                    assembly.PdbSha256 = assembly.PdbLength > 0 ? reader.ReadBytes(32) : Array.Empty<byte>();
                    ValidateAssemblyInfo(assembly);
                    total = checked(total + assembly.DllLength + assembly.PdbLength);
                    if (total > DefaultMaxBundleBytes) throw new InvalidDataException("Bundle is too large.");
                    manifest.Assemblies[i] = assembly;
                }
                EnsureEnd(stream);
                return manifest;
            }
        }

        public static byte[] EncodeAssemblyBegin(Guid bundleId, int assemblyIndex, bool pdb, long length, byte[] sha256)
        {
            if (sha256 == null || sha256.Length != 32 || assemblyIndex < 0 || length < 0)
                throw new InvalidDataException("Invalid assembly begin message.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write(bundleId.ToByteArray());
                writer.Write(assemblyIndex);
                writer.Write(pdb);
                writer.Write(length);
                writer.Write(sha256);
                return stream.ToArray();
            }
        }

        public static void DecodeAssemblyBegin(byte[] payload, out Guid bundleId, out int assemblyIndex, out bool pdb, out long length, out byte[] sha256)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                byte[] id = reader.ReadBytes(16);
                if (id.Length != 16) throw new EndOfStreamException();
                bundleId = new Guid(id);
                assemblyIndex = reader.ReadInt32();
                pdb = reader.ReadBoolean();
                length = reader.ReadInt64();
                sha256 = reader.ReadBytes(32);
                if (assemblyIndex < 0 || length < 0 || sha256.Length != 32) throw new InvalidDataException("Invalid assembly begin message.");
                EnsureEnd(stream);
            }
        }

        public static byte[] EncodeChunk(Guid bundleId, int assemblyIndex, bool pdb, long offset, byte[] data, int count)
        {
            if (data == null || count < 0 || count > MaxChunkBytes || count > data.Length || offset < 0)
                throw new InvalidDataException("Invalid assembly chunk.");
            using (var stream = new MemoryStream(37 + count))
            using (var writer = NewWriter(stream))
            {
                writer.Write(bundleId.ToByteArray());
                writer.Write(assemblyIndex);
                writer.Write(pdb);
                writer.Write(offset);
                writer.Write(count);
                writer.Write(data, 0, count);
                return stream.ToArray();
            }
        }

        public static void DecodeChunk(byte[] payload, out Guid bundleId, out int assemblyIndex, out bool pdb, out long offset, out byte[] data)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                byte[] id = reader.ReadBytes(16);
                if (id.Length != 16) throw new EndOfStreamException();
                bundleId = new Guid(id);
                assemblyIndex = reader.ReadInt32();
                pdb = reader.ReadBoolean();
                offset = reader.ReadInt64();
                int count = reader.ReadInt32();
                if (assemblyIndex < 0 || offset < 0 || count < 0 || count > MaxChunkBytes || count > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid assembly chunk bounds.");
                data = reader.ReadBytes(count);
                if (data.Length != count) throw new EndOfStreamException();
                EnsureEnd(stream);
            }
        }

        public static byte[] EncodeAssemblyEnd(Guid bundleId, int assemblyIndex, bool pdb)
        {
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write(bundleId.ToByteArray());
                writer.Write(assemblyIndex);
                writer.Write(pdb);
                return stream.ToArray();
            }
        }

        public static void DecodeAssemblyEnd(byte[] payload, out Guid bundleId, out int assemblyIndex, out bool pdb)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                byte[] id = reader.ReadBytes(16);
                if (id.Length != 16) throw new EndOfStreamException();
                bundleId = new Guid(id);
                assemblyIndex = reader.ReadInt32();
                pdb = reader.ReadBoolean();
                EnsureEnd(stream);
            }
        }

        public static byte[] EncodeMethods(IReadOnlyList<RemoteMethodInfo> methods)
        {
            if (methods == null || methods.Count > ushort.MaxValue) throw new InvalidDataException("Too many methods.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write((ushort)methods.Count);
                foreach (var method in methods)
                {
                    WriteString(writer, method.Id);
                    WriteString(writer, method.Description);
                    writer.Write(method.TimeoutSeconds);
                }
                return stream.ToArray();
            }
        }

        public static RemoteMethodInfo[] DecodeMethods(byte[] payload)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                int count = reader.ReadUInt16();
                var methods = new RemoteMethodInfo[count];
                for (int i = 0; i < count; i++)
                {
                    methods[i] = new RemoteMethodInfo
                    {
                        Id = ReadString(reader),
                        Description = ReadString(reader),
                        TimeoutSeconds = reader.ReadInt32()
                    };
                }
                EnsureEnd(stream);
                return methods;
            }
        }

        public static byte[] EncodeInvoke(string methodId)
        {
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                WriteString(writer, methodId);
                return stream.ToArray();
            }
        }

        public static string DecodeInvoke(byte[] payload)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                string result = ReadString(reader);
                EnsureEnd(stream);
                return result;
            }
        }

        public static byte[] EncodeResult(bool succeeded, string code, string message)
        {
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write(succeeded);
                WriteString(writer, code);
                WriteString(writer, message);
                return stream.ToArray();
            }
        }

        public static void DecodeResult(byte[] payload, out bool succeeded, out string code, out string message)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                succeeded = reader.ReadBoolean();
                code = ReadString(reader);
                message = ReadString(reader);
                EnsureEnd(stream);
            }
        }

        public static byte[] EncodeError(string code, string message) => EncodeResult(false, code, message);

        public static RemoteError DecodeError(byte[] payload)
        {
            DecodeResult(payload, out _, out string code, out string message);
            return new RemoteError { Code = code, Message = message };
        }

        private static void ValidateAssemblyInfo(RemoteAssemblyInfo assembly)
        {
            if (assembly == null || string.IsNullOrEmpty(assembly.Name) || assembly.DllLength <= 0 || assembly.DllLength > DefaultMaxBundleBytes ||
                assembly.PdbLength < 0 || assembly.PdbLength > DefaultMaxBundleBytes || assembly.DllSha256 == null || assembly.DllSha256.Length != 32 ||
                (assembly.PdbLength > 0 && (assembly.PdbSha256 == null || assembly.PdbSha256.Length != 32)))
                throw new InvalidDataException("Invalid assembly manifest entry.");
        }

        private static BinaryWriter NewWriter(Stream stream) => new BinaryWriter(stream, Encoding.UTF8, true);
        private static BinaryReader NewReader(Stream stream) => new BinaryReader(stream, Encoding.UTF8, true);
        private static MemoryStream NewStream(byte[] data) => new MemoryStream(data ?? throw new ArgumentNullException(nameof(data)), false);

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > MaxStringBytes) throw new InvalidDataException("Remote execution string is too long.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length > MaxStringBytes) throw new InvalidDataException("Remote execution string is too long.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static void EnsureEnd(Stream stream)
        {
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing bytes.");
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                int read = await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Remote execution peer disconnected.");
                offset += read;
                count -= read;
            }
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) => (ushort)(buffer[offset] | buffer[offset + 1] << 8);
        private static uint ReadUInt32(byte[] buffer, int offset) => (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
        private static void WriteUInt16(byte[] buffer, int offset, ushort value) { buffer[offset] = (byte)value; buffer[offset + 1] = (byte)(value >> 8); }
        private static void WriteUInt32(byte[] buffer, int offset, uint value) { buffer[offset] = (byte)value; buffer[offset + 1] = (byte)(value >> 8); buffer[offset + 2] = (byte)(value >> 16); buffer[offset + 3] = (byte)(value >> 24); }
    }
}
