using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public enum RemoteMessageKind : byte
    {
        Hello = 1,
        Ready = 2,
        Error = 3,
        Ping = 4,
        Pong = 5,
        ListCommands = 6,
        Commands = 7,
        CommandInputBegin = 8,
        CommandInputChunk = 9,
        CommandInputEnd = 10,
        CommandResult = 11,
        CommandResultChunk = 12,
        CommandResultEnd = 13,
        CancelCommand = 14
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
        public string RuntimeVersion;
    }

    public sealed class RemoteCommandInfo
    {
        public string Id;
        public string Name;
        public string Description;
        public string Category;
        public int TimeoutSeconds;
        public int MaxRequestBytes;
        public int MaxResponseBytes;
        public string RequestContentType;
        public string ResponseContentType;
        public bool Executable;
        public bool RequiresMainThread;
    }

    public sealed class RemoteError
    {
        public string Code;
        public string Message;
    }

    public static class RemoteExecutionProtocol
    {
        public const ushort Version = 3;
        public const int HeaderLength = 28;
        public const int MaxFramePayload = 1024 * 1024;
        public const int MaxChunkBytes = 60 * 1024;
        public const int MaxStringBytes = 32 * 1024;
        public const int MaxCommandRequestBytes = 128 * 1024 * 1024;
        public const int MaxCommandResponseBytes = 64 * 1024 * 1024;
        public const int DefaultMaxCommandRequestBytes = 16 * 1024 * 1024;
        public const int DefaultMaxCommandResponseBytes = 16 * 1024 * 1024;
        private const uint Magic = 0x33585255; // URX3 in little-endian form.
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

        public static async Task<RemoteFrame> ReadFrameAsync(Stream stream,
            CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            byte[] header = new byte[HeaderLength];
            await ReadExactlyAsync(stream, header, 0, header.Length, cancellationToken)
                .ConfigureAwait(false);
            int payloadLength = ValidateHeader(header);
            byte[] payload = new byte[payloadLength];
            await ReadExactlyAsync(stream, payload, 0, payload.Length, cancellationToken)
                .ConfigureAwait(false);
            return DecodeFrameParts(header, payload);
        }

        public static async Task WriteFrameAsync(Stream stream, RemoteFrame frame,
            CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            ValidateFrame(frame);
            byte[] header = EncodeFrameHeader(frame);
            await stream.WriteAsync(header, 0, header.Length, cancellationToken)
                .ConfigureAwait(false);
            if (frame.Payload.Length != 0)
                await stream.WriteAsync(frame.Payload, 0, frame.Payload.Length,
                    cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static void ValidateFrame(RemoteFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (!Enum.IsDefined(typeof(RemoteMessageKind), frame.Kind))
                throw new InvalidDataException("Unknown remote execution message kind.");
            if (frame.Payload == null)
                throw new InvalidDataException("Remote execution frame payload is required.");
            if (frame.Payload.Length > MaxFramePayload)
                throw new InvalidDataException(
                    $"Frame payload exceeds {MaxFramePayload} bytes.");
        }

        public static byte[] EncodeFrame(RemoteFrame frame)
        {
            ValidateFrame(frame);
            byte[] bytes = new byte[HeaderLength + frame.Payload.Length];
            byte[] header = EncodeFrameHeader(frame);
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            if (frame.Payload.Length != 0)
                Buffer.BlockCopy(frame.Payload, 0, bytes, HeaderLength,
                    frame.Payload.Length);
            return bytes;
        }

        public static RemoteFrame DecodeFrame(byte[] frameBytes)
        {
            if (frameBytes == null) throw new ArgumentNullException(nameof(frameBytes));
            if (frameBytes.Length < HeaderLength)
                throw new InvalidDataException("Remote execution frame is truncated.");
            byte[] header = new byte[HeaderLength];
            Buffer.BlockCopy(frameBytes, 0, header, 0, HeaderLength);
            int payloadLength = ValidateHeader(header);
            if (frameBytes.Length != HeaderLength + payloadLength)
                throw new InvalidDataException(
                    frameBytes.Length < HeaderLength + payloadLength
                        ? "Remote execution frame is truncated."
                        : "Unexpected trailing remote execution frame bytes.");
            byte[] payload = new byte[payloadLength];
            if (payloadLength != 0)
                Buffer.BlockCopy(frameBytes, HeaderLength, payload, 0, payloadLength);
            return DecodeFrameParts(header, payload);
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
                WriteString(writer, hello.RuntimeVersion);
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
                    RuntimeVersion = ReadString(reader)
                };
                EnsureEnd(stream);
                if (string.IsNullOrWhiteSpace(hello.ClientId) ||
                    string.IsNullOrWhiteSpace(hello.Target))
                    throw new InvalidDataException("Hello client ID and target are required.");
                return hello;
            }
        }

        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        public static byte[] EncodeCommands(IReadOnlyList<RemoteCommandInfo> commands)
        {
            if (commands == null || commands.Count > ushort.MaxValue) throw new InvalidDataException("Too many commands.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write((ushort)commands.Count);
                foreach (RemoteCommandInfo command in commands)
                {
                    ValidateCommandInfo(command);
                    WriteString(writer, command.Id);
                    WriteString(writer, command.Name);
                    WriteString(writer, command.Description);
                    WriteString(writer, command.Category);
                    writer.Write(command.TimeoutSeconds);
                    writer.Write(command.MaxRequestBytes);
                    writer.Write(command.MaxResponseBytes);
                    WriteString(writer, command.RequestContentType);
                    WriteString(writer, command.ResponseContentType);
                    writer.Write(command.Executable);
                    writer.Write(command.RequiresMainThread);
                }
                byte[] result = stream.ToArray();
                if (result.Length > MaxFramePayload) throw new InvalidDataException("Command catalog is too large.");
                return result;
            }
        }

        public static RemoteCommandInfo[] DecodeCommands(byte[] payload)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                int count = reader.ReadUInt16();
                var commands = new RemoteCommandInfo[count];
                for (int i = 0; i < count; i++)
                {
                    commands[i] = new RemoteCommandInfo
                    {
                        Id = ReadString(reader),
                        Name = ReadString(reader),
                        Description = ReadString(reader),
                        Category = ReadString(reader),
                        TimeoutSeconds = reader.ReadInt32(),
                        MaxRequestBytes = reader.ReadInt32(),
                        MaxResponseBytes = reader.ReadInt32(),
                        RequestContentType = ReadString(reader),
                        ResponseContentType = ReadString(reader),
                        Executable = reader.ReadBoolean(),
                        RequiresMainThread = reader.ReadBoolean()
                    };
                    ValidateCommandInfo(commands[i]);
                }
                EnsureEnd(stream);
                return commands;
            }
        }

        public static byte[] EncodeCommandInputBegin(string commandId, string contentType, long length, byte[] sha256)
        {
            ValidateCommandInputIdentity(commandId, contentType);
            if (length < 0 || length > MaxCommandRequestBytes || sha256 == null || sha256.Length != 32)
                throw new InvalidDataException("Invalid command input metadata.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                WriteString(writer, commandId);
                WriteString(writer, contentType);
                writer.Write(length);
                writer.Write(sha256);
                return stream.ToArray();
            }
        }

        public static void DecodeCommandInputBegin(byte[] payload, out string commandId, out string contentType, out long length, out byte[] sha256)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                commandId = ReadString(reader);
                contentType = ReadString(reader);
                length = reader.ReadInt64();
                sha256 = reader.ReadBytes(32);
                if (length < 0 || length > MaxCommandRequestBytes || sha256.Length != 32)
                    throw new InvalidDataException("Invalid command input metadata.");
                EnsureEnd(stream);
                ValidateCommandInputIdentity(commandId, contentType);
            }
        }

        public static byte[] EncodeCommandChunk(long offset, byte[] data, int dataOffset, int count)
        {
            if (data == null || offset < 0 || dataOffset < 0 || count < 0 ||
                count > MaxChunkBytes || dataOffset > data.Length - count)
                throw new InvalidDataException("Invalid command chunk.");
            using (var stream = new MemoryStream(12 + count))
            using (var writer = NewWriter(stream))
            {
                writer.Write(offset);
                writer.Write(count);
                writer.Write(data, dataOffset, count);
                return stream.ToArray();
            }
        }

        public static void DecodeCommandChunk(byte[] payload, out long offset, out byte[] data)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                offset = reader.ReadInt64();
                int count = reader.ReadInt32();
                if (offset < 0 || count < 0 || count > MaxChunkBytes || count > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid command chunk bounds.");
                data = reader.ReadBytes(count);
                if (data.Length != count) throw new EndOfStreamException();
                EnsureEnd(stream);
            }
        }

        public static byte[] EncodeCommandEnd() => Array.Empty<byte>();

        public static void DecodeCommandEnd(byte[] payload)
        {
            if (payload == null || payload.Length != 0) throw new InvalidDataException("Invalid command end message.");
        }

        public static byte[] EncodeCommandResult(bool succeeded, string code, string message, string contentType, byte[] payload)
        {
            ValidateCommandResult(succeeded, code, message, contentType);
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length > MaxCommandResponseBytes) throw new InvalidDataException("Command result exceeds the maximum size.");
            using (var stream = new MemoryStream())
            using (var writer = NewWriter(stream))
            {
                writer.Write(succeeded);
                WriteString(writer, code);
                WriteString(writer, message);
                WriteString(writer, contentType);
                writer.Write((long)payload.Length);
                if (payload.Length > 0)
                {
                    using (var sha = SHA256.Create()) writer.Write(sha.ComputeHash(payload));
                }
                byte[] result = stream.ToArray();
                if (result.Length > MaxFramePayload) throw new InvalidDataException("Command result metadata is too large.");
                return result;
            }
        }

        public static void DecodeCommandResult(byte[] payload, out bool succeeded, out string code, out string message,
            out string contentType, out long length, out byte[] sha256)
        {
            using (var stream = NewStream(payload))
            using (var reader = NewReader(stream))
            {
                succeeded = reader.ReadBoolean();
                code = ReadString(reader);
                message = ReadString(reader);
                contentType = ReadString(reader);
                length = reader.ReadInt64();
                if (length < 0 || length > MaxCommandResponseBytes) throw new InvalidDataException("Invalid command result length.");
                sha256 = length > 0 ? reader.ReadBytes(32) : Array.Empty<byte>();
                if (length > 0 && sha256.Length != 32) throw new InvalidDataException("Invalid command result hash.");
                EnsureEnd(stream);
                ValidateCommandResult(succeeded, code, message, contentType);
            }
        }

        public static byte[] EncodeCommandResultChunk(long offset, byte[] data,
            int dataOffset, int count)
        {
            return EncodeCommandChunk(offset, data, dataOffset, count);
        }

        public static void DecodeCommandResultChunk(byte[] payload, out long offset, out byte[] data)
        {
            DecodeCommandChunk(payload, out offset, out data);
        }

        public static byte[] EncodeCommandResultEnd() => Array.Empty<byte>();

        public static void DecodeCommandResultEnd(byte[] payload)
        {
            DecodeCommandEnd(payload);
        }

        public static byte[] EncodeResult(bool succeeded, string code, string message)
        {
            if (!IsValidString(code) || !IsValidString(message) ||
                (!succeeded && string.IsNullOrWhiteSpace(code)))
                throw new InvalidDataException("Invalid result metadata.");
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
                if (!IsValidString(code) || !IsValidString(message) ||
                    (!succeeded && string.IsNullOrWhiteSpace(code)))
                    throw new InvalidDataException("Invalid result metadata.");
            }
        }

        public static byte[] EncodeError(string code, string message) => EncodeResult(false, code, message);

        public static RemoteError DecodeError(byte[] payload)
        {
            DecodeResult(payload, out _, out string code, out string message);
            return new RemoteError { Code = code, Message = message };
        }

        private static byte[] EncodeFrameHeader(RemoteFrame frame)
        {
            byte[] header = new byte[HeaderLength];
            WriteUInt32(header, 0, Magic);
            WriteUInt16(header, 4, Version);
            header[6] = (byte)frame.Kind;
            Buffer.BlockCopy(frame.RequestId.ToByteArray(), 0, header, 8, 16);
            WriteUInt32(header, 24, checked((uint)frame.Payload.Length));
            return header;
        }

        private static int ValidateHeader(byte[] header)
        {
            if (header == null || header.Length != HeaderLength)
                throw new InvalidDataException("Invalid remote execution frame header.");
            if (ReadUInt32(header, 0) != Magic)
                throw new InvalidDataException("Invalid remote execution frame magic.");
            if (ReadUInt16(header, 4) != Version)
                throw new InvalidDataException("Unsupported remote execution protocol version.");
            if (header[7] != 0)
                throw new InvalidDataException("Unsupported remote execution frame flags.");
            RemoteMessageKind kind = (RemoteMessageKind)header[6];
            if (!Enum.IsDefined(typeof(RemoteMessageKind), kind))
                throw new InvalidDataException("Unknown remote execution message kind.");
            uint encodedLength = ReadUInt32(header, 24);
            if (encodedLength > MaxFramePayload)
                throw new InvalidDataException(
                    $"Frame payload exceeds {MaxFramePayload} bytes.");
            return checked((int)encodedLength);
        }

        private static RemoteFrame DecodeFrameParts(byte[] header, byte[] payload)
        {
            byte[] requestId = new byte[16];
            Buffer.BlockCopy(header, 8, requestId, 0, requestId.Length);
            var frame = new RemoteFrame((RemoteMessageKind)header[6],
                new Guid(requestId), payload);
            ValidateFrame(frame);
            return frame;
        }

        private static void ValidateCommandInfo(RemoteCommandInfo command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.Id) ||
                string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.Description) ||
                !IsValidString(command.Id) || !IsValidString(command.Name) ||
                !IsValidString(command.Description) || !IsValidString(command.Category) ||
                !IsValidString(command.RequestContentType) || !IsValidString(command.ResponseContentType) ||
                command.MaxRequestBytes < 0 || command.MaxRequestBytes > MaxCommandRequestBytes ||
                command.MaxResponseBytes < 0 || command.MaxResponseBytes > MaxCommandResponseBytes ||
                command.TimeoutSeconds < 1 || command.TimeoutSeconds > 3600)
                throw new InvalidDataException("Invalid command metadata.");
        }

        private static bool IsValidString(string value)
        {
            try { return s_Utf8.GetByteCount(value ?? string.Empty) <= MaxStringBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        private static void ValidateCommandInputIdentity(string commandId, string contentType)
        {
            if (string.IsNullOrWhiteSpace(commandId) || !IsValidString(commandId) ||
                !IsValidString(contentType))
                throw new InvalidDataException("Invalid command ID or content type.");
        }

        private static void ValidateCommandResult(bool succeeded, string code, string message,
            string contentType)
        {
            if (!IsValidString(code) || !IsValidString(message) || !IsValidString(contentType) ||
                (!succeeded && string.IsNullOrWhiteSpace(code)))
                throw new InvalidDataException("Invalid command result metadata.");
        }

        private static BinaryWriter NewWriter(Stream stream) => new BinaryWriter(stream, s_Utf8, true);
        private static BinaryReader NewReader(Stream stream) => new BinaryReader(stream, s_Utf8, true);
        private static MemoryStream NewStream(byte[] data) => new MemoryStream(data ?? throw new ArgumentNullException(nameof(data)), false);

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = s_Utf8.GetBytes(value ?? string.Empty);
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
            return s_Utf8.GetString(bytes);
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
