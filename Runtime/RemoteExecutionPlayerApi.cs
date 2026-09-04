using System;
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

    public sealed class RemoteExecutionPlayerOptions
    {
        public RemoteExecutionPlayerOptions(IRemoteExecutionConnector connector = null,
            string clientId = null,
            int maxCommandRequestBytes =
                RemoteExecutionProtocol.DefaultMaxCommandRequestBytes,
            int maxCommandResponseBytes =
                RemoteExecutionProtocol.DefaultMaxCommandResponseBytes,
            TimeSpan? connectTimeout = null,
            TimeSpan? handshakeTimeout = null)
        {
            Connector = connector;
            ClientId = clientId;
            MaxCommandRequestBytes = maxCommandRequestBytes;
            MaxCommandResponseBytes = maxCommandResponseBytes;
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
            HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15);
        }

        public IRemoteExecutionConnector Connector { get; }
        public string ClientId { get; }
        public int MaxCommandRequestBytes { get; }
        public int MaxCommandResponseBytes { get; }
        public TimeSpan ConnectTimeout { get; }
        public TimeSpan HandshakeTimeout { get; }
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
            Start(new RemoteExecutionPlayerOptions(
                new RemoteExecutionTcpConnector(editorHost, editorPort), clientId,
                maxCommandRequestBytes, maxCommandResponseBytes));
        }

        public static void Start(RemoteExecutionPlayerOptions options)
        {
            EnsureMainThread();
            if (options == null) throw new ArgumentNullException(nameof(options));
#if UNITY_EDITOR
            throw new PlatformNotSupportedException(
                "The Remote Execution Player client is not available in the Unity Editor.");
#else
            RemoteExecutionPlayerConfiguration configuration = CreateConfiguration(options);
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

        private static RemoteExecutionPlayerConfiguration CreateConfiguration(
            RemoteExecutionPlayerOptions options)
        {
            IRemoteExecutionConnector connector = options.Connector ??
                new RemoteExecutionTcpConnector();
            string connectionKey = (connector.ConnectionKey ?? string.Empty).Trim();
            if (connectionKey.Length == 0)
                throw new ArgumentException("Connector connection key is required.",
                    nameof(options));
            if (GetUtf8ByteCount(connectionKey) > RemoteExecutionProtocol.MaxStringBytes)
                throw new ArgumentException("Connector connection key is too long.",
                    nameof(options));
            if (options.MaxCommandRequestBytes < 0 ||
                options.MaxCommandRequestBytes > RemoteExecutionProtocol.MaxCommandRequestBytes)
                throw new ArgumentOutOfRangeException(nameof(options.MaxCommandRequestBytes));
            if (options.MaxCommandResponseBytes < 0 ||
                options.MaxCommandResponseBytes > RemoteExecutionProtocol.MaxCommandResponseBytes)
                throw new ArgumentOutOfRangeException(nameof(options.MaxCommandResponseBytes));
            ValidateTimeout(options.ConnectTimeout, nameof(options.ConnectTimeout));
            ValidateTimeout(options.HandshakeTimeout, nameof(options.HandshakeTimeout));

            string resolvedClientId = string.IsNullOrWhiteSpace(options.ClientId)
                ? ResolveClientId() : options.ClientId.Trim();
            if (string.IsNullOrWhiteSpace(resolvedClientId) ||
                GetUtf8ByteCount(resolvedClientId) > RemoteExecutionProtocol.MaxStringBytes)
                throw new ArgumentException(
                    $"Client ID must contain 1..{RemoteExecutionProtocol.MaxStringBytes} UTF-8 bytes.",
                    nameof(options.ClientId));

            return new RemoteExecutionPlayerConfiguration(connector, connectionKey,
                resolvedClientId, options.MaxCommandRequestBytes,
                options.MaxCommandResponseBytes, options.ConnectTimeout,
                options.HandshakeTimeout, Application.unityVersion, GetRuntimeTarget());
        }

        private static void ValidateTimeout(TimeSpan timeout, string parameterName)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(parameterName,
                    "Timeout must be greater than zero and no more than one hour.");
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

    internal sealed class RemoteExecutionPlayerConfiguration :
        IEquatable<RemoteExecutionPlayerConfiguration>
    {
        internal RemoteExecutionPlayerConfiguration(IRemoteExecutionConnector connector,
            string connectionKey, string clientId, int maxCommandRequestBytes,
            int maxCommandResponseBytes, TimeSpan connectTimeout,
            TimeSpan handshakeTimeout, string unityVersion, string target)
        {
            Connector = connector;
            ConnectionKey = connectionKey;
            ClientId = clientId;
            MaxCommandRequestBytes = maxCommandRequestBytes;
            MaxCommandResponseBytes = maxCommandResponseBytes;
            ConnectTimeout = connectTimeout;
            HandshakeTimeout = handshakeTimeout;
            UnityVersion = unityVersion;
            Target = target;
        }

        internal IRemoteExecutionConnector Connector { get; }
        internal string ConnectionKey { get; }
        internal string ClientId { get; }
        internal int MaxCommandRequestBytes { get; }
        internal int MaxCommandResponseBytes { get; }
        internal TimeSpan ConnectTimeout { get; }
        internal TimeSpan HandshakeTimeout { get; }
        internal string UnityVersion { get; }
        internal string Target { get; }

        public bool Equals(RemoteExecutionPlayerConfiguration other)
        {
            return other != null &&
                string.Equals(ConnectionKey, other.ConnectionKey, StringComparison.Ordinal) &&
                string.Equals(ClientId, other.ClientId, StringComparison.Ordinal) &&
                MaxCommandRequestBytes == other.MaxCommandRequestBytes &&
                MaxCommandResponseBytes == other.MaxCommandResponseBytes &&
                ConnectTimeout == other.ConnectTimeout &&
                HandshakeTimeout == other.HandshakeTimeout &&
                string.Equals(UnityVersion, other.UnityVersion, StringComparison.Ordinal) &&
                string.Equals(Target, other.Target, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) =>
            Equals(obj as RemoteExecutionPlayerConfiguration);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(ConnectionKey);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ClientId);
                hash = (hash * 397) ^ MaxCommandRequestBytes;
                hash = (hash * 397) ^ MaxCommandResponseBytes;
                hash = (hash * 397) ^ ConnectTimeout.GetHashCode();
                hash = (hash * 397) ^ HandshakeTimeout.GetHashCode();
                return hash;
            }
        }
    }
}
