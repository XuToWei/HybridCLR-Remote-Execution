using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HybridCLR;
using UnityEngine;

namespace HybridCLR.RemoteExecution
{
    /// <summary>Development Player client for the HybridCLR Remote Execution editor host.</summary>
    public sealed class RemoteExecutionComponent : MonoBehaviour
    {
        [SerializeField] private RemoteExecutionSettings m_Configuration;
        private readonly Queue<Action> m_MainThreadActions = new Queue<Action>();
        private readonly object m_ActionLock = new object();
        private readonly Dictionary<string, RemoteCallableDescriptor> m_Methods = new Dictionary<string, RemoteCallableDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, Assembly> m_Assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> m_AssemblyHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Queue<RemoteFrame> m_SendQueue = new Queue<RemoteFrame>();
        private readonly object m_SendLock = new object();
        private CancellationTokenSource m_Cancellation;
        private SemaphoreSlim m_SendSignal;
        private TcpClient m_Client;
        private NetworkStream m_Stream;
        private bool m_Authenticated;
        private bool m_InvocationRunning;
        private IncomingBundle m_IncomingBundle;

        public bool IsConnected => m_Client != null && m_Client.Connected && m_Authenticated;

        private void Awake()
        {
#if UNITY_EDITOR
            enabled = false;
            return;
#else
            if (m_Configuration == null || !m_Configuration.Enabled || !Debug.isDebugBuild || string.IsNullOrEmpty(m_Configuration.AuthenticationToken))
            {
                enabled = false;
                return;
            }
            DontDestroyOnLoad(gameObject);
            RefreshMethods(AppDomain.CurrentDomain.GetAssemblies());
#if ENABLE_IL2CPP
            if (m_Configuration.LoadAotMetadata)
            {
                foreach (TextAsset asset in m_Configuration.AotMetadataAssemblies ?? Array.Empty<TextAsset>())
                {
                    if (asset != null) RuntimeApi.LoadMetadataForAOTAssembly(asset.bytes, HomologousImageMode.Consistent);
                }
            }
#endif
            m_Cancellation = new CancellationTokenSource();
            m_SendSignal = new SemaphoreSlim(0);
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
        }

        private void OnDestroy() { Disconnect(); }

        public void Disconnect()
        {
            m_Cancellation?.Cancel();
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
            m_Authenticated = false;
            m_IncomingBundle = null;
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
                    HybridCLRVersion = "HybridCLR"
                };
                await RemoteExecutionProtocol.WriteFrameAsync(m_Stream,
                    new RemoteFrame(RemoteMessageKind.Hello, Guid.NewGuid(), RemoteExecutionProtocol.EncodeHello(hello)), cancellationToken).ConfigureAwait(false);
                Task receiveTask = ReceiveLoopAsync(cancellationToken);
                Task sendTask = SendLoopAsync(cancellationToken);
                await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Remote execution connection stopped: {exception.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RemoteFrame frame = await RemoteExecutionProtocol.ReadFrameAsync(m_Stream, cancellationToken).ConfigureAwait(false);
                if (frame.Kind == RemoteMessageKind.Challenge)
                {
                    byte[] nonce = RemoteExecutionProtocol.DecodeChallenge(frame.Payload);
                    Send(RemoteMessageKind.Authenticate, frame.RequestId,
                        RemoteExecutionProtocol.ComputeAuthentication(nonce, m_Configuration.AuthenticationToken));
                }
                else if (frame.Kind == RemoteMessageKind.Ready)
                {
                    m_Authenticated = true;
                }
                else
                {
                    EnqueueMainThread(() => HandleFrame(frame));
                }
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
            if (!m_Authenticated) return;
            try
            {
                switch (frame.Kind)
                {
                    case RemoteMessageKind.ListMethods:
                        Send(RemoteMessageKind.Methods, frame.RequestId, EncodeMethods());
                        break;
                    case RemoteMessageKind.Invoke:
                        HandleInvoke(frame);
                        break;
                    case RemoteMessageKind.LoadManifest:
                        BeginBundle(frame);
                        break;
                    case RemoteMessageKind.AssemblyBegin:
                        BeginAssembly(frame);
                        break;
                    case RemoteMessageKind.AssemblyChunk:
                        WriteChunk(frame);
                        break;
                    case RemoteMessageKind.AssemblyEnd:
                        EndAssembly(frame);
                        break;
                    case RemoteMessageKind.LoadComplete:
                        CompleteBundle(frame);
                        break;
                    case RemoteMessageKind.Ping:
                        Send(RemoteMessageKind.Pong, frame.RequestId, Array.Empty<byte>());
                        break;
                }
            }
            catch (Exception exception)
            {
                Guid responseRequestId = frame.Kind == RemoteMessageKind.Invoke
                    ? frame.RequestId : m_IncomingBundle?.RequestId ?? frame.RequestId;
                m_IncomingBundle = null;
                RemoteMessageKind resultKind = frame.Kind == RemoteMessageKind.Invoke
                    ? RemoteMessageKind.InvokeResult : RemoteMessageKind.ApplyResult;
                Send(resultKind, responseRequestId,
                    RemoteExecutionProtocol.EncodeResult(false, "PROTOCOL_ERROR", exception.Message));
            }
        }

