using System;
using System.Collections.Concurrent;
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
        private const int OperationTimeoutSeconds = 180;
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<int, ClientSession> s_Sessions = new Dictionary<int, ClientSession>();
        private static readonly SemaphoreSlim s_CompileLock = new SemaphoreSlim(1, 1);
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

        internal static Task CompileAndSend(int sessionId, IEnumerable<string> selectedAssemblyNames,
            IEnumerable<string> selectedDefines, string source, string entryTypeName, string entryMethodName)
        {
            return CompileAndSendAsync(sessionId, selectedAssemblyNames, source, entryTypeName, entryMethodName);
        }

        private static async Task CompileAndSendAsync(int sessionId, IEnumerable<string> selectedAssemblyNames,
            IEnumerable<string> selectedDefines, string source, string entryTypeName, string entryMethodName)
        {
            if (!TryGetSession(sessionId, out ClientSession session)) throw new InvalidOperationException("Client is no longer connected.");
            if (!session.IsAuthenticated) throw new InvalidOperationException("Client is not authenticated.");
            if (!Enum.TryParse(session.Target, true, out BuildTarget target))
                throw new InvalidOperationException($"Unsupported Player target '{session.Target}'.");

            await s_CompileLock.WaitAsync();
            try
            {
                RemoteExecutionBundle bundle = await RemoteExecutionCompiler.CompileAsync(target,
                    selectedAssemblyNames, selectedDefines, source, entryTypeName, entryMethodName);
                await session.SendBundleAndInvokeAsync(bundle);
            }
            finally
            {
                s_CompileLock.Release();
            }
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
            private readonly SemaphoreSlim m_OperationLock = new SemaphoreSlim(1, 1);
            private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteResponse>> m_Pending =
                new ConcurrentDictionary<Guid, TaskCompletionSource<RemoteResponse>>();
            private bool m_Authenticated;
            private bool m_Disposed;
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
                        byte[] nonce = new byte[32];
                        using (var random = RandomNumberGenerator.Create()) random.GetBytes(nonce);
                        await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Challenge, hello.RequestId,
                            RemoteExecutionProtocol.EncodeChallenge(nonce)), linked.Token).ConfigureAwait(false);
                        RemoteFrame auth = await ReadWithTimeoutAsync(stream, linked.Token).ConfigureAwait(false);
                        byte[] expected = RemoteExecutionProtocol.ComputeAuthentication(nonce, m_Token);
                        if (auth.Kind != RemoteMessageKind.Authenticate || !RemoteExecutionProtocol.FixedTimeEquals(auth.Payload, expected))
                            throw new InvalidDataException("Authentication failed.");
                        m_Authenticated = true;
                        m_Status = "Authenticated";
                        await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Ready, auth.RequestId, Array.Empty<byte>()), linked.Token).ConfigureAwait(false);
                        while (!linked.IsCancellationRequested)
                        {
                            RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(stream, linked.Token).ConfigureAwait(false);
                            HandleResponse(frame);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        m_Status = "Error: " + exception.Message;
                        FailPending(exception);
                        Debug.LogWarning($"[HybridCLR.RemoteExecution] client {Id} stopped: {exception.Message}");
                    }
                }
                Dispose();
                lock (s_Lock) s_Sessions.Remove(Id);
            }

            private void HandleResponse(RemoteFrame frame)
            {
                if (frame.Kind == RemoteMessageKind.Ping)
                {
                    SendAsync(m_Client.GetStream(), new RemoteFrame(RemoteMessageKind.Pong, frame.RequestId, Array.Empty<byte>()),
                        m_Cancellation.Token).Forget();
                    return;
                }
                if (frame.Kind != RemoteMessageKind.ApplyResult && frame.Kind != RemoteMessageKind.InvokeResult && frame.Kind != RemoteMessageKind.Error)
                {
                    SendAsync(m_Client.GetStream(), new RemoteFrame(RemoteMessageKind.Error, frame.RequestId,
                        RemoteExecutionProtocol.EncodeError("UNKNOWN_MESSAGE", frame.Kind.ToString())), m_Cancellation.Token).Forget();
                    return;
                }

                try
                {
                    bool succeeded;
                    string code;
                    string message;
                    RemoteExecutionProtocol.DecodeResult(frame.Payload, out succeeded, out code, out message);
                    if (frame.Kind == RemoteMessageKind.Error) succeeded = false;
                    var response = new RemoteResponse(succeeded, code, message);
                    if (m_Pending.TryRemove(frame.RequestId, out TaskCompletionSource<RemoteResponse> completion))
                        completion.TrySetResult(response);
                }
                catch (Exception exception)
                {
                    if (m_Pending.TryRemove(frame.RequestId, out TaskCompletionSource<RemoteResponse> completion))
                        completion.TrySetException(exception);
                }
            }

            internal async Task SendBundleAndInvokeAsync(RemoteExecutionBundle bundle)
            {
                await m_OperationLock.WaitAsync(m_Cancellation.Token).ConfigureAwait(false);
                try
                {
                    m_Status = "Sending bundle";
                    Guid requestId = Guid.NewGuid();
                    Task<RemoteResponse> applyTask = RegisterPending(requestId);
                    NetworkStream stream = m_Client.GetStream();
                    await SendAsync(stream, new RemoteFrame(RemoteMessageKind.LoadManifest, requestId,
                        RemoteExecutionProtocol.EncodeManifest(bundle.ToManifest())), m_Cancellation.Token).ConfigureAwait(false);
                    for (int i = 0; i < bundle.Artifacts.Count; i++)
                    {
                        RemoteExecutionArtifact artifact = bundle.Artifacts[i];
                        await SendArtifactAsync(stream, bundle.BundleId, i, false, artifact.Dll, artifact.DllSha256,
                            m_Cancellation.Token).ConfigureAwait(false);
                        if (artifact.Pdb != null && artifact.Pdb.Length > 0)
                            await SendArtifactAsync(stream, bundle.BundleId, i, true, artifact.Pdb, artifact.PdbSha256,
                                m_Cancellation.Token).ConfigureAwait(false);
                    }
                    await SendAsync(stream, new RemoteFrame(RemoteMessageKind.LoadComplete, requestId,
                        RemoteExecutionProtocol.EncodeBundleComplete(bundle.BundleId)), m_Cancellation.Token).ConfigureAwait(false);
                    RemoteResponse applied = await WaitForResponseAsync(requestId, applyTask, m_Cancellation.Token).ConfigureAwait(false);
                    if (!applied.Succeeded) throw new InvalidOperationException($"Player load failed [{applied.Code}]: {applied.Message}");

                    m_Status = "Executing custom code";
                    Guid invokeRequestId = Guid.NewGuid();
                    Task<RemoteResponse> invokeTask = RegisterPending(invokeRequestId);
                    await SendAsync(stream, new RemoteFrame(RemoteMessageKind.Invoke, invokeRequestId,
                        RemoteExecutionProtocol.EncodeInvoke(bundle.EntryMethodId)), m_Cancellation.Token).ConfigureAwait(false);
                    RemoteResponse invoked = await WaitForResponseAsync(invokeRequestId, invokeTask, m_Cancellation.Token).ConfigureAwait(false);
                    if (!invoked.Succeeded) throw new InvalidOperationException($"Player execution failed [{invoked.Code}]: {invoked.Message}");
                    m_Status = "Completed";
                }
                catch
                {
                    m_Status = "Error";
                    throw;
                }
                finally
                {
                    m_OperationLock.Release();
                }
            }

            private async Task SendArtifactAsync(NetworkStream stream, Guid bundleId, int index, bool pdb,
                byte[] bytes, byte[] hash, CancellationToken cancellationToken)
            {
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyBegin, Guid.NewGuid(),
                    RemoteExecutionProtocol.EncodeAssemblyBegin(bundleId, index, pdb, bytes.LongLength, hash)), cancellationToken).ConfigureAwait(false);
                for (int offset = 0; offset < bytes.Length; offset += RemoteExecutionProtocol.MaxChunkBytes)
                {
                    int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes, bytes.Length - offset);
                    await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyChunk, Guid.NewGuid(),
                        RemoteExecutionProtocol.EncodeChunk(bundleId, index, pdb, offset, bytes, count)), cancellationToken).ConfigureAwait(false);
                }
                await SendAsync(stream, new RemoteFrame(RemoteMessageKind.AssemblyEnd, Guid.NewGuid(),
                    RemoteExecutionProtocol.EncodeAssemblyEnd(bundleId, index, pdb)), cancellationToken).ConfigureAwait(false);
            }

            private TaskCompletionSource<RemoteResponse> RegisterPending(Guid requestId)
            {
                var completion = new TaskCompletionSource<RemoteResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!m_Pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Duplicate remote request ID.");
                return completion;
            }

            private async Task<RemoteResponse> WaitForResponseAsync(Guid requestId,
                Task<RemoteResponse> responseTask, CancellationToken cancellationToken)
            {
                Task timeout = Task.Delay(TimeSpan.FromSeconds(OperationTimeoutSeconds), cancellationToken);
                Task completed = await Task.WhenAny(responseTask, timeout).ConfigureAwait(false);
                if (completed != responseTask)
                {
                    m_Pending.TryRemove(requestId, out _);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for the Player response.");
                }
                return await responseTask.ConfigureAwait(false);
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

            private void FailPending(Exception exception)
            {
                foreach (KeyValuePair<Guid, TaskCompletionSource<RemoteResponse>> item in m_Pending.ToArray())
                    if (m_Pending.TryRemove(item.Key, out TaskCompletionSource<RemoteResponse> completion))
                        completion.TrySetException(exception);
            }

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                m_Cancellation.Cancel();
                m_Client.Close();
                FailPending(new IOException("Remote execution client disconnected."));
                m_OperationLock.Dispose();
                m_Cancellation.Dispose();
            }
        }

        private sealed class RemoteResponse
        {
            internal RemoteResponse(bool succeeded, string code, string message)
            {
                Succeeded = succeeded;
                Code = code;
                Message = message;
            }
            internal bool Succeeded { get; }
            internal string Code { get; }
            internal string Message { get; }
        }
    }

    internal static class RemoteExecutionEditorTaskExtensions
    {
        internal static void Forget(this Task task) { _ = task; }
    }
}
