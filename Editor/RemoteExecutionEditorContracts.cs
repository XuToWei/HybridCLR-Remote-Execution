using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public interface IRemoteExecutionEditorPanel
    {
        string Id { get; }
        string DisplayName { get; }
        int Order { get; }
        bool IsAvailable(RemoteExecutionEditorContext context, out string unavailableReason);
        void DrawGUI(RemoteExecutionEditorContext context);
    }

    public sealed class RemoteExecutionEditorContext
    {
        private readonly Func<string, Func<CancellationToken, Task<string>>, bool> m_StartOperation;
        private bool m_IsValid = true;

        internal RemoteExecutionEditorContext(RemoteExecutionClientInfo selectedPlayer,
            bool isOperationRunning, string operationStatus,
            Func<string, Func<CancellationToken, Task<string>>, bool> startOperation)
        {
            SelectedPlayer = selectedPlayer;
            IsOperationRunning = isOperationRunning;
            OperationStatus = operationStatus ?? string.Empty;
            m_StartOperation = startOperation;
        }

        public RemoteExecutionClientInfo SelectedPlayer { get; }
        public bool IsOperationRunning { get; }
        public string OperationStatus { get; }

        public bool TryStartOperation(string runningStatus,
            Func<CancellationToken, Task<string>> operation)
        {
            if (!m_IsValid) throw new InvalidOperationException(
                "Remote execution editor context is no longer valid.");
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return m_StartOperation != null && m_StartOperation(
                string.IsNullOrWhiteSpace(runningStatus) ? "Running..." : runningStatus,
                operation);
        }

        internal void Invalidate()
        {
            m_IsValid = false;
        }
    }

    public sealed class RemoteExecutionResult
    {
        internal RemoteExecutionResult(bool succeeded, string code, string message,
            byte[] payload, string contentType)
        {
            Succeeded = succeeded;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Payload = payload == null || payload.Length == 0
                ? Array.Empty<byte>() : (byte[])payload.Clone();
            ContentType = contentType ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Code { get; }
        public string Message { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }
    }

    public sealed class RemoteCommandSnapshot
    {
        internal RemoteCommandSnapshot(RemoteCommandInfo command)
        {
            Id = command.Id;
            Name = command.Name;
            Description = command.Description;
            Category = command.Category;
            TimeoutSeconds = command.TimeoutSeconds;
            MaxRequestBytes = command.MaxRequestBytes;
            MaxResponseBytes = command.MaxResponseBytes;
            RequestContentType = command.RequestContentType;
            ResponseContentType = command.ResponseContentType;
            Executable = command.Executable;
            RequiresMainThread = command.RequiresMainThread;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public int TimeoutSeconds { get; }
        public int MaxRequestBytes { get; }
        public int MaxResponseBytes { get; }
        public string RequestContentType { get; }
        public string ResponseContentType { get; }
        public bool Executable { get; }
        public bool RequiresMainThread { get; }
    }

    public sealed class RemoteExecutionClientInfo
    {
        internal RemoteExecutionClientInfo(int id, string clientId, string target, string status,
            bool isReady, DateTime commandsUpdatedAt,
            IReadOnlyList<RemoteCommandSnapshot> commands)
        {
            Id = id;
            ClientId = clientId ?? string.Empty;
            Target = target ?? string.Empty;
            Status = status ?? string.Empty;
            IsReady = isReady;
            CommandsUpdatedAt = commandsUpdatedAt;
            Commands = commands ?? Array.Empty<RemoteCommandSnapshot>();
        }

        public int Id { get; }
        public string ClientId { get; }
        public string Target { get; }
        public string Status { get; }
        public string Description => $"{ClientId} ({Target})";
        public bool IsReady { get; }
        public DateTime CommandsUpdatedAt { get; }
        public IReadOnlyList<RemoteCommandSnapshot> Commands { get; }
    }

    public sealed class RemoteExecutionServerOptions
    {
        public RemoteExecutionServerOptions(IRemoteExecutionListener listener,
            int maxClients = 4, TimeSpan? handshakeTimeout = null)
        {
            Listener = listener ?? throw new ArgumentNullException(nameof(listener));
            ListenerDescription = listener.Description;
            if (string.IsNullOrWhiteSpace(ListenerDescription))
                throw new ArgumentException("Listener description is required.",
                    nameof(listener));
            if (maxClients < 1 || maxClients > 1024)
                throw new ArgumentOutOfRangeException(nameof(maxClients));
            MaxClients = maxClients;
            HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15);
            if (HandshakeTimeout <= TimeSpan.Zero ||
                HandshakeTimeout > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }

        public IRemoteExecutionListener Listener { get; }
        internal string ListenerDescription { get; }
        public int MaxClients { get; }
        public TimeSpan HandshakeTimeout { get; }
    }

    public static class RemoteExecutionEditorApi
    {
        public static bool IsServerRunning => RemoteExecutionServer.IsRunning;
        public static string ListenerDescription =>
            RemoteExecutionServer.ListenerDescription;

        public static void StartServer(string bindAddress = "127.0.0.1",
            int port = 38421, int maxClients = 4,
            TimeSpan? handshakeTimeout = null)
        {
            RemoteExecutionTcpListener listener = null;
            bool started = false;
            try
            {
                listener = new RemoteExecutionTcpListener(bindAddress, port);
                StartServer(new RemoteExecutionServerOptions(listener, maxClients,
                    handshakeTimeout));
                started = true;
            }
            finally
            {
                if (!started && listener != null)
                {
                    try { listener.Abort(); }
                    catch (Exception) { }
                    try { listener.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        public static void StartServer(RemoteExecutionServerOptions options)
        {
            RemoteExecutionServer.Start(options ??
                throw new ArgumentNullException(nameof(options)));
        }

        public static void StopServer()
        {
            RemoteExecutionServer.Stop();
        }

        public static IReadOnlyList<RemoteExecutionClientInfo> GetClients()
        {
            return RemoteExecutionServer.GetClients();
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync(int sessionId, string commandId,
            byte[] payload = null, string contentType = "")
        {
            return ExecuteCommandAsync(sessionId, commandId, payload, contentType,
                CancellationToken.None);
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync(int sessionId, string commandId,
            byte[] payload, string contentType, CancellationToken cancellationToken)
        {
            return RemoteExecutionServer.ExecuteCommandAsync(sessionId, commandId, payload,
                contentType, cancellationToken);
        }

        public static Task RefreshCommandsAsync(int sessionId)
        {
            return RefreshCommandsAsync(sessionId, CancellationToken.None);
        }

        public static Task RefreshCommandsAsync(int sessionId,
            CancellationToken cancellationToken)
        {
            return RemoteExecutionServer.RefreshCommandsAsync(sessionId, cancellationToken);
        }
    }
}
