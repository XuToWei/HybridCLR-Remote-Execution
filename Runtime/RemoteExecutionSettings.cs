using UnityEngine;

namespace HybridCLR.RemoteExecution
{
    [CreateAssetMenu(menuName = "HybridCLR/Remote Execution Settings", fileName = "RemoteExecutionSettings")]
    public sealed class RemoteExecutionSettings : ScriptableObject
    {
        [Header("Development only")]
        [SerializeField] private bool m_Enabled = true;
        [SerializeField] private string m_EditorHost = "127.0.0.1";
        [SerializeField] private int m_EditorPort = 38421;
        [SerializeField] private string m_AuthenticationToken = "";
        [SerializeField] private string m_ClientId = "";
        [SerializeField] private int m_MaxBundleBytes = RemoteExecutionProtocol.DefaultMaxBundleBytes;
        [SerializeField] private bool m_LoadAotMetadata = true;
        [SerializeField] private TextAsset[] m_AotMetadataAssemblies = new TextAsset[0];

        public bool Enabled => m_Enabled;
        public string EditorHost => m_EditorHost;
        public int EditorPort => m_EditorPort;
        public string AuthenticationToken => m_AuthenticationToken;
        public string ClientId => m_ClientId;
        public int MaxBundleBytes => Mathf.Clamp(m_MaxBundleBytes, 1, RemoteExecutionProtocol.DefaultMaxBundleBytes);
        public bool LoadAotMetadata => m_LoadAotMetadata;
        public TextAsset[] AotMetadataAssemblies => m_AotMetadataAssemblies;
    }
}
