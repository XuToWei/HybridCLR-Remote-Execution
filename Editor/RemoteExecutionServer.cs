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

namespace HybridCLR.RemoteExecution
{
    [InitializeOnLoad]
    internal static class RemoteExecutionServer
    {
        private const int MaxClients = 4;
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<int, ClientSession> s_Sessions = new Dictionary<int, ClientSession>();
        private static readonly object s_CompileLock = new object();
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

        internal sealed class ClientInfo
        {
            internal int Id;
            internal string Description;
            internal string Status;
        }

        internal static IReadOnlyList<ClientInfo> GetClients()
        {
            lock (s_Lock)
            {
                var result = new List<ClientInfo>();
                foreach (ClientSession session in s_Sessions.Values) result.Add(session.CreateInfo());
                return result.AsReadOnly();
            }
        }

        internal static void Start(string address, int port, string token)
        {
#if !UNITY_HOTFIX
            throw new InvalidOperationException("Remote execution requires UNITY_HOTFIX.");
#else
            if (!HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable) throw new InvalidOperationException("HybridCLR is not enabled.");
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Authentication token is required.");
            if (!IPAddress.TryParse(address, out IPAddress ip)) throw new InvalidOperationException("Invalid bind address.");
            if (port < 0 || port > ushort.MaxValue) throw new InvalidOperationException("Invalid port.");
            Stop();
            s_Cancellation = new CancellationTokenSource();
            s_Listener = new TcpListener(ip, port);
            s_Listener.Start();
            AcceptLoopAsync(s_Listener, token, s_Cancellation.Token).Forget();
            Debug.Log($"[HybridCLR.RemoteExecution] listening on {s_Listener.LocalEndpoint}");
#endif
        }

        internal static void Stop()
        {
            s_Cancellation?.Cancel();
            s_Listener?.Stop();
            s_Listener = null;
            lock (s_Lock)
            {
                foreach (ClientSession session in s_Sessions.Values.ToArray()) session.Dispose();
                s_Sessions.Clear();
            }
            s_Cancellation?.Dispose();
            s_Cancellation = null;
        }

        internal static void CompileAndSend(int sessionId)
        {
#if !UNITY_HOTFIX
            throw new InvalidOperationException("Remote execution requires UNITY_HOTFIX.");
#else
            if (!TryGetSession(sessionId, out ClientSession session)) throw new InvalidOperationException("Client is no longer connected.");
            if (!session.IsAuthenticated) throw new InvalidOperationException("Client is not authenticated.");
            if (!Enum.TryParse(session.Target, true, out BuildTarget target)) throw new InvalidOperationException($"Unsupported Player target '{session.Target}'.");
            RemoteExecutionBundle bundle;
            lock (s_CompileLock)
            {
                bundle = RemoteExecutionCompiler.Compile(target);
            }
            session.SendBundle(bundle);
#endif
        }

        private static bool TryGetSession(int id, out ClientSession session)
        {
            lock (s_Lock) return s_Sessions.TryGetValue(id, out session);
        }

        private static async Task AcceptLoopAsync(TcpListener listener, string token, CancellationToken cancellationToken)
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
                        if (s_Sessions.Count >= MaxClients) { client.Close(); continue; }
                        session = new ClientSession(++s_NextSessionId, client, token);
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
            private readonly string m_Token;
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
            private readonly SemaphoreSlim m_SendLock = new SemaphoreSlim(1, 1);
            private bool m_Authenticated;
            private string m_Status = "Authenticating";
            private string m_ClientId = "Unknown";
            private string m_Target;

            internal ClientSession(int id, TcpClient client, string token) { Id = id; m_Client = client; m_Token = token; }
            internal int Id { get; }
            internal bool IsAuthenticated => m_Authenticated;
            internal string Target => m_Target;
            internal ClientInfo CreateInfo() => new ClientInfo { Id = Id, Description = $"{m_ClientId} ({m_Target ?? "?"})", Status = m_Status };

