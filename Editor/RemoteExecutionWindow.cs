using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.RemoteExecution
{
    internal sealed class RemoteExecutionWindow : EditorWindow
    {
        private string m_Address = "127.0.0.1";
        private int m_Port = 38421;
        private string m_Token;
        private string m_Status;
        private Vector2 m_Scroll;

        [MenuItem("Game/HybridCLR/Remote Execution")]
        private static void Open()
        {
            GetWindow<RemoteExecutionWindow>("Remote Execution");
        }

        private void OnEnable()
        {
            m_Token = Guid.NewGuid().ToString("N");
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            bool hybridClrEnabled = HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable;
            EditorGUILayout.LabelField("HybridCLR", hybridClrEnabled ? "Enabled" : "Disabled");
            EditorGUILayout.LabelField("Active Target", EditorUserBuildSettings.activeBuildTarget.ToString());
            EditorGUILayout.LabelField("Status", RemoteExecutionServer.IsRunning ? $"Listening on {RemoteExecutionServer.Port}" : "Stopped");
            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(RemoteExecutionServer.IsRunning))
            {
                m_Address = EditorGUILayout.TextField("Bind Address", m_Address);
                m_Port = EditorGUILayout.IntField("Port (0 = random)", m_Port);
                m_Token = EditorGUILayout.TextField("Session Token", m_Token);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(RemoteExecutionServer.IsRunning || !hybridClrEnabled))
                {
                    if (GUILayout.Button("Start"))
                    {
                        try
                        {
                            RemoteExecutionServer.Start(m_Address, m_Port, m_Token);
                            m_Status = "Server started.";
                        }
                        catch (Exception exception) { m_Status = exception.Message; }
                    }
                }
                using (new EditorGUI.DisabledScope(!RemoteExecutionServer.IsRunning))
                {
                    if (GUILayout.Button("Stop"))
                    {
                        RemoteExecutionServer.Stop();
                        m_Status = "Server stopped.";
                    }
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Connected Players", EditorStyles.boldLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (RemoteExecutionServer.ClientInfo client in RemoteExecutionServer.GetClients())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(client.Description, client.Status);
                    using (new EditorGUI.DisabledScope(!RemoteExecutionServer.IsRunning || client.Status != "Authenticated"))
                    {
                        if (GUILayout.Button("Compile & Send", GUILayout.Width(110)))
                        {
                            try
                            {
                                RemoteExecutionServer.CompileAndSend(client.Id);
                                m_Status = $"Compiled and sent to {client.Description}.";
                            }
                            catch (Exception exception) { m_Status = exception.Message; }
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.Info);
            EditorGUILayout.HelpBox("Only Development Players with a matching token can connect. Loaded assembly identities are not replaced in-process; restart the Player before sending a changed DLL.", MessageType.Warning);
        }
    }
}
