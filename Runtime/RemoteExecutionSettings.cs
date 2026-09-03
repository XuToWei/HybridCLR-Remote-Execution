using UnityEngine;

namespace RemoteExecution
{
    [CreateAssetMenu(menuName = "Unity/Remote Execution Settings", fileName = "RemoteExecutionSettings")]
    public sealed class RemoteExecutionSettings : ScriptableObject
    {
        [Header("Connection")]
        [SerializeField] private bool m_Enabled = true;
        [SerializeField] private string m_EditorHost = "127.0.0.1";
        [SerializeField] private int m_EditorPort = 38421;
        [SerializeField] private string m_ClientId = "";
        [SerializeField] private int m_MaxCommandRequestBytes = RemoteExecutionProtocol.MaxCommandRequestBytes;
        [SerializeField] private int m_MaxCommandResponseBytes = RemoteExecutionProtocol.DefaultMaxCommandResponseBytes;

        public bool Enabled => m_Enabled;
        public string EditorHost => m_EditorHost;
        public int EditorPort => m_EditorPort;
        public string ClientId => m_ClientId;
        public int MaxCommandRequestBytes => Mathf.Clamp(m_MaxCommandRequestBytes, 0, RemoteExecutionProtocol.MaxCommandRequestBytes);
        public int MaxCommandResponseBytes => Mathf.Clamp(m_MaxCommandResponseBytes, 0, RemoteExecutionProtocol.MaxCommandResponseBytes);
    }
}
