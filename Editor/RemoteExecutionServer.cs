using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution
{
    [InitializeOnLoad]
    internal static class RemoteExecutionServer
    {
        private const int MaxClients = 4;
        private const int CommandResponseGraceSeconds = 5;
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<int, ClientSession> s_Sessions =
            new Dictionary<int, ClientSession>();
        private static TcpListener s_Listener;
        private static CancellationTokenSource s_Cancellation;
        private static int s_NextSessionId;

        static RemoteExecutionServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        internal static bool IsRunning => s_Listener != null;
        internal static int Port => (s_Listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

        internal static IReadOnlyList<RemoteExecutionClientInfo> GetClients()
        {
            lock (s_Lock)
            {
                return s_Sessions.Values
                    .OrderBy(session => session.Id)
                    .Select(session => session.CreateInfo())
                    .ToArray();
            }
        }

        internal static void Start(string address, int port)
        {
            if (!IPAddress.TryParse(address, out IPAddress ip))
                throw new InvalidOperationException("Invalid bind address.");
            if (port < 0 || port > ushort.MaxValue)
                throw new InvalidOperationException("Invalid port.");
            Stop();
            s_Cancellation = new CancellationTokenSource();
            s_Listener = new TcpListener(ip, port);
            s_Listener.Start();
            AcceptLoopAsync(s_Listener, s_Cancellation.Token).Forget();
            Debug.Log($"[Unity.RemoteExecution] listening on {s_Listener.LocalEndpoint}");
        }

        internal static void Stop()
        {
            s_Cancellation?.Cancel();
            s_Listener?.Stop();
            s_Listener = null;
            lock (s_Lock)
            {
                foreach (ClientSession session in s_Sessions.Values.ToArray())
                    session.Dispose();
                s_Sessions.Clear();
            }
            s_Cancellation?.Dispose();
            s_Cancellation = null;
        }

        internal static Task<RemoteExecutionResult> ExecuteCommandAsync(int sessionId,
            string commandId, byte[] payload, string contentType,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(sessionId, out ClientSession session))
                throw new InvalidOperationException("Client is no longer connected.");
            return session.ExecuteCommandAsync(commandId, payload ?? Array.Empty<byte>(),
                contentType ?? string.Empty, cancellationToken);
        }

        internal static Task RefreshCommandsAsync(int sessionId,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(sessionId, out ClientSession session))
                throw new InvalidOperationException("Client is no longer connected.");
            return session.RefreshCommandsAsync(cancellationToken);
        }

        private static bool TryGetSession(int id, out ClientSession session)
        {
            lock (s_Lock) return s_Sessions.TryGetValue(id, out session);
        }

        private static async Task AcceptLoopAsync(TcpListener listener,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    ClientSession session;
                    lock (s_Lock)
                    {
                        if (s_Sessions.Count >= MaxClients)
                        {
                            client.Close();
                            continue;
                        }
                        session = new ClientSession(++s_NextSessionId, client);
                        s_Sessions.Add(session.Id, session);
                    }
                    session.RunAsync(cancellationToken).Forget();
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        }

        private sealed class ClientSession : IDisposable
        {
            private readonly TcpClient m_Client;
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
            private readonly SemaphoreSlim m_SendLock = new SemaphoreSlim(1, 1);
            private readonly SemaphoreSlim m_OperationLock = new SemaphoreSlim(1, 1);
            private readonly SemaphoreSlim m_CatalogLock = new SemaphoreSlim(1, 1);
            private readonly Dictionary<Guid, PendingOperation> m_Pending =
                new Dictionary<Guid, PendingOperation>();
            private readonly object m_PendingLock = new object();
            private readonly object m_CatalogStateLock = new object();
            private bool m_IsReady;
            private bool m_Disposed;
            private string m_Status = "Connecting";
            private string m_ClientId = "Unknown";
            private string m_Target = string.Empty;
            private RemoteCommandInfo[] m_Commands = Array.Empty<RemoteCommandInfo>();
            private DateTime m_CommandsUpdatedAt;

            internal ClientSession(int id, TcpClient client)
            {
                Id = id;
                m_Client = client;
            }

            internal int Id { get; }

            internal RemoteExecutionClientInfo CreateInfo()
            {
                RemoteCommandSnapshot[] commands;
                DateTime updatedAt;
                lock (m_CatalogStateLock)
                {
                    commands = m_Commands.Select(command => new RemoteCommandSnapshot(command)).ToArray();
                    updatedAt = m_CommandsUpdatedAt;
                }
                return new RemoteExecutionClientInfo(Id, m_ClientId, m_Target, m_Status,
                    m_IsReady, updatedAt, commands);
            }

            internal async Task RunAsync(CancellationToken serverToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    serverToken, m_Cancellation.Token))
                {
                    try
                    {
                        NetworkStream stream = m_Client.GetStream();
                        RemoteFrame hello = await ReadInitialHelloAsync(stream, linked.Token)
                            .ConfigureAwait(false);
                        if (hello.Kind != RemoteMessageKind.Hello || hello.RequestId == Guid.Empty)
                            throw new InvalidDataException("Hello with a request ID is required.");
                        RemoteHello data = RemoteExecutionProtocol.DecodeHello(hello.Payload);
                        m_ClientId = data.ClientId;
                        m_Target = data.Target;
                        await SendAsync(stream,
                            new RemoteFrame(RemoteMessageKind.Ready, hello.RequestId,
                                Array.Empty<byte>()), linked.Token).ConfigureAwait(false);
                        m_IsReady = true;
                        m_Status = "Ready";
                        RequestCommandsInBackground();
                        while (!linked.IsCancellationRequested)
                        {
                            RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(stream,
                                linked.Token).ConfigureAwait(false);
                            HandleResponse(frame);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        m_Status = "Error: " + exception.Message;
                        FailPending(exception);
                        Debug.LogWarning($"[Unity.RemoteExecution] client {Id} stopped: {exception.Message}");
                    }
                }
                Dispose();
                lock (s_Lock) s_Sessions.Remove(Id);
            }

            internal async Task RefreshCommandsAsync(CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    m_Cancellation.Token, cancellationToken))
                {
                    await m_CatalogLock.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (!m_IsReady)
                            throw new InvalidOperationException("Client is not ready.");
                        await RequestCommandsAsync(linked.Token).ConfigureAwait(false);
                    }
                    finally { m_CatalogLock.Release(); }
                }
            }

            internal async Task<RemoteExecutionResult> ExecuteCommandAsync(string commandId,
                byte[] payload, string contentType, CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    m_Cancellation.Token, cancellationToken))
                {
                    await m_OperationLock.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (!m_IsReady)
                            throw new InvalidOperationException("Client is not ready.");
                        RemoteCommandInfo command = FindCommand(commandId);
                        if (command == null)
                            throw new InvalidOperationException($"Remote command was not found: {commandId}");
                        if (!command.Executable)
                            throw new InvalidOperationException("Remote command is unavailable.");
                        if (payload.Length > command.MaxRequestBytes ||
                            payload.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                            throw new InvalidDataException("Command payload exceeds the advertised limit.");
                        if (!ContentTypeMatches(command.RequestContentType, contentType))
                            throw new InvalidDataException(
                                "Command payload content type does not match the command.");

                        Guid requestId = Guid.NewGuid();
                        var pending = new PendingOperation(command.MaxResponseBytes,
                            command.ResponseContentType);
                        AddPending(requestId, pending);
                        try
                        {
                            NetworkStream stream = m_Client.GetStream();
                            byte[] hash = ComputeHash(payload);
                            linked.Token.ThrowIfCancellationRequested();
                            await SendAsync(stream,
                                new RemoteFrame(RemoteMessageKind.CommandInputBegin, requestId,
                                    RemoteExecutionProtocol.EncodeCommandInputBegin(command.Id,
                                        contentType, payload.LongLength, hash)),
                                m_Cancellation.Token).ConfigureAwait(false);
                            for (int offset = 0; offset < payload.Length;
                                offset += RemoteExecutionProtocol.MaxChunkBytes)
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes,
                                    payload.Length - offset);
                                await SendAsync(stream,
                                    new RemoteFrame(RemoteMessageKind.CommandInputChunk, requestId,
                                        RemoteExecutionProtocol.EncodeCommandChunk(offset, payload,
                                            offset, count)),
                                    m_Cancellation.Token).ConfigureAwait(false);
                            }
                            linked.Token.ThrowIfCancellationRequested();
                            await SendAsync(stream,
                                new RemoteFrame(RemoteMessageKind.CommandInputEnd, requestId,
                                    RemoteExecutionProtocol.EncodeCommandEnd()),
                                m_Cancellation.Token).ConfigureAwait(false);
                            return await WaitForCommandAsync(requestId, pending,
                                command.TimeoutSeconds, linked.Token, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            RemovePending(requestId);
                            SendCancelCommand(requestId);
                            throw;
                        }
                        catch (TimeoutException)
                        {
                            RemovePending(requestId);
                            SendCancelCommand(requestId);
                            throw;
                        }
                        catch
                        {
                            RemovePending(requestId);
                            throw;
                        }
                    }
                    finally { m_OperationLock.Release(); }
                }
            }

            private async Task<RemoteExecutionResult> WaitForCommandAsync(Guid requestId,
                PendingOperation pending, int timeoutSeconds, CancellationToken linkedToken,
                CancellationToken callerToken)
            {
                Task timeout = Task.Delay(TimeSpan.FromSeconds(
                    timeoutSeconds + CommandResponseGraceSeconds), CancellationToken.None);
                Task cancelled = Task.Delay(Timeout.Infinite, linkedToken);
                Task completed = await Task.WhenAny(pending.CommandCompletion.Task, timeout,
                    cancelled).ConfigureAwait(false);
                if (pending.CommandCompletion.Task.IsCompleted)
                    return await pending.CommandCompletion.Task.ConfigureAwait(false);
                if (completed == cancelled)
                {
                    if (callerToken.IsCancellationRequested)
                        throw new OperationCanceledException(callerToken);
                    linkedToken.ThrowIfCancellationRequested();
                }
                throw new TimeoutException(
                    $"Timed out after {timeoutSeconds} seconds waiting for the Player command response.");
            }

            private void SendCancelCommand(Guid requestId)
            {
                if (m_Disposed || m_Cancellation.IsCancellationRequested) return;
                SendAsync(m_Client.GetStream(),
                    new RemoteFrame(RemoteMessageKind.CancelCommand, requestId, Array.Empty<byte>()),
                    m_Cancellation.Token).Forget();
            }

            private async Task RequestCommandsAsync(CancellationToken cancellationToken)
            {
                Guid requestId = Guid.NewGuid();
                var pending = new PendingOperation();
                AddPending(requestId, pending);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await SendAsync(m_Client.GetStream(),
                        new RemoteFrame(RemoteMessageKind.ListCommands, requestId, Array.Empty<byte>()),
                        m_Cancellation.Token).ConfigureAwait(false);
                    Task timeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                    Task cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                    Task completed = await Task.WhenAny(pending.CatalogCompletion.Task, timeout,
                        cancelled).ConfigureAwait(false);
                    if (pending.CatalogCompletion.Task.IsCompleted)
                    {
                        UpdateCommands(await pending.CatalogCompletion.Task.ConfigureAwait(false));
                        return;
                    }
                    if (completed == cancelled) cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for the Player command catalog.");
                }
                finally { RemovePending(requestId); }
            }

            private void RequestCommandsInBackground()
            {
                RefreshCommandsAsync(m_Cancellation.Token).ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        m_Status = "Ready (command catalog unavailable)";
                }, TaskScheduler.Default);
            }

            private void UpdateCommands(RemoteCommandInfo[] commands)
            {
                lock (m_CatalogStateLock)
                {
                    m_Commands = (commands ?? Array.Empty<RemoteCommandInfo>())
                        .OrderBy(command => command.Id, StringComparer.Ordinal)
                        .ToArray();
                    m_CommandsUpdatedAt = DateTime.UtcNow;
                }
                m_Status = "Ready";
            }

            private RemoteCommandInfo FindCommand(string commandId)
            {
                lock (m_CatalogStateLock)
                {
                    return m_Commands.FirstOrDefault(command =>
                        string.Equals(command.Id, commandId, StringComparison.Ordinal));
                }
            }

            private void HandleResponse(RemoteFrame frame)
            {
                if (frame.Kind == RemoteMessageKind.Hello || frame.Kind == RemoteMessageKind.Ready)
                    throw new InvalidDataException("Unexpected handshake frame.");
                if (frame.Kind == RemoteMessageKind.Ping)
                {
                    SendAsync(m_Client.GetStream(),
                        new RemoteFrame(RemoteMessageKind.Pong, frame.RequestId, Array.Empty<byte>()),
                        m_Cancellation.Token).Forget();
                    return;
                }
                PendingOperation pending = GetPending(frame.RequestId);
                if (pending == null) return;
                try
                {
                    switch (frame.Kind)
                    {
                        case RemoteMessageKind.Commands:
                            if (pending.IsCommand)
                                throw new InvalidDataException("Command operation received a catalog response.");
                            pending.CatalogCompletion.TrySetResult(
                                RemoteExecutionProtocol.DecodeCommands(frame.Payload));
                            break;
                        case RemoteMessageKind.CommandResult:
                            if (!pending.IsCommand)
                                throw new InvalidDataException("Catalog operation received a command response.");
                            HandleCommandResult(frame.RequestId, pending, frame.Payload);
                            break;
                        case RemoteMessageKind.CommandResultChunk:
                            if (!pending.IsCommand)
                                throw new InvalidDataException("Catalog operation received a command chunk.");
                            HandleCommandResultChunk(pending, frame.Payload);
                            break;
                        case RemoteMessageKind.CommandResultEnd:
                            if (!pending.IsCommand)
                                throw new InvalidDataException("Catalog operation received a command end frame.");
                            HandleCommandResultEnd(frame.RequestId, pending, frame.Payload);
                            break;
                        case RemoteMessageKind.Error:
                            RemoteError error = RemoteExecutionProtocol.DecodeError(frame.Payload);
                            var remoteException = new InvalidDataException($"[{error.Code}] {error.Message}");
                            RemovePending(frame.RequestId);
                            if (pending.IsCommand)
                                pending.CommandCompletion.TrySetException(remoteException);
                            else
                                pending.CatalogCompletion.TrySetException(remoteException);
                            break;
                        default:
                            throw new InvalidDataException($"Unexpected response kind: {frame.Kind}");
                    }
                }
                catch (Exception exception)
                {
                    RemovePending(frame.RequestId);
                    if (pending.IsCommand)
                        pending.CommandCompletion.TrySetException(exception);
                    else
                        pending.CatalogCompletion.TrySetException(exception);
                }
            }

            private void HandleCommandResult(Guid requestId, PendingOperation pending, byte[] payload)
            {
                if (!pending.IsCommand || pending.ResultPayload != null)
                    throw new InvalidDataException("Unexpected or duplicate command result metadata.");
                RemoteExecutionProtocol.DecodeCommandResult(payload, out bool succeeded,
                    out string code, out string message, out string contentType,
                    out long length, out byte[] hash);
                if (length > pending.MaxResponseBytes)
                    throw new InvalidDataException("Command result exceeds the advertised limit.");
                if (!ContentTypeMatches(pending.ExpectedContentType, contentType))
                    throw new InvalidDataException("Command result content type does not match the command.");
                pending.ResultSucceeded = succeeded;
                pending.ResultCode = code;
                pending.ResultMessage = message;
                pending.ResultContentType = contentType;
                pending.ExpectedLength = length;
                pending.ExpectedHash = hash;
                pending.ResultPayload = new MemoryStream(checked((int)length));
                if (length == 0)
                {
                    RemovePending(requestId);
                    pending.CommandCompletion.TrySetResult(new RemoteExecutionResult(succeeded,
                        code, message, Array.Empty<byte>(), contentType));
                }
            }

            private static void HandleCommandResultChunk(PendingOperation pending, byte[] payload)
            {
                if (!pending.IsCommand || pending.ResultPayload == null)
                    throw new InvalidDataException("Command result metadata is missing.");
                RemoteExecutionProtocol.DecodeCommandResultChunk(payload, out long offset,
                    out byte[] data);
                if (pending.ResultPayload.Position != offset ||
                    pending.ResultPayload.Length + data.Length > pending.ExpectedLength)
                    throw new InvalidDataException("Command result chunk is out of order or too large.");
                pending.ResultPayload.Write(data, 0, data.Length);
            }

            private void HandleCommandResultEnd(Guid requestId, PendingOperation pending, byte[] payload)
            {
                RemoteExecutionProtocol.DecodeCommandResultEnd(payload);
                if (!pending.IsCommand || pending.ResultPayload == null ||
                    pending.ExpectedLength == 0 ||
                    pending.ResultPayload.Length != pending.ExpectedLength)
                    throw new InvalidDataException("Command result length does not match metadata.");
                byte[] result = pending.ResultPayload.ToArray();
                if (!RemoteExecutionProtocol.FixedTimeEquals(ComputeHash(result),
                    pending.ExpectedHash))
                    throw new InvalidDataException("Command result hash does not match metadata.");
                RemovePending(requestId);
                pending.CommandCompletion.TrySetResult(new RemoteExecutionResult(
                    pending.ResultSucceeded, pending.ResultCode, pending.ResultMessage,
                    result, pending.ResultContentType));
            }

            private void AddPending(Guid requestId, PendingOperation pending)
            {
                if (requestId == Guid.Empty) throw new ArgumentException("Request ID is required.", nameof(requestId));
                lock (m_PendingLock)
                {
                    if (m_Disposed) throw new ObjectDisposedException(nameof(ClientSession));
                    if (m_Pending.ContainsKey(requestId))
                        throw new InvalidOperationException("Duplicate remote request ID.");
                    m_Pending.Add(requestId, pending);
                }
            }

            private PendingOperation GetPending(Guid requestId)
            {
                lock (m_PendingLock)
                    return m_Pending.TryGetValue(requestId, out PendingOperation pending)
                        ? pending : null;
            }

            private void RemovePending(Guid requestId)
            {
                PendingOperation pending = null;
                lock (m_PendingLock)
                {
                    if (m_Pending.TryGetValue(requestId, out pending))
                        m_Pending.Remove(requestId);
                }
                pending?.Dispose();
            }

            private static byte[] ComputeHash(byte[] bytes)
            {
                using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
            }

            private static bool ContentTypeMatches(string expected, string actual)
            {
                return string.IsNullOrEmpty(expected) ||
                    string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }

            private async Task<RemoteFrame> ReadInitialHelloAsync(NetworkStream stream,
                CancellationToken cancellationToken)
            {
                Task<RemoteFrame> read = RemoteExecutionProtocol.ReadFrameAsync(stream,
                    cancellationToken);
                Task timeout = Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None);
                Task cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                Task completed = await Task.WhenAny(read, timeout, cancelled)
                    .ConfigureAwait(false);
                if (completed == read) return await read.ConfigureAwait(false);

                m_Client.Close();
                try { await read.ConfigureAwait(false); }
                catch (Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out waiting for the Player Hello.");
            }

            private async Task SendAsync(NetworkStream stream, RemoteFrame frame,
                CancellationToken cancellationToken)
            {
                await m_SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RemoteExecutionProtocol.WriteFrameAsync(stream, frame,
                        cancellationToken).ConfigureAwait(false);
                }
                finally { m_SendLock.Release(); }
            }

            private void FailPending(Exception exception)
            {
                PendingOperation[] pending;
                lock (m_PendingLock)
                {
                    pending = m_Pending.Values.ToArray();
                    m_Pending.Clear();
                }
                foreach (PendingOperation item in pending)
                {
                    if (item.IsCommand)
                        item.CommandCompletion.TrySetException(exception);
                    else
                        item.CatalogCompletion.TrySetException(exception);
                    item.Dispose();
                }
            }

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                m_IsReady = false;
                if (!m_Status.StartsWith("Error", StringComparison.Ordinal)) m_Status = "Disconnected";
                m_Cancellation.Cancel();
                m_Client.Close();
                FailPending(new IOException("Remote execution client disconnected."));
            }

            private sealed class PendingOperation
            {
                internal PendingOperation()
                {
                    CatalogCompletion = new TaskCompletionSource<RemoteCommandInfo[]>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    CommandCompletion = new TaskCompletionSource<RemoteExecutionResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    ExpectedContentType = string.Empty;
                }

                internal PendingOperation(int maxResponseBytes, string expectedContentType)
                    : this()
                {
                    IsCommand = true;
                    MaxResponseBytes = Math.Min(maxResponseBytes,
                        RemoteExecutionProtocol.MaxCommandResponseBytes);
                    ExpectedContentType = expectedContentType ?? string.Empty;
                }

                internal bool IsCommand { get; }
                internal int MaxResponseBytes { get; }
                internal string ExpectedContentType { get; }
                internal TaskCompletionSource<RemoteCommandInfo[]> CatalogCompletion { get; }
                internal TaskCompletionSource<RemoteExecutionResult> CommandCompletion { get; }
                internal bool ResultSucceeded;
                internal string ResultCode;
                internal string ResultMessage;
                internal string ResultContentType;
                internal long ExpectedLength;
                internal byte[] ExpectedHash;
                internal MemoryStream ResultPayload;

                internal void Dispose()
                {
                    ResultPayload?.Dispose();
                    ResultPayload = null;
                }
            }
        }
    }

    internal static class RemoteExecutionEditorTaskExtensions
    {
        internal static void Forget(this Task task) { _ = task; }
    }
}