        private void HandleInvoke(RemoteFrame frame)
        {
            if (m_InvocationRunning)
            {
                Send(RemoteMessageKind.InvokeResult, frame.RequestId,
                    RemoteExecutionProtocol.EncodeResult(false, "INVOCATION_BUSY", "Another remote invocation is running."));
                return;
            }
            string methodId = RemoteExecutionProtocol.DecodeInvoke(frame.Payload);
            if (!m_Methods.TryGetValue(methodId, out RemoteCallableDescriptor descriptor))
            {
                Send(RemoteMessageKind.InvokeResult, frame.RequestId,
                    RemoteExecutionProtocol.EncodeResult(false, "METHOD_NOT_FOUND", methodId));
                return;
            }
            m_InvocationRunning = true;
            InvokeAsync(frame.RequestId, descriptor).Forget();
        }

        private async Task InvokeAsync(Guid requestId, RemoteCallableDescriptor descriptor)
        {
            try
            {
                await RemoteCallableRegistry.InvokeAsync(descriptor);
                Send(RemoteMessageKind.InvokeResult, requestId, RemoteExecutionProtocol.EncodeResult(true, "", ""));
            }
            catch (Exception exception)
            {
                Send(RemoteMessageKind.InvokeResult, requestId,
                    RemoteExecutionProtocol.EncodeResult(false, "METHOD_EXECUTION_FAILED", exception.Message));
            }
            finally
            {
                m_InvocationRunning = false;
            }
        }

