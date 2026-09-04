using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionPlayerConnection
    {
        private readonly RemoteExecutionPlayerDriver m_Driver;
        private readonly long m_Generation;
        private readonly RemoteExecutionPlayerConfiguration m_Configuration;
        private readonly CancellationTokenSource m_Cancellation =
            new CancellationTokenSource();
        private readonly SemaphoreSlim m_SendSignal = new SemaphoreSlim(0);
        private readonly Queue<RemoteFrame> m_SendQueue = new Queue<RemoteFrame>();
        private readonly object m_Lock = new object();
        private IRemoteExecutionChannel m_Channel;
        private bool m_StopRequested;
        private bool m_Disposed;
        private bool m_Ready;
        private bool m_WasReady;

        internal RemoteExecutionPlayerConnection(RemoteExecutionPlayerDriver driver,
            long generation, RemoteExecutionPlayerConfiguration configuration)
        {
            m_Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            m_Generation = generation;
            m_Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        internal void Start()
        {
            _ = RunAsync();
        }

        internal void Stop()
        {
            lock (m_Lock)
            {
                if (m_StopRequested) return;
                m_StopRequested = true;
            }
            CloseTransport();
        }

        internal void Send(RemoteMessageKind kind, Guid requestId, byte[] payload)
        {
            SemaphoreSlim signal;
            lock (m_Lock)
            {
                if (m_StopRequested || m_Disposed || !m_Ready) return;
                m_SendQueue.Enqueue(new RemoteFrame(kind, requestId, payload));
                signal = m_SendSignal;
            }
            try { signal.Release(); }
            catch (ObjectDisposedException) { }
        }

        private async Task RunAsync()
        {
            RemoteExecutionConnectionError error = null;
            try
            {
                m_Channel = await ConnectWithTimeoutAsync(m_Cancellation.Token)
                    .ConfigureAwait(false);
                if (m_Channel == null)
                    throw new RemoteExecutionConnectionException("CONNECT_FAILED",
                        "The transport connector returned no channel.");
                if (IsStopRequested()) return;
                m_Driver.PostHandshaking(m_Generation);

                Guid helloRequestId = Guid.NewGuid();
                var hello = new RemoteHello
                {
                    ClientId = m_Configuration.ClientId,
                    Target = m_Configuration.Target,
                    UnityVersion = m_Configuration.UnityVersion,
                    RuntimeVersion = "Unity Remote Execution"
                };
                await SendFrameAsync(new RemoteFrame(RemoteMessageKind.Hello,
                    helloRequestId, RemoteExecutionProtocol.EncodeHello(hello)),
                    m_Cancellation.Token).ConfigureAwait(false);
                RemoteFrame ready = await ReadReadyWithTimeoutAsync(m_Cancellation.Token)
                    .ConfigureAwait(false);
                if (ready.Kind != RemoteMessageKind.Ready ||
                    ready.RequestId != helloRequestId || ready.Payload.Length != 0)
                    throw new RemoteExecutionConnectionException("HANDSHAKE_FAILED",
                        "Ready must acknowledge the Hello request with an empty payload.");

                lock (m_Lock)
                {
                    if (m_StopRequested) return;
                    m_Ready = true;
                    m_WasReady = true;
                }
                m_Driver.PostConnected(m_Generation);

                Task receiveTask = ReceiveLoopAsync(m_Cancellation.Token);
                Task sendTask = SendLoopAsync(m_Cancellation.Token);
                Task completed = await Task.WhenAny(receiveTask, sendTask)
                    .ConfigureAwait(false);
                Exception failure = await GetTaskFailureAsync(completed)
                    .ConfigureAwait(false);
                CloseTransport();
                Exception receiveFailure = await GetTaskFailureAsync(receiveTask)
                    .ConfigureAwait(false);
                Exception sendFailure = await GetTaskFailureAsync(sendTask)
                    .ConfigureAwait(false);
                failure = failure ?? receiveFailure ?? sendFailure;
                if (!IsStopRequested()) error = CreateTerminalError(failure, true);
            }
            catch (RemoteExecutionConnectionException exception)
            {
                if (!IsStopRequested())
                    error = new RemoteExecutionConnectionError(exception.Code,
                        exception.Message);
            }
            catch (OperationCanceledException)
            {
                if (!IsStopRequested())
                    error = new RemoteExecutionConnectionError(
                        m_WasReady ? "CONNECTION_LOST" : "CONNECT_FAILED",
                        "The remote execution transport was cancelled unexpectedly.");
            }
            catch (Exception exception)
            {
                if (!IsStopRequested())
                    error = CreateTerminalError(exception, m_WasReady);
            }
            finally
            {
                CloseTransport();
                lock (m_Lock)
                {
                    m_SendQueue.Clear();
                    m_Disposed = true;
                }
                DisposeChannel();
                m_SendSignal.Dispose();
                m_Cancellation.Dispose();
            }

            if (error != null)
                m_Driver.PostFault(m_Generation, this, error);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteFrame frame = await m_Channel.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                RemoteExecutionProtocol.ValidateFrame(frame);
                if (frame.Kind == RemoteMessageKind.Hello ||
                    frame.Kind == RemoteMessageKind.Ready)
                    throw new InvalidDataException("Unexpected handshake frame.");
                m_Driver.PostFrame(m_Generation, frame);
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await m_SendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    RemoteFrame frame;
                    lock (m_Lock)
                    {
                        if (m_SendQueue.Count == 0) break;
                        frame = m_SendQueue.Dequeue();
                    }
                    await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task<IRemoteExecutionChannel> ConnectWithTimeoutAsync(
            CancellationToken cancellationToken)
        {
            using (var connectCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<IRemoteExecutionChannel> connect;
                try
                {
                    connect = m_Configuration.Connector.ConnectAsync(
                        connectCancellation.Token);
                    if (connect == null)
                        throw new InvalidOperationException(
                            "The transport connector returned no connect task.");
                }
                catch (Exception exception)
                {
                    throw new RemoteExecutionConnectionException("CONNECT_FAILED",
                        exception.Message, exception);
                }
                Task timeout = Task.Delay(m_Configuration.ConnectTimeout,
                    CancellationToken.None);
                Task cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                Task completed = await Task.WhenAny(connect, timeout, cancelled)
                    .ConfigureAwait(false);
                if (completed != connect)
                {
                    connectCancellation.Cancel();
                    ObserveLateChannel(connect);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new RemoteExecutionConnectionException("CONNECT_TIMEOUT",
                        $"Timed out connecting through '{m_Configuration.ConnectionKey}'.");
                }
                try { return await connect.ConfigureAwait(false); }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new RemoteExecutionConnectionException("CONNECT_FAILED",
                        exception.Message, exception);
                }
            }
        }

        private async Task<RemoteFrame> ReadReadyWithTimeoutAsync(
            CancellationToken cancellationToken)
        {
            Task<RemoteFrame> read;
            try { read = m_Channel.ReceiveAsync(cancellationToken); }
            catch (Exception exception)
            {
                throw new RemoteExecutionConnectionException("HANDSHAKE_FAILED",
                    exception.Message, exception);
            }
            if (read == null)
                throw new RemoteExecutionConnectionException("HANDSHAKE_FAILED",
                    "The transport channel returned no receive task.");
            Task timeout = Task.Delay(m_Configuration.HandshakeTimeout,
                CancellationToken.None);
            if (await Task.WhenAny(read, timeout).ConfigureAwait(false) == read)
            {
                try
                {
                    RemoteFrame frame = await read.ConfigureAwait(false);
                    RemoteExecutionProtocol.ValidateFrame(frame);
                    return frame;
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new RemoteExecutionConnectionException("HANDSHAKE_FAILED",
                        exception.Message, exception);
                }
            }
            AbortChannel();
            await IgnoreTaskFailureAsync(read).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new RemoteExecutionConnectionException("HANDSHAKE_TIMEOUT",
                "Timed out waiting for the Editor Ready response.");
        }

        private Task SendFrameAsync(RemoteFrame frame,
            CancellationToken cancellationToken)
        {
            RemoteExecutionProtocol.ValidateFrame(frame);
            Task send = m_Channel.SendAsync(frame, cancellationToken);
            if (send == null)
                throw new InvalidOperationException(
                    "The transport channel returned no send task.");
            return send;
        }

        private void CloseTransport()
        {
            try { m_Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            AbortChannel();
            lock (m_Lock) m_Ready = false;
        }

        private void AbortChannel()
        {
            IRemoteExecutionChannel channel;
            lock (m_Lock) channel = m_Channel;
            try { channel?.Abort(); }
            catch (Exception) { }
        }

        private void DisposeChannel()
        {
            IRemoteExecutionChannel channel;
            lock (m_Lock)
            {
                channel = m_Channel;
                m_Channel = null;
            }
            if (channel == null) return;
            try { channel.Abort(); }
            catch (Exception) { }
            try { channel.Dispose(); }
            catch (Exception) { }
        }

        private bool IsStopRequested()
        {
            lock (m_Lock) return m_StopRequested;
        }

        private static RemoteExecutionConnectionError CreateTerminalError(
            Exception exception, bool wasReady)
        {
            if (exception is InvalidDataException)
                return new RemoteExecutionConnectionError("PROTOCOL_ERROR",
                    exception.Message);
            return new RemoteExecutionConnectionError(
                wasReady ? "CONNECTION_LOST" : "CONNECT_FAILED",
                exception?.Message ??
                "The remote execution connection stopped unexpectedly.");
        }

        private static async Task<Exception> GetTaskFailureAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception exception) { return exception; }
        }

        private static async Task IgnoreTaskFailureAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
        }

        private static void ObserveLateChannel(
            Task<IRemoteExecutionChannel> connect)
        {
            _ = connect.ContinueWith(task =>
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    _ = task.Exception;
                    return;
                }
                if (task.Result == null) return;
                try { task.Result.Abort(); }
                catch (Exception) { }
                try { task.Result.Dispose(); }
                catch (Exception) { }
            }, TaskScheduler.Default);
        }

        private sealed class RemoteExecutionConnectionException : Exception
        {
            internal RemoteExecutionConnectionException(string code, string message,
                Exception innerException = null) : base(message, innerException)
            {
                Code = code;
            }

            internal string Code { get; }
        }
    }
}