            internal async Task RunAsync(CancellationToken serverToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, m_Cancellation.Token))
                {
                    try
                    {
                        NetworkStream stream = m_Client.GetStream();
                        RemoteFrame hello = await ReadWithTimeoutAsync(stream, linked.Token).ConfigureAwait(false);
                        if (hello.Kind != RemoteMessageKind.Hello) throw new InvalidDataException("Hello is required.");
                        RemoteHello data = RemoteExecutionProtocol.DecodeHello(hello.Payload);
                        m_ClientId = data.ClientId;
                        m_Target = data.Target;
                        m_Nonce = new byte[32];
                        using (var random = RandomNumberGenerator.Create()) random.GetBytes(m_Nonce);
                        await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Challenge, hello.RequestId, RemoteExecutionProtocol.EncodeChallenge(m_Nonce)), linked.Token).ConfigureAwait(false);
                        RemoteFrame auth = await ReadWithTimeoutAsync(stream, linked.Token).ConfigureAwait(false);
                        byte[] expected = RemoteExecutionProtocol.ComputeAuthentication(m_Nonce, m_Token);
                        if (auth.Kind != RemoteMessageKind.Authenticate || !RemoteExecutionProtocol.FixedTimeEquals(auth.Payload, expected)) throw new InvalidDataException("Authentication failed.");
                        m_Authenticated = true;
                        m_Status = "Authenticated";
                        await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Ready, auth.RequestId, Array.Empty<byte>()), linked.Token).ConfigureAwait(false);
                        while (!linked.IsCancellationRequested)
                        {
                            RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(stream, linked.Token).ConfigureAwait(false);
                            if (frame.Kind == RemoteMessageKind.Ping)
                                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Pong, frame.RequestId, Array.Empty<byte>()), linked.Token).ConfigureAwait(false);
                            else
                                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Error, frame.RequestId, RemoteExecutionProtocol.EncodeError("UNKNOWN_MESSAGE", frame.Kind.ToString())), linked.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) { m_Status = "Error: " + exception.Message; Debug.LogWarning($"[HybridCLR.RemoteExecution] client {Id} stopped: {exception.Message}"); }
                }
                Dispose();
                lock (s_Lock) s_Sessions.Remove(Id);
            }

            private byte[] m_Nonce;

            internal void SendBundle(RemoteExecutionBundle bundle)
            {
                if (!m_Authenticated) throw new InvalidOperationException("Client is not authenticated.");
                SendBundleAsync(bundle, m_Cancellation.Token).Forget();
            }

            private async Task SendBundleAsync(RemoteExecutionBundle bundle, CancellationToken cancellationToken)
            {
                NetworkStream stream = m_Client.GetStream();
                Guid requestId = Guid.NewGuid();
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.LoadManifest, requestId, RemoteExecutionProtocol.EncodeManifest(bundle.ToManifest())), cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < bundle.Artifacts.Count; i++)
                {
                    RemoteExecutionArtifact artifact = bundle.Artifacts[i];
                    await SendArtifactAsync(stream, bundle.BundleId, i, false, artifact.Dll, artifact.DllSha256, cancellationToken).ConfigureAwait(false);
                    if (artifact.Pdb != null && artifact.Pdb.Length > 0) await SendArtifactAsync(stream, bundle.BundleId, i, true, artifact.Pdb, artifact.PdbSha256, cancellationToken).ConfigureAwait(false);
                }
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.LoadComplete, requestId, RemoteExecutionProtocol.EncodeBundleComplete(bundle.BundleId)), cancellationToken).ConfigureAwait(false);
            }

            private async Task SendArtifactAsync(NetworkStream stream, Guid bundleId, int index, bool pdb, byte[] bytes, byte[] hash, CancellationToken cancellationToken)
            {
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyBegin, Guid.NewGuid(), RemoteExecutionProtocol.EncodeAssemblyBegin(bundleId, index, pdb, bytes.LongLength, hash)), cancellationToken).ConfigureAwait(false);
                for (int offset = 0; offset < bytes.Length; offset += RemoteExecutionProtocol.MaxChunkBytes)
                {
                    int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes, bytes.Length - offset);
                    await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyChunk, Guid.NewGuid(), RemoteExecutionProtocol.EncodeChunk(bundleId, index, pdb, offset, bytes, count)), cancellationToken).ConfigureAwait(false);
                }
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyEnd, Guid.NewGuid(), RemoteExecutionProtocol.EncodeAssemblyEnd(bundleId, index, pdb)), cancellationToken).ConfigureAwait(false);
            }

            private async Task<RemoteFrame> ReadWithTimeoutAsync(NetworkStream stream, CancellationToken cancellationToken)
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    return await RemoteExecutionProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                }
            }

            private async Task SendAsync(NetworkStream stream, RemoteFrame frame, CancellationToken cancellationToken)
            {
                await m_SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await RemoteExecutionProtocol.WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false); }
                finally { m_SendLock.Release(); }
            }

            public void Dispose()
            {
                m_Cancellation.Cancel();
                m_Client.Close();
                if (m_SendLock.CurrentCount != 0) m_SendLock.Dispose();
                m_Cancellation.Dispose();
            }
        }
    }

    internal static class RemoteExecutionEditorTaskExtensions
    {
        internal static void Forget(this Task task) { _ = task; }
    }
}
