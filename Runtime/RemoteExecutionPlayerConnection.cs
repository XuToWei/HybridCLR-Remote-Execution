using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionPlayerConnection
    {
        private const int ConnectTimeoutSeconds = 10;
        private const int HandshakeTimeoutSeconds = 15;
        private readonly RemoteExecutionPlayerDriver m_Driver;
        private readonly long m_Generation;
        private readonly RemoteExecutionPlayerConfiguration m_Configuration;
        private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim m_SendSignal = new SemaphoreSlim(0);
        private readonly Queue<RemoteFrame> m_SendQueue = new Queue<RemoteFrame>();
        private readonly object m_Lock = new object();
        private TcpClient m_Client;
        private NetworkStream m_Stream;
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
                CloseTransport();
            }
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
                m_Client = new TcpClient { NoDelay = true };
                await ConnectWithTimeoutAsync(m_Client, m_Cancellation.Token)
                    .ConfigureAwait(false);
                m_Stream = m_Client.GetStream();
                m_Driver.PostHandshaking(m_Generation);

                Guid helloRequestId = Guid.NewGuid();
                var hello = new RemoteHello
                {
                    ClientId = m_Configuration.ClientId,
                    Target = m_Configuration.Target,
                    UnityVersion = m_Configuration.UnityVersion,
                    RuntimeVersion = "Unity Remote Execution"
                };
                await RemoteExecutionProtocol.WriteFrameAsync(m_Stream,
                    new RemoteFrame(RemoteMessageKind.Hello, helloRequestId,
                        RemoteExecutionProtocol.EncodeHello(hello)),
                    m_Cancellation.Token).ConfigureAwait(false);
                RemoteFrame ready = await ReadReadyWithTimeoutAsync(m_Stream,
                    m_Cancellation.Token).ConfigureAwait(false);
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
                Exception failure = await GetTaskFailureAsync(completed).ConfigureAwait(false);
                lock (m_Lock) CloseTransport();
                Exception receiveFailure = await GetTaskFailureAsync(receiveTask).ConfigureAwait(false);
                Exception sendFailure = await GetTaskFailureAsync(sendTask).ConfigureAwait(false);
                failure = failure ?? receiveFailure ?? sendFailure;
                if (!IsStopRequested())
                    error = CreateTerminalError(failure, true);
            }
            catch (RemoteExecutionConnectionException exception)
            {
                if (!IsStopRequested())
                    error = new RemoteExecutionConnectionError(exception.Code, exception.Message);
            }
            catch (OperationCanceledException)
            {
                if (!IsStopRequested())
                    error = new RemoteExecutionConnectionError("CONNECTION_LOST",
                        "The remote execution connection was cancelled unexpectedly.");
            }
            catch (Exception exception)
            {
                if (!IsStopRequested())
                    error = CreateTerminalError(exception, m_WasReady);
            }
            finally
            {
                lock (m_Lock)
                {
                    CloseTransport();
                    m_SendQueue.Clear();
                    m_Disposed = true;
                }
                m_Stream = null;
                m_Client = null;
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
                RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(m_Stream,
                    cancellationToken).ConfigureAwait(false);
                if (frame.Kind == RemoteMessageKind.Hello || frame.Kind == RemoteMessageKind.Ready)
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
                    await RemoteExecutionProtocol.WriteFrameAsync(m_Stream, frame,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task ConnectWithTimeoutAsync(TcpClient client,
            CancellationToken cancellationToken)
        {
            Task connect = client.ConnectAsync(m_Configuration.EditorHost,
                m_Configuration.EditorPort);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds),
                cancellationToken);
            if (await Task.WhenAny(connect, timeout).ConfigureAwait(false) != connect)
            {
                client.Close();
                await IgnoreTaskFailureAsync(connect).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new RemoteExecutionConnectionException("CONNECT_TIMEOUT",
                    $"Timed out connecting to {m_Configuration.EditorHost}:{m_Configuration.EditorPort}.");
            }
            try { await connect.ConfigureAwait(false); }
            catch (Exception exception)
            {
                throw new RemoteExecutionConnectionException("CONNECT_FAILED",
                    exception.Message, exception);
            }
        }

        private async Task<RemoteFrame> ReadReadyWithTimeoutAsync(NetworkStream stream,
            CancellationToken cancellationToken)
        {
            Task<RemoteFrame> read = RemoteExecutionProtocol.ReadFrameAsync(stream,
                cancellationToken);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(HandshakeTimeoutSeconds),
                cancellationToken);
            if (await Task.WhenAny(read, timeout).ConfigureAwait(false) == read)
            {
                try { return await read.ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    throw new RemoteExecutionConnectionException("HANDSHAKE_FAILED",
                        exception.Message, exception);
                }
            }
            lock (m_Lock) CloseSocket();
            await IgnoreTaskFailureAsync(read).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new RemoteExecutionConnectionException("HANDSHAKE_TIMEOUT",
                "Timed out waiting for the Editor Ready response.");
        }

        private void CloseTransport()
        {
            try { m_Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            CloseSocket();
            m_Ready = false;
        }

        private void CloseSocket()
        {
            try { m_Stream?.Close(); }
            catch (ObjectDisposedException) { }
            try { m_Client?.Close(); }
            catch (ObjectDisposedException) { }
        }

        private bool IsStopRequested()
        {
            lock (m_Lock) return m_StopRequested;
        }

        private static RemoteExecutionConnectionError CreateTerminalError(Exception exception,
            bool wasReady)
        {
            if (exception is InvalidDataException)
                return new RemoteExecutionConnectionError("PROTOCOL_ERROR", exception.Message);
            return new RemoteExecutionConnectionError(
                wasReady ? "CONNECTION_LOST" : "CONNECT_FAILED",
                exception?.Message ?? "The remote execution connection stopped unexpectedly.");
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
