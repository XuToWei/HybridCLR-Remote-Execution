using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionPlayerCommandHost : IDisposable
    {
        private readonly List<RemoteCommandDescriptor> m_Registrations =
            new List<RemoteCommandDescriptor>();
        private readonly HashSet<Guid> m_ActiveRequestIds = new HashSet<Guid>();
        private readonly object m_CommandLock = new object();
        private readonly object m_LifecycleLock = new object();
        private bool m_Initialized;
        private bool m_Disposed;
        private long m_Generation;
        private RemoteExecutionPlayerConfiguration m_Configuration;
        private Action<RemoteMessageKind, Guid, byte[]> m_Send;
        private IncomingCommandInput m_IncomingCommandInput;
        private bool m_CommandRunning;
        private long m_RunningCommandGeneration;
        private Guid m_RunningCommandId;
        private CancellationTokenSource m_CommandCancellation;
        private DateTime m_CommandDeadlineUtc;
        private bool m_CommandCancelledRemotely;

        internal void Initialize()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(RemoteExecutionPlayerCommandHost));
            if (m_Initialized) return;
            var registered = new List<RemoteCommandDescriptor>();
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                RegisterProviders(assemblies, registered);
                m_Registrations.AddRange(registered);
                m_Initialized = true;
            }
            catch
            {
                if (registered.Count > 0) RemoteCommandRegistry.Unregister(registered);
                throw;
            }
        }

        internal void BeginConnection(long generation,
            RemoteExecutionPlayerConfiguration configuration,
            Action<RemoteMessageKind, Guid, byte[]> send)
        {
            if (!m_Initialized) throw new InvalidOperationException("Command host is not initialized.");
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (send == null) throw new ArgumentNullException(nameof(send));
            lock (m_LifecycleLock)
            {
                m_Generation = generation;
                m_Configuration = configuration;
                m_Send = send;
            }
        }

        internal void CancelConnection(long generation)
        {
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                m_Send = null;
                m_Configuration = null;
            }
            CancellationTokenSource commandCancellation = null;
            lock (m_CommandLock)
            {
                if (m_CommandRunning && m_RunningCommandGeneration == generation)
                    commandCancellation = m_CommandCancellation;
                m_ActiveRequestIds.Clear();
            }
            Cancel(commandCancellation);
            ResetCommandInput();
        }

        internal void UpdateTimeout()
        {
            CancellationTokenSource commandCancellation = null;
            lock (m_CommandLock)
            {
                if (m_CommandRunning && DateTime.UtcNow >= m_CommandDeadlineUtc)
                    commandCancellation = m_CommandCancellation;
            }
            Cancel(commandCancellation);
        }

        internal void HandleFrame(long generation, RemoteFrame frame)
        {
            lock (m_LifecycleLock)
                if (generation != m_Generation || m_Send == null) return;
            try
            {
                if (frame.RequestId == Guid.Empty &&
                    frame.Kind != RemoteMessageKind.Ping && frame.Kind != RemoteMessageKind.Pong)
                    throw new InvalidDataException("Request ID is required.");
                switch (frame.Kind)
                {
                    case RemoteMessageKind.ListCommands:
                        Send(generation, RemoteMessageKind.Commands, frame.RequestId,
                            EncodeCommands());
                        break;
                    case RemoteMessageKind.CommandInputBegin:
                        BeginCommandInput(generation, frame);
                        break;
                    case RemoteMessageKind.CommandInputChunk:
                        WriteCommandChunk(frame);
                        break;
                    case RemoteMessageKind.CommandInputEnd:
                        EndCommandInput(generation, frame);
                        break;
                    case RemoteMessageKind.CancelCommand:
                        CancelCommand(generation, frame);
                        break;
                    case RemoteMessageKind.Ping:
                        Send(generation, RemoteMessageKind.Pong, frame.RequestId,
                            Array.Empty<byte>());
                        break;
                    default:
                        Send(generation, RemoteMessageKind.Error, frame.RequestId,
                            RemoteExecutionProtocol.EncodeError("UNKNOWN_MESSAGE", frame.Kind.ToString()));
                        break;
                }
            }
            catch (Exception exception)
            {
                Guid inputRequestId = m_IncomingCommandInput?.RequestId ?? Guid.Empty;
                ResetCommandInput();
                if (inputRequestId != Guid.Empty) EndRequest(generation, inputRequestId);
                bool isCommand = IsCommandMessage(frame.Kind);
                Send(generation,
                    isCommand ? RemoteMessageKind.CommandResult : RemoteMessageKind.Error,
                    frame.RequestId,
                    isCommand
                        ? RemoteExecutionProtocol.EncodeCommandResult(false, "PROTOCOL_ERROR",
                            exception.Message, string.Empty, null)
                        : RemoteExecutionProtocol.EncodeError("PROTOCOL_ERROR", exception.Message));
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            CancelConnection(m_Generation);
            if (m_Registrations.Count == 0) return;
            try { RemoteCommandRegistry.Unregister(m_Registrations); }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Unity.RemoteExecution] registry cleanup was incomplete: {exception.Message}");
            }
            m_Registrations.Clear();
        }

        private static void RegisterProviders(IEnumerable<Assembly> assemblies,
            List<RemoteCommandDescriptor> registered)
        {
            var providerTypes = new List<Type>();
            foreach (Assembly assembly in assemblies ?? Array.Empty<Assembly>())
            {
                foreach (Type type in RemoteCommandRegistry.GetLoadableTypes(assembly))
                {
                    if (IsProviderType(type)) providerTypes.Add(type);
                }
            }
            providerTypes.Sort(CompareProviderTypes);
            foreach (Type providerType in providerTypes)
            {
                var provider = (IRemoteCommandProvider)Activator.CreateInstance(providerType);
                registered.AddRange(RemoteCommandRegistry.RegisterProvider(provider));
            }
        }

        private static bool IsProviderType(Type type)
        {
            return type != null && type.IsClass && !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                typeof(IRemoteCommandProvider).IsAssignableFrom(type) &&
                !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static int CompareProviderTypes(Type left, Type right)
        {
            int assemblyComparison = StringComparer.Ordinal.Compare(
                left.Assembly.FullName, right.Assembly.FullName);
            return assemblyComparison != 0 ? assemblyComparison :
                StringComparer.Ordinal.Compare(left.FullName ?? left.Name,
                    right.FullName ?? right.Name);
        }

        private void BeginCommandInput(long generation, RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null)
                throw new InvalidOperationException("Another transfer is active.");
            RemoteExecutionProtocol.DecodeCommandInputBegin(frame.Payload,
                out string commandId, out string contentType, out long length, out byte[] hash);
            if (!RemoteCommandRegistry.TryGet(commandId, out RemoteCommandDescriptor descriptor) ||
                !descriptor.IsExecutable)
                throw new InvalidOperationException("Command is not executable.");
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock) configuration = m_Configuration;
            if (configuration == null || length > descriptor.MaxRequestBytes ||
                length > configuration.MaxCommandRequestBytes ||
                !ContentTypeMatches(descriptor.RequestContentType, contentType))
                throw new InvalidDataException("Command input exceeds the command limits.");
            BeginRequest(generation, frame.RequestId);
            try
            {
                m_IncomingCommandInput = IncomingCommandInput.Create(generation,
                    frame.RequestId, commandId, contentType, length, hash);
            }
            catch
            {
                EndRequest(generation, frame.RequestId);
                throw;
            }
        }

        private void WriteCommandChunk(RemoteFrame frame)
        {
            EnsureCommandInput(frame.RequestId);
            RemoteExecutionProtocol.DecodeCommandChunk(frame.Payload, out long offset,
                out byte[] data);
            MemoryStream stream = m_IncomingCommandInput.CommandPayload;
            if (stream.Position != offset || stream.Length + data.Length > stream.Capacity)
                throw new InvalidDataException("Command chunk is out of order or too large.");
            stream.Write(data, 0, data.Length);
        }

        private void EndCommandInput(long generation, RemoteFrame frame)
        {
            EnsureCommandInput(frame.RequestId);
            RemoteExecutionProtocol.DecodeCommandEnd(frame.Payload);
            IncomingCommandInput input = m_IncomingCommandInput;
            m_IncomingCommandInput = null;
            byte[] bytes;
            try
            {
                if (input.CommandPayload.Length != input.Length)
                    throw new InvalidDataException("Command input length mismatch.");
                bytes = input.CommandPayload.ToArray();
                using (var sha = SHA256.Create())
                {
                    if (!RemoteExecutionProtocol.FixedTimeEquals(sha.ComputeHash(bytes), input.Hash))
                        throw new InvalidDataException("Command input hash mismatch.");
                }
            }
            catch
            {
                EndRequest(generation, frame.RequestId);
                throw;
            }
            finally { input.CommandPayload.Dispose(); }
            if (!RemoteCommandRegistry.TryGet(input.CommandId,
                out RemoteCommandDescriptor descriptor))
            {
                EndRequest(generation, frame.RequestId);
                throw new InvalidOperationException("Command is no longer registered.");
            }
            ScheduleCommand(generation, frame.RequestId, descriptor, bytes,
                input.ContentType);
        }

        private void ScheduleCommand(long generation, Guid requestId,
            RemoteCommandDescriptor descriptor, byte[] payload, string contentType)
        {
            if (descriptor.RequiresMainThread)
                _ = ExecuteCommand(generation, requestId, descriptor, payload, contentType);
            else
                _ = Task.Run(() => ExecuteCommand(generation, requestId, descriptor,
                    payload, contentType));
        }

        private async Task ExecuteCommand(long generation, Guid requestId,
            RemoteCommandDescriptor descriptor, byte[] payload, string contentType)
        {
            CancellationTokenSource commandCancellation = null;
            bool commandBusy;
            lock (m_CommandLock)
            {
                commandBusy = m_CommandRunning;
                if (!commandBusy)
                {
                    m_CommandRunning = true;
                    m_RunningCommandGeneration = generation;
                    m_RunningCommandId = requestId;
                    commandCancellation = new CancellationTokenSource();
                    m_CommandCancellation = commandCancellation;
                    m_CommandDeadlineUtc = DateTime.UtcNow.AddSeconds(descriptor.TimeoutSeconds);
                    m_CommandCancelledRemotely = false;
                }
            }
            if (commandBusy)
            {
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, "COMMAND_BUSY",
                        "Another command is running.", string.Empty, null));
                EndRequest(generation, requestId);
                return;
            }
            try
            {
                var context = new RemoteCommandContext(descriptor.Id, "Editor", payload,
                    contentType, commandCancellation.Token);
                Task<RemoteCommandResult> execution = RemoteCommandRegistry.ExecuteAsync(
                    descriptor, context, commandCancellation.Token);
                if (descriptor.RequiresMainThread && !execution.IsCompleted)
                    Debug.LogWarning($"[Unity.RemoteExecution] command '{descriptor.Id}' continued asynchronously; " +
                        "code after the first await is not guaranteed to run on the Unity main thread.");
                RemoteCommandResult result = await execution.ConfigureAwait(false);
                commandCancellation.Token.ThrowIfCancellationRequested();
                SendCommandResult(generation, requestId, result);
            }
            catch (OperationCanceledException)
            {
                bool cancelledRemotely;
                lock (m_CommandLock) cancelledRemotely = m_CommandCancelledRemotely;
                string code = cancelledRemotely ? "COMMAND_CANCELLED" : "COMMAND_TIMED_OUT";
                string message = cancelledRemotely ? "Command was cancelled." : "Command timed out.";
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, code, message,
                        string.Empty, null));
            }
            catch (Exception exception)
            {
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false,
                        "COMMAND_EXECUTION_FAILED", exception.Message, string.Empty, null));
            }
            finally
            {
                lock (m_CommandLock)
                {
                    if (ReferenceEquals(m_CommandCancellation, commandCancellation))
                    {
                        m_CommandCancellation = null;
                        m_RunningCommandGeneration = 0;
                        m_RunningCommandId = Guid.Empty;
                        m_CommandDeadlineUtc = default(DateTime);
                        m_CommandCancelledRemotely = false;
                        m_CommandRunning = false;
                    }
                }
                commandCancellation.Dispose();
                EndRequest(generation, requestId);
            }
        }

        private void SendCommandResult(long generation, Guid requestId,
            RemoteCommandResult result)
        {
            byte[] payload = result.Payload ?? Array.Empty<byte>();
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                configuration = m_Configuration;
            }
            if (configuration == null) return;
            if (payload.Length > configuration.MaxCommandResponseBytes)
                throw new InvalidDataException(
                    "Command result exceeds the configured response limit.");
            Send(generation, RemoteMessageKind.CommandResult, requestId,
                RemoteExecutionProtocol.EncodeCommandResult(result.Succeeded, result.Code,
                    result.Message, result.ContentType, payload));
            if (payload.Length == 0) return;
            for (int offset = 0; offset < payload.Length;
                offset += RemoteExecutionProtocol.MaxChunkBytes)
            {
                int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes,
                    payload.Length - offset);
                Send(generation, RemoteMessageKind.CommandResultChunk, requestId,
                    RemoteExecutionProtocol.EncodeCommandResultChunk(offset, payload,
                        offset, count));
            }
            Send(generation, RemoteMessageKind.CommandResultEnd, requestId,
                RemoteExecutionProtocol.EncodeCommandResultEnd());
        }

        private void CancelCommand(long generation, RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null &&
                m_IncomingCommandInput.Generation == generation &&
                m_IncomingCommandInput.RequestId == frame.RequestId)
            {
                ResetCommandInput();
                EndRequest(generation, frame.RequestId);
            }
            CancellationTokenSource commandCancellation = null;
            lock (m_CommandLock)
            {
                if (m_CommandRunning && m_RunningCommandGeneration == generation &&
                    m_RunningCommandId == frame.RequestId)
                {
                    m_CommandCancelledRemotely = true;
                    commandCancellation = m_CommandCancellation;
                }
            }
            Cancel(commandCancellation);
        }

        private void EnsureCommandInput(Guid requestId)
        {
            if (m_IncomingCommandInput == null ||
                m_IncomingCommandInput.Generation != m_Generation ||
                m_IncomingCommandInput.RequestId != requestId)
                throw new InvalidDataException("No matching command input.");
        }

        private void ResetCommandInput()
        {
            IncomingCommandInput input = m_IncomingCommandInput;
            m_IncomingCommandInput = null;
            input?.CommandPayload.Dispose();
        }

        private void BeginRequest(long generation, Guid requestId)
        {
            lock (m_LifecycleLock)
            lock (m_CommandLock)
            {
                if (generation != m_Generation || !m_ActiveRequestIds.Add(requestId))
                    throw new InvalidDataException("Duplicate or retired request ID.");
            }
        }

        private void EndRequest(long generation, Guid requestId)
        {
            lock (m_LifecycleLock)
            lock (m_CommandLock)
            {
                if (generation == m_Generation) m_ActiveRequestIds.Remove(requestId);
            }
        }

        private byte[] EncodeCommands()
        {
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock) configuration = m_Configuration;
            if (configuration == null) return RemoteExecutionProtocol.EncodeCommands(
                Array.Empty<RemoteCommandInfo>());
            var commands = new List<RemoteCommandInfo>();
            foreach (RemoteCommandDescriptor descriptor in RemoteCommandRegistry.Snapshot())
            {
                commands.Add(new RemoteCommandInfo
                {
                    Id = descriptor.Id,
                    Name = descriptor.Name,
                    Description = descriptor.Description,
                    Category = descriptor.Category,
                    TimeoutSeconds = descriptor.TimeoutSeconds,
                    MaxRequestBytes = Math.Min(descriptor.MaxRequestBytes,
                        configuration.MaxCommandRequestBytes),
                    MaxResponseBytes = Math.Min(descriptor.MaxResponseBytes,
                        configuration.MaxCommandResponseBytes),
                    RequestContentType = descriptor.RequestContentType,
                    ResponseContentType = descriptor.ResponseContentType,
                    Executable = descriptor.IsExecutable,
                    RequiresMainThread = descriptor.RequiresMainThread
                });
            }
            return RemoteExecutionProtocol.EncodeCommands(commands);
        }

        private void Send(long generation, RemoteMessageKind kind, Guid requestId,
            byte[] payload)
        {
            Action<RemoteMessageKind, Guid, byte[]> send;
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                send = m_Send;
            }
            send?.Invoke(kind, requestId, payload);
        }

        private static void Cancel(CancellationTokenSource cancellation)
        {
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private static bool IsCommandMessage(RemoteMessageKind kind)
        {
            return kind == RemoteMessageKind.CommandInputBegin ||
                kind == RemoteMessageKind.CommandInputChunk ||
                kind == RemoteMessageKind.CommandInputEnd ||
                kind == RemoteMessageKind.CancelCommand;
        }

        private static bool ContentTypeMatches(string expected, string actual)
        {
            return string.IsNullOrEmpty(expected) ||
                string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class IncomingCommandInput
        {
            private IncomingCommandInput() { }

            internal long Generation { get; private set; }
            internal Guid RequestId { get; private set; }
            internal string CommandId { get; private set; }
            internal string ContentType { get; private set; }
            internal long Length { get; private set; }
            internal byte[] Hash { get; private set; }
            internal MemoryStream CommandPayload { get; private set; }

            internal static IncomingCommandInput Create(long generation, Guid requestId,
                string commandId, string contentType, long length, byte[] hash)
            {
                return new IncomingCommandInput
                {
                    Generation = generation,
                    RequestId = requestId,
                    CommandId = commandId,
                    ContentType = contentType,
                    Length = length,
                    Hash = hash,
                    CommandPayload = new MemoryStream(checked((int)length))
                };
            }
        }
    }
}
