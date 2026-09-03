using System;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace RemoteExecution
{
    public enum RemoteExecutionConnectionState
    {
        Disconnected,
        Connecting,
        Handshaking,
        Connected,
        Faulted
    }

    public sealed class RemoteExecutionConnectionError
    {
        internal RemoteExecutionConnectionError(string code, string message)
        {
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public string Code { get; }
        public string Message { get; }
    }

    public static class RemoteExecutionPlayerApi
    {
        private static readonly object s_Lock = new object();
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);
        private static RemoteExecutionPlayerDriver s_Driver;
        private static RemoteExecutionConnectionState s_ConnectionState;
        private static RemoteExecutionConnectionError s_LastError;
        private static Action<RemoteExecutionConnectionState> s_ConnectionStateChanged;
        private static int s_MainThreadId;
        private static string s_FallbackClientId;

        public static RemoteExecutionConnectionState ConnectionState
        {
            get { lock (s_Lock) return s_ConnectionState; }
        }

        public static bool IsConnected => ConnectionState == RemoteExecutionConnectionState.Connected;

        public static RemoteExecutionConnectionError LastError
        {
            get { lock (s_Lock) return s_LastError; }
        }

        public static event Action<RemoteExecutionConnectionState> ConnectionStateChanged
        {
            add { lock (s_Lock) s_ConnectionStateChanged += value; }
            remove { lock (s_Lock) s_ConnectionStateChanged -= value; }
        }

        public static void Start(string editorHost, int editorPort = 38421,
            string clientId = null,
            int maxCommandRequestBytes = RemoteExecutionProtocol.DefaultMaxCommandRequestBytes,
            int maxCommandResponseBytes = RemoteExecutionProtocol.DefaultMaxCommandResponseBytes)
        {
            EnsureMainThread();
#if UNITY_EDITOR
            throw new PlatformNotSupportedException(
                "The Remote Execution Player client is not available in the Unity Editor.");
#else
            RemoteExecutionPlayerConfiguration configuration = CreateConfiguration(editorHost,
                editorPort, clientId, maxCommandRequestBytes, maxCommandResponseBytes);
            RemoteExecutionPlayerDriver driver;
            lock (s_Lock) driver = s_Driver;
            if (driver == null)
            {
                var gameObject = new GameObject("[Unity Remote Execution]")
                {
                    hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
                };
                driver = gameObject.AddComponent<RemoteExecutionPlayerDriver>();
                try { driver.Initialize(); }
                catch
                {
                    UnityEngine.Object.Destroy(gameObject);
                    throw;
                }
                lock (s_Lock) s_Driver = driver;
            }
            driver.StartConnection(configuration);
#endif
        }

        public static void Stop()
        {
            EnsureMainThread();
#if !UNITY_EDITOR
            RemoteExecutionPlayerDriver driver;
            lock (s_Lock) driver = s_Driver;
            if (driver != null) driver.StopConnection();
#endif
        }

        internal static void SetState(RemoteExecutionPlayerDriver driver, long generation,
            RemoteExecutionConnectionState state, RemoteExecutionConnectionError error)
        {
            Action<RemoteExecutionConnectionState> handlers;
            lock (s_Lock)
            {
                if (!ReferenceEquals(s_Driver, driver) || driver.Generation != generation) return;
                if (s_ConnectionState == state && ReferenceEquals(s_LastError, error)) return;
                s_LastError = state == RemoteExecutionConnectionState.Faulted ? error : null;
                s_ConnectionState = state;
                handlers = s_ConnectionStateChanged;
            }
            InvokeStateChanged(handlers, state);
        }

        internal static void DriverDestroyed(RemoteExecutionPlayerDriver driver)
        {
            Action<RemoteExecutionConnectionState> handlers;
            bool changed;
            lock (s_Lock)
            {
                if (!ReferenceEquals(s_Driver, driver)) return;
                s_Driver = null;
                changed = s_ConnectionState != RemoteExecutionConnectionState.Disconnected ||
                    s_LastError != null;
                s_ConnectionState = RemoteExecutionConnectionState.Disconnected;
                s_LastError = null;
                handlers = changed ? s_ConnectionStateChanged : null;
            }
            InvokeStateChanged(handlers, RemoteExecutionConnectionState.Disconnected);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            lock (s_Lock)
            {
                s_Driver = null;
                s_ConnectionState = RemoteExecutionConnectionState.Disconnected;
                s_LastError = null;
                s_ConnectionStateChanged = null;
                s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
                s_FallbackClientId = null;
            }
        }

        private static RemoteExecutionPlayerConfiguration CreateConfiguration(string editorHost,
            int editorPort, string clientId, int maxCommandRequestBytes,
            int maxCommandResponseBytes)
        {
            string host = (editorHost ?? string.Empty).Trim();
            if (host.Length == 0)
                throw new ArgumentException("Editor host is required.", nameof(editorHost));
            if (host == "*" || IsWildcardAddress(host))
                throw new ArgumentException("A wildcard address cannot be used as an Editor destination.",
                    nameof(editorHost));
            if (editorPort < 1 || editorPort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(editorPort),
                    "Editor port must be in range 1..65535.");
            if (maxCommandRequestBytes < 0 ||
                maxCommandRequestBytes > RemoteExecutionProtocol.MaxCommandRequestBytes)
                throw new ArgumentOutOfRangeException(nameof(maxCommandRequestBytes));
            if (maxCommandResponseBytes < 0 ||
                maxCommandResponseBytes > RemoteExecutionProtocol.MaxCommandResponseBytes)
                throw new ArgumentOutOfRangeException(nameof(maxCommandResponseBytes));

            string resolvedClientId = string.IsNullOrWhiteSpace(clientId)
                ? ResolveClientId() : clientId.Trim();
            if (string.IsNullOrWhiteSpace(resolvedClientId) ||
                GetUtf8ByteCount(resolvedClientId) > RemoteExecutionProtocol.MaxStringBytes)
                throw new ArgumentException(
                    $"Client ID must contain 1..{RemoteExecutionProtocol.MaxStringBytes} UTF-8 bytes.",
                    nameof(clientId));

            return new RemoteExecutionPlayerConfiguration(host, editorPort, resolvedClientId,
                maxCommandRequestBytes, maxCommandResponseBytes, Application.unityVersion,
                GetRuntimeTarget());
        }

        private static string ResolveClientId()
        {
            string value = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrWhiteSpace(value)) value = SystemInfo.deviceName;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            lock (s_Lock)
            {
                if (string.IsNullOrEmpty(s_FallbackClientId))
                    s_FallbackClientId = $"Player-{Guid.NewGuid():N}";
                return s_FallbackClientId;
            }
        }

        private static bool IsWildcardAddress(string host)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress address)) return false;
            return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        }

        private static int GetUtf8ByteCount(string value)
        {
            try { return s_Utf8.GetByteCount(value ?? string.Empty); }
            catch (EncoderFallbackException) { return int.MaxValue; }
        }

        private static void EnsureMainThread()
        {
            int currentThreadId = Thread.CurrentThread.ManagedThreadId;
            lock (s_Lock)
            {
                if (s_MainThreadId == 0) s_MainThreadId = currentThreadId;
                if (s_MainThreadId == currentThreadId) return;
            }
            throw new InvalidOperationException(
                "Remote Execution Player lifecycle methods must be called on the Unity main thread.");
        }

        private static void InvokeStateChanged(Action<RemoteExecutionConnectionState> handlers,
            RemoteExecutionConnectionState state)
        {
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<RemoteExecutionConnectionState>)handler)(state); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
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
    }

    internal sealed class RemoteExecutionPlayerConfiguration : IEquatable<RemoteExecutionPlayerConfiguration>
    {
        internal RemoteExecutionPlayerConfiguration(string editorHost, int editorPort,
            string clientId, int maxCommandRequestBytes, int maxCommandResponseBytes,
            string unityVersion, string target)
        {
            EditorHost = editorHost;
            EditorPort = editorPort;
            ClientId = clientId;
            MaxCommandRequestBytes = maxCommandRequestBytes;
            MaxCommandResponseBytes = maxCommandResponseBytes;
            UnityVersion = unityVersion;
            Target = target;
        }

        internal string EditorHost { get; }
        internal int EditorPort { get; }
        internal string ClientId { get; }
        internal int MaxCommandRequestBytes { get; }
        internal int MaxCommandResponseBytes { get; }
        internal string UnityVersion { get; }
        internal string Target { get; }

        public bool Equals(RemoteExecutionPlayerConfiguration other)
        {
            return other != null &&
                string.Equals(EditorHost, other.EditorHost, StringComparison.OrdinalIgnoreCase) &&
                EditorPort == other.EditorPort &&
                string.Equals(ClientId, other.ClientId, StringComparison.Ordinal) &&
                MaxCommandRequestBytes == other.MaxCommandRequestBytes &&
                MaxCommandResponseBytes == other.MaxCommandResponseBytes &&
                string.Equals(UnityVersion, other.UnityVersion, StringComparison.Ordinal) &&
                string.Equals(Target, other.Target, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as RemoteExecutionPlayerConfiguration);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(EditorHost);
                hash = (hash * 397) ^ EditorPort;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ClientId);
                hash = (hash * 397) ^ MaxCommandRequestBytes;
                hash = (hash * 397) ^ MaxCommandResponseBytes;
                return hash;
            }
        }
    }
}
