using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public interface IRemoteExecutionChannel : IDisposable
    {
        Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken);
        Task<RemoteFrame> ReceiveAsync(CancellationToken cancellationToken);
        void Abort();
    }

    public interface IRemoteExecutionConnector
    {
        string ConnectionKey { get; }
        Task<IRemoteExecutionChannel> ConnectAsync(CancellationToken cancellationToken);
    }

    public interface IRemoteExecutionListener : IDisposable
    {
        string Description { get; }
        Task<IRemoteExecutionChannel> AcceptAsync(CancellationToken cancellationToken);
        void Abort();
    }

    public sealed class RemoteExecutionTcpConnector : IRemoteExecutionConnector
    {
        public RemoteExecutionTcpConnector(string host = "127.0.0.1", int port = 38421)
        {
            Host = RemoteExecutionTransportValidation.ValidateHost(host, nameof(host));
            Port = RemoteExecutionTransportValidation.ValidatePort(port, false, nameof(port));
        }

        public string Host { get; }
        public int Port { get; }
        public string ConnectionKey => $"tcp://{Host.ToLowerInvariant()}:{Port}";

        public async Task<IRemoteExecutionChannel> ConnectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient { NoDelay = true };
            try
            {
                Task connect = client.ConnectAsync(Host, Port);
                using (cancellationToken.Register(client.Close))
                    await connect.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new RemoteExecutionStreamChannel(client.GetStream(), client);
            }
            catch
            {
                client.Close();
                throw;
            }
        }
    }

    public sealed class RemoteExecutionTcpListener : IRemoteExecutionListener
    {
        private readonly TcpListener m_Listener;
        private readonly object m_Lock = new object();
        private bool m_Disposed;
        private bool m_Accepting;

        public RemoteExecutionTcpListener(string bindAddress = "127.0.0.1",
            int port = 38421)
        {
            if (!IPAddress.TryParse(bindAddress, out IPAddress address))
                throw new ArgumentException("Bind address must be an IP address.",
                    nameof(bindAddress));
            int validatedPort = RemoteExecutionTransportValidation.ValidatePort(
                port, true, nameof(port));
            m_Listener = new TcpListener(address, validatedPort);
            try { m_Listener.Start(); }
            catch
            {
                m_Listener.Stop();
                throw;
            }
            IPEndPoint endpoint = (IPEndPoint)m_Listener.LocalEndpoint;
            BindAddress = address.ToString();
            Port = endpoint.Port;
            Description = $"TCP {endpoint}";
        }

        public string BindAddress { get; }
        public int Port { get; }
        public string Description { get; }

        public async Task<IRemoteExecutionChannel> AcceptAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (m_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(RemoteExecutionTcpListener));
                if (m_Accepting)
                    throw new InvalidOperationException(
                        "Only one pending accept is supported.");
                m_Accepting = true;
            }
            TcpClient client = null;
            try
            {
                Task<TcpClient> accept = m_Listener.AcceptTcpClientAsync();
                using (cancellationToken.Register(Abort))
                    client = await accept.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                client.NoDelay = true;
                return new RemoteExecutionStreamChannel(client.GetStream(), client);
            }
            catch
            {
                client?.Close();
                throw;
            }
            finally
            {
                lock (m_Lock) m_Accepting = false;
            }
        }

        public void Abort()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                try { m_Listener.Stop(); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                try { m_Listener.Stop(); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal sealed class RemoteExecutionStreamChannel : IRemoteExecutionChannel
    {
        private readonly Stream m_Stream;
        private readonly IDisposable m_Owner;
        private readonly object m_Lock = new object();
        private bool m_Disposed;

        internal RemoteExecutionStreamChannel(Stream stream, IDisposable owner = null)
        {
            m_Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            m_Owner = owner;
        }

        public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken)
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(RemoteExecutionStreamChannel));
            }
            return RemoteExecutionProtocol.WriteFrameAsync(m_Stream, frame,
                cancellationToken);
        }

        public Task<RemoteFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(RemoteExecutionStreamChannel));
            }
            return RemoteExecutionProtocol.ReadFrameAsync(m_Stream, cancellationToken);
        }

        public void Abort()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                try { m_Stream.Close(); }
                catch (ObjectDisposedException) { }
                try { m_Owner?.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                try { m_Stream.Dispose(); }
                finally { m_Owner?.Dispose(); }
            }
        }
    }

    internal static class RemoteExecutionTransportValidation
    {
        internal static string ValidateHost(string host, string parameterName)
        {
            string value = (host ?? string.Empty).Trim();
            if (value.Length == 0)
                throw new ArgumentException("Host is required.", parameterName);
            if (value == "*" || IsWildcardAddress(value))
                throw new ArgumentException(
                    "A wildcard address cannot be used as a destination.", parameterName);
            return value;
        }

        internal static int ValidatePort(int port, bool allowZero,
            string parameterName)
        {
            int minimum = allowZero ? 0 : 1;
            if (port < minimum || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName,
                    $"Port must be in range {minimum}..65535.");
            return port;
        }

        private static bool IsWildcardAddress(string host)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress address))
                return false;
            return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        }
    }
}
