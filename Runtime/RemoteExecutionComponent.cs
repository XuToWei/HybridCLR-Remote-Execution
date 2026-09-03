using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RemoteExecution
{
    public sealed class RemoteExecutionComponent : MonoBehaviour
    {
        [SerializeField] private RemoteExecutionSettings m_Configuration;
        private readonly Queue<Action> m_MainThreadActions = new Queue<Action>();
        private readonly object m_ActionLock = new object();
        private readonly Queue<RemoteFrame> m_SendQueue = new Queue<RemoteFrame>();
        private readonly object m_SendLock = new object();
        private readonly HashSet<Guid> m_ActiveRequestIds = new HashSet<Guid>();
        private static readonly object s_ComponentLock = new object();
        private static RemoteExecutionComponent s_RegistryOwner;
        private readonly List<RemoteCommandDescriptor> m_Registrations = new List<RemoteCommandDescriptor>();
        private CancellationTokenSource m_Cancellation;
        private SemaphoreSlim m_SendSignal;
        private TcpClient m_Client;
        private NetworkStream m_Stream;
        private bool m_IsReady;
        private readonly object m_CommandLock = new object();
        private bool m_CommandRunning;
        private Guid m_RunningCommandId;
        private CancellationTokenSource m_CommandCancellation;
        private DateTime m_CommandDeadlineUtc;
        private bool m_CommandCancelledRemotely;
        private bool m_Started;
        private IncomingCommandInput m_IncomingCommandInput;

        public bool IsConnected => m_Client != null && m_Client.Connected && m_IsReady;

        private void Awake()
        {
#if UNITY_EDITOR
            enabled = false;
            return;
#else
            if (m_Configuration == null || !m_Configuration.Enabled)
            {
                enabled = false;
                return;
            }
            lock (s_ComponentLock)
            {
                if (s_RegistryOwner != null && s_RegistryOwner != this)
                {
                    Debug.LogWarning("[Unity.RemoteExecution] only one active component is supported.");
                    enabled = false;
                    return;
                }
                s_RegistryOwner = this;
            }
            DontDestroyOnLoad(gameObject);
#endif
        }

        private void Start()
        {
#if !UNITY_EDITOR
            if (!enabled) return;
            m_Cancellation = new CancellationTokenSource();
            m_SendSignal = new SemaphoreSlim(0);
            m_Started = true;
            DiscoverAndRegisterCommands();
            ConnectAsync(m_Cancellation.Token).Forget();
#endif
        }

        private void Update()
        {
            while (true)
            {
                Action action;
                lock (m_ActionLock)
                {
                    if (m_MainThreadActions.Count == 0) break;
                    action = m_MainThreadActions.Dequeue();
                }
                try { action(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
            lock (m_CommandLock)
            {
                if (m_CommandRunning && DateTime.UtcNow >= m_CommandDeadlineUtc)
                    m_CommandCancellation?.Cancel();
            }
        }

        private void OnDestroy()
        {
            Disconnect();
            if (m_Started && m_Registrations.Count > 0)
            {
                try { RemoteCommandRegistry.Unregister(m_Registrations); }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[Unity.RemoteExecution] registry cleanup was incomplete: {exception.Message}");
                }
            }
            lock (s_ComponentLock)
            {
                if (s_RegistryOwner == this) s_RegistryOwner = null;
            }
        }

        public void Disconnect()
        {
            m_Cancellation?.Cancel();
            lock (m_CommandLock) m_CommandCancellation?.Cancel();
            m_Stream?.Close();
            m_Client?.Close();
            if (m_SendSignal != null)
            {
                try { m_SendSignal.Release(); } catch (ObjectDisposedException) { }
            }
            m_Cancellation?.Dispose();
            m_SendSignal?.Dispose();
            m_Cancellation = null;
            m_SendSignal = null;
            m_Stream = null;
            m_Client = null;
            m_IsReady = false;
            ResetCommandInput();
            lock (m_CommandLock) m_ActiveRequestIds.Clear();
        }

        private void DiscoverAndRegisterCommands()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            RegisterProviders(assemblies);
            try
            {
                IReadOnlyList<RemoteCommandDescriptor> registered =
                    RemoteCommandRegistry.RegisterAttributeCommands(assemblies);
                m_Registrations.AddRange(registered);
            }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        private void RegisterProviders(IEnumerable<Assembly> assemblies)
        {
            var providerTypes = new List<Type>();
            foreach (Assembly assembly in assemblies ?? Array.Empty<Assembly>())
            {
                foreach (Type type in RemoteCommandRegistry.GetLoadableTypes(assembly))
                {
                    if (!IsProviderType(type)) continue;
                    providerTypes.Add(type);
                }
            }
            providerTypes.Sort(CompareProviderTypes);
            foreach (Type providerType in providerTypes)
                TryRegisterProvider(providerType);
        }

        private static bool IsProviderType(Type type)
        {
            return type != null && type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters &&
                typeof(IRemoteCommandProvider).IsAssignableFrom(type) &&
                !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) != null;
        }

        private static int CompareProviderTypes(Type left, Type right)
        {
            int assemblyComparison = StringComparer.Ordinal.Compare(left.Assembly.FullName, right.Assembly.FullName);
            return assemblyComparison != 0 ? assemblyComparison :
                StringComparer.Ordinal.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name);
        }

        private void TryRegisterProvider(Type providerType)
        {
            try
            {
                var provider = (IRemoteCommandProvider)Activator.CreateInstance(providerType);
                IReadOnlyList<RemoteCommandDescriptor> registered =
                    RemoteCommandRegistry.RegisterProvider(provider);
                m_Registrations.AddRange(registered);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Unity.RemoteExecution] provider '{providerType.AssemblyQualifiedName}' failed: {exception.Message}");
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                m_Client = new TcpClient();
                await m_Client.ConnectAsync(m_Configuration.EditorHost, m_Configuration.EditorPort).ConfigureAwait(false);
                m_Stream = m_Client.GetStream();
                var hello = new RemoteHello
                {
                    ClientId = string.IsNullOrEmpty(m_Configuration.ClientId) ? SystemInfo.deviceUniqueIdentifier : m_Configuration.ClientId,
                    Target = GetRuntimeTarget(),
                    UnityVersion = Application.unityVersion,
                    RuntimeVersion = "Unity Remote Execution"
                };
                Guid helloRequestId = Guid.NewGuid();
                await RemoteExecutionProtocol.WriteFrameAsync(m_Stream,
                    new RemoteFrame(RemoteMessageKind.Hello, helloRequestId,
                        RemoteExecutionProtocol.EncodeHello(hello)), cancellationToken).ConfigureAwait(false);
                Task receiveTask = ReceiveLoopAsync(helloRequestId, cancellationToken);
                Task sendTask = SendLoopAsync(cancellationToken);
                await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Debug.LogWarning($"[Unity.RemoteExecution] connection stopped: {exception.Message}"); }
            finally { Disconnect(); }
        }

        private async Task ReceiveLoopAsync(Guid helloRequestId,
            CancellationToken cancellationToken)
        {
            RemoteFrame ready = await RemoteExecutionProtocol.ReadFrameAsync(m_Stream,
                cancellationToken).ConfigureAwait(false);
            if (ready.Kind != RemoteMessageKind.Ready ||
                ready.RequestId != helloRequestId || ready.Payload.Length != 0)
                throw new InvalidDataException(
                    "Ready must acknowledge the Hello request with an empty payload.");
            m_IsReady = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(m_Stream,
                    cancellationToken).ConfigureAwait(false);
                if (frame.Kind == RemoteMessageKind.Hello || frame.Kind == RemoteMessageKind.Ready)
                    throw new InvalidDataException("Unexpected handshake frame.");
                EnqueueMainThread(() => HandleFrame(frame));
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await m_SendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    RemoteFrame frame;
                    lock (m_SendLock)
                    {
                        if (m_SendQueue.Count == 0) break;
                        frame = m_SendQueue.Dequeue();
                    }
                    await RemoteExecutionProtocol.WriteFrameAsync(m_Stream, frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void EnqueueMainThread(Action action)
        {
            lock (m_ActionLock) m_MainThreadActions.Enqueue(action);
        }

        private void HandleFrame(RemoteFrame frame)
        {
            if (!m_IsReady) return;
            try
            {
                if (frame.RequestId == Guid.Empty &&
                    frame.Kind != RemoteMessageKind.Ping && frame.Kind != RemoteMessageKind.Pong)
                    throw new InvalidDataException("Request ID is required.");
                switch (frame.Kind)
                {
                    case RemoteMessageKind.ListCommands:
                        Send(RemoteMessageKind.Commands, frame.RequestId, EncodeCommands());
                        break;
                    case RemoteMessageKind.CommandInputBegin:
                        BeginCommandInput(frame);
                        break;
                    case RemoteMessageKind.CommandInputChunk:
                        WriteCommandChunk(frame);
                        break;
                    case RemoteMessageKind.CommandInputEnd:
                        EndCommandInput(frame);
                        break;
                    case RemoteMessageKind.CancelCommand:
                        CancelCommand(frame);
                        break;
                    case RemoteMessageKind.Ping:
                        Send(RemoteMessageKind.Pong, frame.RequestId, Array.Empty<byte>());
                        break;
                    default:
                        Send(RemoteMessageKind.Error, frame.RequestId,
                            RemoteExecutionProtocol.EncodeError("UNKNOWN_MESSAGE", frame.Kind.ToString()));
                        break;
                }
            }
            catch (Exception exception)
            {
                ResetCommandInput();
                bool isCommand = IsCommandMessage(frame.Kind);
                Send(isCommand ? RemoteMessageKind.CommandResult : RemoteMessageKind.Error, frame.RequestId,
                    isCommand
                        ? RemoteExecutionProtocol.EncodeCommandResult(false, "PROTOCOL_ERROR", exception.Message,
                            string.Empty, null)
                        : RemoteExecutionProtocol.EncodeError("PROTOCOL_ERROR", exception.Message));
            }
        }

        private void ScheduleCommand(Guid requestId, RemoteCommandDescriptor descriptor,
            byte[] payload, string contentType)
        {
            if (descriptor.RequiresMainThread)
                ExecuteCommand(requestId, descriptor, payload, contentType).Forget();
            else
                Task.Run(() => ExecuteCommand(requestId, descriptor, payload, contentType)).Forget();
        }

        private async Task ExecuteCommand(Guid requestId, RemoteCommandDescriptor descriptor, byte[] payload,
            string contentType)
        {
            CancellationTokenSource commandCancellation;
            lock (m_CommandLock)
            {
                if (m_CommandRunning)
                {
                    Send(RemoteMessageKind.CommandResult, requestId,
                        RemoteExecutionProtocol.EncodeCommandResult(false, "COMMAND_BUSY",
                            "Another command is running.", string.Empty, null));
                    EndRequest(requestId);
                    return;
                }
                m_CommandRunning = true;
                m_RunningCommandId = requestId;
                commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    m_Cancellation?.Token ?? CancellationToken.None);
                m_CommandCancellation = commandCancellation;
                m_CommandDeadlineUtc = DateTime.UtcNow.AddSeconds(descriptor.TimeoutSeconds);
                m_CommandCancelledRemotely = false;
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
                SendCommandResult(requestId, result);
            }
            catch (OperationCanceledException)
            {
                bool cancelledRemotely;
                lock (m_CommandLock) cancelledRemotely = m_CommandCancelledRemotely;
                string code = cancelledRemotely ? "COMMAND_CANCELLED" : "COMMAND_TIMED_OUT";
                string message = cancelledRemotely ? "Command was cancelled." : "Command timed out.";
                Send(RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, code, message,
                        string.Empty, null));
            }
            catch (Exception exception)
            {
                Send(RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, "COMMAND_EXECUTION_FAILED",
                        exception.Message, string.Empty, null));
            }
            finally
            {
                lock (m_CommandLock)
                {
                    if (ReferenceEquals(m_CommandCancellation, commandCancellation))
                    {
                        m_CommandCancellation = null;
                        m_RunningCommandId = Guid.Empty;
                        m_CommandDeadlineUtc = default(DateTime);
                        m_CommandCancelledRemotely = false;
                        m_CommandRunning = false;
                    }
                }
                commandCancellation.Dispose();
                EndRequest(requestId);
            }
        }

        private void SendCommandResult(Guid requestId, RemoteCommandResult result)
        {
            byte[] payload = result.Payload ?? Array.Empty<byte>();
            if (payload.Length > m_Configuration.MaxCommandResponseBytes)
                throw new InvalidDataException("Command result exceeds the configured response limit.");
            Send(RemoteMessageKind.CommandResult, requestId,
                RemoteExecutionProtocol.EncodeCommandResult(result.Succeeded, result.Code, result.Message, result.ContentType, payload));
            if (payload.Length == 0) return;
            for (int offset = 0; offset < payload.Length; offset += RemoteExecutionProtocol.MaxChunkBytes)
            {
                int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes, payload.Length - offset);
                Send(RemoteMessageKind.CommandResultChunk, requestId,
                    RemoteExecutionProtocol.EncodeCommandResultChunk(offset, payload, offset, count));
            }
            Send(RemoteMessageKind.CommandResultEnd, requestId, RemoteExecutionProtocol.EncodeCommandResultEnd());
        }

        private void ResetCommandInput()
        {
            IncomingCommandInput input = m_IncomingCommandInput;
            m_IncomingCommandInput = null;
            input?.CommandPayload.Dispose();
        }

        private void BeginCommandInput(RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null) throw new InvalidOperationException("Another transfer is active.");
            RemoteExecutionProtocol.DecodeCommandInputBegin(frame.Payload, out string commandId, out string contentType,
                out long length, out byte[] hash);
            if (!RemoteCommandRegistry.TryGet(commandId, out RemoteCommandDescriptor descriptor) || !descriptor.IsExecutable)
                throw new InvalidOperationException("Command is not executable.");
            if (length > descriptor.MaxRequestBytes || length > m_Configuration.MaxCommandRequestBytes ||
                !ContentTypeMatches(descriptor.RequestContentType, contentType))
                throw new InvalidDataException("Command input exceeds the command limits.");
            BeginRequest(frame.RequestId);
            m_IncomingCommandInput = IncomingCommandInput.Create(frame.RequestId, commandId,
                contentType, length, hash);
        }

        private void WriteCommandChunk(RemoteFrame frame)
        {
            EnsureCommandInput(frame.RequestId);
            RemoteExecutionProtocol.DecodeCommandChunk(frame.Payload, out long offset, out byte[] data);
            MemoryStream stream = m_IncomingCommandInput.CommandPayload;
            if (stream.Position != offset || stream.Length + data.Length > stream.Capacity)
                throw new InvalidDataException("Command chunk is out of order or too large.");
            stream.Write(data, 0, data.Length);
        }

        private void EndCommandInput(RemoteFrame frame)
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
            finally { input.CommandPayload.Dispose(); }
            if (!RemoteCommandRegistry.TryGet(input.CommandId, out RemoteCommandDescriptor descriptor))
                throw new InvalidOperationException("Command is no longer registered.");
            ScheduleCommand(frame.RequestId, descriptor, bytes, input.ContentType);
        }

        private void CancelCommand(RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null && m_IncomingCommandInput.RequestId == frame.RequestId)
            {
                ResetCommandInput();
                EndRequest(frame.RequestId);
            }
            lock (m_CommandLock)
            {
                if (m_CommandRunning && m_RunningCommandId == frame.RequestId)
                {
                    m_CommandCancelledRemotely = true;
                    m_CommandCancellation?.Cancel();
                }
            }
        }

        private void EnsureCommandInput(Guid requestId)
        {
            if (m_IncomingCommandInput == null || m_IncomingCommandInput.RequestId != requestId)
                throw new InvalidDataException("No matching command input.");
        }

        private void BeginRequest(Guid requestId)
        {
            lock (m_CommandLock)
            {
                if (!m_ActiveRequestIds.Add(requestId))
                    throw new InvalidDataException("Duplicate active request ID.");
            }
        }

        private void EndRequest(Guid requestId)
        {
            lock (m_CommandLock) m_ActiveRequestIds.Remove(requestId);
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
            return string.IsNullOrEmpty(expected) || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private byte[] EncodeCommands()
        {
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
                    MaxRequestBytes = Math.Min(descriptor.MaxRequestBytes, m_Configuration.MaxCommandRequestBytes),
                    MaxResponseBytes = Math.Min(descriptor.MaxResponseBytes, m_Configuration.MaxCommandResponseBytes),
                    RequestContentType = descriptor.RequestContentType,
                    ResponseContentType = descriptor.ResponseContentType,
                    Executable = descriptor.IsExecutable,
                    RequiresMainThread = descriptor.RequiresMainThread
                });
            }
            return RemoteExecutionProtocol.EncodeCommands(commands);
        }

        private void Send(RemoteMessageKind kind, Guid requestId, byte[] payload)
        {
            if (m_Cancellation == null || m_SendSignal == null) return;
            lock (m_SendLock) m_SendQueue.Enqueue(new RemoteFrame(kind, requestId, payload));
            try { m_SendSignal.Release(); } catch (ObjectDisposedException) { }
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

        private sealed class IncomingCommandInput
        {
            private IncomingCommandInput() { }

            internal Guid RequestId { get; private set; }
            internal string CommandId { get; private set; }
            internal string ContentType { get; private set; }
            internal long Length { get; private set; }
            internal byte[] Hash { get; private set; }
            internal MemoryStream CommandPayload { get; private set; }

            internal static IncomingCommandInput Create(Guid requestId, string commandId,
                string contentType, long length, byte[] hash)
            {
                return new IncomingCommandInput
                {
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

    internal static class RemoteExecutionTaskExtensions
    {
        public static void Forget(this Task task) { _ = task; }
    }
}