        private void BeginBundle(RemoteFrame frame)
        {
            if (m_IncomingBundle != null) throw new InvalidDataException("Another bundle is already being received.");
            RemoteBundleManifest manifest = RemoteExecutionProtocol.DecodeManifest(frame.Payload);
            string target = GetRuntimeTarget();
            if (!string.Equals(manifest.Target, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Bundle target '{manifest.Target}' does not match '{target}'.");
            long total = 0;
            foreach (RemoteAssemblyInfo assembly in manifest.Assemblies) total = checked(total + assembly.DllLength + assembly.PdbLength);
            if (total > m_Configuration.MaxBundleBytes) throw new InvalidDataException("Bundle exceeds the configured size limit.");
            m_IncomingBundle = new IncomingBundle(manifest, frame.RequestId);
        }

        private void BeginAssembly(RemoteFrame frame)
        {
            EnsureBundle();
            RemoteExecutionProtocol.DecodeAssemblyBegin(frame.Payload, out Guid bundleId, out int index, out bool pdb, out long length, out byte[] hash);
            if (bundleId != m_IncomingBundle.Manifest.BundleId || index < 0 || index >= m_IncomingBundle.Assemblies.Length)
                throw new InvalidDataException("Assembly does not belong to the active bundle.");
            RemoteAssemblyInfo info = m_IncomingBundle.Manifest.Assemblies[index];
            long expectedLength = pdb ? info.PdbLength : info.DllLength;
            if (length != expectedLength || !RemoteExecutionProtocol.FixedTimeEquals(hash, pdb ? info.PdbSha256 : info.DllSha256))
                throw new InvalidDataException("Assembly metadata does not match the manifest.");
            IncomingAssembly received = m_IncomingBundle.Assemblies[index];
            if (pdb ? received.Pdb != null : received.Dll != null) throw new InvalidDataException("Duplicate assembly begin.");
            var stream = new MemoryStream(checked((int)length));
            if (pdb) received.Pdb = stream; else received.Dll = stream;
        }

        private void WriteChunk(RemoteFrame frame)
        {
            EnsureBundle();
            RemoteExecutionProtocol.DecodeChunk(frame.Payload, out Guid bundleId, out int index, out bool pdb, out long offset, out byte[] data);
            if (bundleId != m_IncomingBundle.Manifest.BundleId || index < 0 || index >= m_IncomingBundle.Assemblies.Length)
                throw new InvalidDataException("Chunk does not belong to the active bundle.");
            MemoryStream stream = pdb ? m_IncomingBundle.Assemblies[index].Pdb : m_IncomingBundle.Assemblies[index].Dll;
            if (stream == null || stream.Position != offset || stream.Length + data.Length > stream.Capacity)
                throw new InvalidDataException("Assembly chunk is out of order or too large.");
            stream.Write(data, 0, data.Length);
        }

        private void EndAssembly(RemoteFrame frame)
        {
            EnsureBundle();
            RemoteExecutionProtocol.DecodeAssemblyEnd(frame.Payload, out Guid bundleId, out int index, out bool pdb);
            if (bundleId != m_IncomingBundle.Manifest.BundleId || index < 0 || index >= m_IncomingBundle.Assemblies.Length)
                throw new InvalidDataException("Assembly end does not belong to the active bundle.");
            IncomingAssembly received = m_IncomingBundle.Assemblies[index];
            MemoryStream stream = pdb ? received.Pdb : received.Dll;
            if (stream == null || (pdb ? received.PdbComplete : received.DllComplete)) throw new InvalidDataException("Invalid assembly end.");
            RemoteAssemblyInfo info = m_IncomingBundle.Manifest.Assemblies[index];
            if (stream.Length != (pdb ? info.PdbLength : info.DllLength)) throw new InvalidDataException("Assembly length does not match.");
            byte[] actual;
            using (var sha = SHA256.Create()) actual = sha.ComputeHash(stream.ToArray());
            if (!RemoteExecutionProtocol.FixedTimeEquals(actual, pdb ? info.PdbSha256 : info.DllSha256)) throw new InvalidDataException("Assembly hash does not match.");
            if (pdb) received.PdbComplete = true; else received.DllComplete = true;
        }

        private void CompleteBundle(RemoteFrame frame)
        {
            EnsureBundle();
            Guid bundleId = RemoteExecutionProtocol.DecodeBundleComplete(frame.Payload);
            if (bundleId != m_IncomingBundle.Manifest.BundleId || !m_IncomingBundle.IsComplete())
                throw new InvalidDataException("Bundle is incomplete.");
            ApplyBundle();
        }

        private void ApplyBundle()
        {
            IncomingBundle bundle = m_IncomingBundle;
            try
            {
                var loaded = new List<Assembly>();
                for (int i = 0; i < bundle.Assemblies.Length; i++)
                {
                    RemoteAssemblyInfo info = bundle.Manifest.Assemblies[i];
                    byte[] dll = bundle.Assemblies[i].Dll.ToArray();
                    if (m_Assemblies.TryGetValue(info.Name, out Assembly _))
                    {
                        if (!RemoteExecutionProtocol.FixedTimeEquals(m_AssemblyHashes[info.Name], info.DllSha256))
                            throw new InvalidOperationException($"Assembly '{info.Name}' is already loaded with another version.");
                        continue;
                    }
                    Assembly assembly = info.PdbLength > 0 ? Assembly.Load(dll, bundle.Assemblies[i].Pdb.ToArray()) : Assembly.Load(dll);
                    m_Assemblies.Add(info.Name, assembly);
                    m_AssemblyHashes.Add(info.Name, (byte[])info.DllSha256.Clone());
                    loaded.Add(assembly);
                }
                RefreshMethods(loaded);
                m_IncomingBundle = null;
                Send(RemoteMessageKind.ApplyResult, bundle.RequestId,
                    RemoteExecutionProtocol.EncodeResult(true, "", bundle.Manifest.Generation));
            }
            catch (Exception exception)
            {
                m_IncomingBundle = null;
                Send(RemoteMessageKind.ApplyResult, bundle.RequestId,
                    RemoteExecutionProtocol.EncodeResult(false, "LOAD_FAILED", exception.Message));
            }
        }

        private void RefreshMethods(IEnumerable<Assembly> additionalAssemblies)
        {
            var assemblies = new List<Assembly>();
            var seen = new HashSet<Assembly>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly != null && seen.Add(assembly)) assemblies.Add(assembly);
            if (additionalAssemblies != null)
            {
                foreach (Assembly assembly in additionalAssemblies)
                    if (assembly != null && seen.Add(assembly)) assemblies.Add(assembly);
            }
            IReadOnlyList<RemoteCallableDescriptor> descriptors = RemoteCallableRegistry.Discover(assemblies);
            m_Methods.Clear();
            foreach (RemoteCallableDescriptor descriptor in descriptors) m_Methods.Add(descriptor.Id, descriptor);
        }

        private byte[] EncodeMethods()
        {
            var methods = new List<RemoteMethodInfo>(m_Methods.Count);
            foreach (RemoteCallableDescriptor descriptor in m_Methods.Values)
                methods.Add(new RemoteMethodInfo { Id = descriptor.Id, Description = descriptor.Description, TimeoutSeconds = descriptor.TimeoutSeconds });
            return RemoteExecutionProtocol.EncodeMethods(methods);
        }

        private void Send(RemoteMessageKind kind, Guid requestId, byte[] payload)
        {
            if (m_Cancellation == null || m_SendSignal == null) return;
            lock (m_SendLock) m_SendQueue.Enqueue(new RemoteFrame(kind, requestId, payload));
            try { m_SendSignal.Release(); } catch (ObjectDisposedException) { }
        }

        private void EnsureBundle()
        {
            if (m_IncomingBundle == null) throw new InvalidDataException("No active bundle.");
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

        private sealed class IncomingBundle
        {
            internal IncomingBundle(RemoteBundleManifest manifest, Guid requestId)
            {
                Manifest = manifest;
                RequestId = requestId;
                Assemblies = new IncomingAssembly[manifest.Assemblies.Length];
                for (int i = 0; i < Assemblies.Length; i++) Assemblies[i] = new IncomingAssembly();
            }
            internal RemoteBundleManifest Manifest { get; }
            internal Guid RequestId { get; }
            internal IncomingAssembly[] Assemblies { get; }
            internal bool IsComplete()
            {
                foreach (IncomingAssembly assembly in Assemblies)
                    if (!assembly.DllComplete || (assembly.Pdb != null && !assembly.PdbComplete)) return false;
                return true;
            }
        }

        private sealed class IncomingAssembly
        {
            internal MemoryStream Dll;
            internal MemoryStream Pdb;
            internal bool DllComplete;
            internal bool PdbComplete;
        }
    }

    internal static class RemoteExecutionTaskExtensions
    {
        public static void Forget(this Task task) { _ = task; }
    }
}
