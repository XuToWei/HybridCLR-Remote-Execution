using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using HybridCLR.Editor;

namespace HybridCLR.RemoteExecution
{
    internal sealed class RemoteExecutionWindow : EditorWindow
    {
        private string m_Address = "127.0.0.1";
        private int m_Port = 38421;
        private string m_Token;
        private string m_Status;
        private Vector2 m_Scroll;
        private Vector2 m_SourceScroll;
        private string m_Source = @"using HybridCLR.RemoteExecution;

public static class RemoteCommand
{
    [RemoteCallable(""Run custom command"")]
    public static void Execute()
    {
        UnityEngine.Debug.Log(""Remote command executed."");
    }
}";
        private string m_EntryTypeName = "RemoteCommand";
        private string m_EntryMethodName = "Execute";
        private readonly HashSet<string> m_SelectedAssemblies = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_SelectedDefines = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, System.Threading.Tasks.Task> m_Operations =
            new Dictionary<int, System.Threading.Tasks.Task>();
        private readonly HashSet<int> m_BusySessions = new HashSet<int>();
        private string[] m_HotUpdateAssemblies = Array.Empty<string>();
        private string[] m_AssemblyDefines = Array.Empty<string>();

        [MenuItem("Window/HybridCLR/Remote Execution")]
        private static void Open()
        {
            GetWindow<RemoteExecutionWindow>("Remote Execution");
        }

        private void OnEnable()
        {
            m_Token = Guid.NewGuid().ToString("N");
            RefreshAssemblyList();
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void RefreshAssemblyList()
        {
            m_HotUpdateAssemblies = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved
                .Where(name => !string.Equals(name, RemoteExecutionCompiler.DynamicAssemblyName, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            m_SelectedAssemblies.RemoveWhere(name => !m_HotUpdateAssemblies.Contains(name, StringComparer.Ordinal));
            RefreshAssemblyDefines();
        }

        private string[] GetProjectDefines()
        {
            var defines = new HashSet<string>(StringComparer.Ordinal);
            var selected = new HashSet<string>(m_SelectedAssemblies, StringComparer.Ordinal);
            foreach (UnityEditor.Compilation.Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.Player))
            {
                if (!selected.Contains(assembly.name)) continue;
                foreach (string define in assembly.defines ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(define)) defines.Add(define);
            }
            return defines.OrderBy(define => define, StringComparer.Ordinal).ToArray();
        }

        private void RefreshAssemblyDefines()
        {
            m_AssemblyDefines = GetProjectDefines();
            m_SelectedDefines.RemoveWhere(name => !m_AssemblyDefines.Contains(name, StringComparer.Ordinal));
        }

        private void OnGUI()
        {
            bool hybridClrEnabled = HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable;
            EditorGUILayout.LabelField("HybridCLR", hybridClrEnabled ? "Enabled" : "Disabled");
            EditorGUILayout.LabelField("Active Target", EditorUserBuildSettings.activeBuildTarget.ToString());
            EditorGUILayout.LabelField("Status", RemoteExecutionServer.IsRunning ? $"Listening on {RemoteExecutionServer.Port}" : "Stopped");
            EditorGUILayout.Space(4);

            DrawServerControls(hybridClrEnabled);
            EditorGUILayout.Space(8);
            DrawSourceControls();
            EditorGUILayout.Space(8);
            DrawClients(hybridClrEnabled);

            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.Info);
            EditorGUILayout.HelpBox(
                "仅 Development Player 可以连接。输入的程序集会在 Player 中加载并执行，不提供沙箱；请只连接可信 Player，并且不要标记高风险操作。修改后通常需要重启 Player。",
                MessageType.Warning);
        }

        private void DrawServerControls(bool hybridClrEnabled)
        {
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
        }

        private void DrawSourceControls()
        {
            EditorGUILayout.LabelField("Custom C# Code", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "请输入完整 C# 类型。入口必须带 [RemoteCallable]，是 static、无参数，并返回 void、Task 或 UniTask。代码编译为独立临时程序集后发送到 Player。",
                MessageType.None);
            m_EntryTypeName = EditorGUILayout.TextField("Entry Type FullName", m_EntryTypeName);
            m_EntryMethodName = EditorGUILayout.TextField("Entry Method", m_EntryMethodName);
            m_SourceScroll = EditorGUILayout.BeginScrollView(m_SourceScroll, GUILayout.MinHeight(180), GUILayout.MaxHeight(360));
            m_Source = EditorGUILayout.TextArea(m_Source, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Assemblies to Compile & Send", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
            if (GUILayout.Button("Refresh")) RefreshAssemblyList();
                if (GUILayout.Button("Select All"))
                {
                    foreach (string name in m_HotUpdateAssemblies) m_SelectedAssemblies.Add(name);
                    RefreshAssemblyDefines();
                }
                if (GUILayout.Button("Clear"))
                {
                    m_SelectedAssemblies.Clear();
                    RefreshAssemblyDefines();
                }
            }
            if (m_HotUpdateAssemblies.Length == 0)
            {
                EditorGUILayout.HelpBox("HybridCLR 没有配置热更新程序集。", MessageType.Error);
                return;
            }
            foreach (string name in m_HotUpdateAssemblies)
            {
                bool selected = m_SelectedAssemblies.Contains(name);
                bool next = EditorGUILayout.ToggleLeft(name, selected);
                if (next) m_SelectedAssemblies.Add(name); else m_SelectedAssemblies.Remove(name);
                if (next != selected) RefreshAssemblyDefines();
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Assembly Defines", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("选择用于 Player 脚本和自定义代码编译的宏。", MessageType.None);
            foreach (string define in m_AssemblyDefines)
            {
                bool selected = m_SelectedDefines.Contains(define);
                bool next = EditorGUILayout.ToggleLeft(define, selected);
                if (next) m_SelectedDefines.Add(define); else m_SelectedDefines.Remove(define);
            }
        }

        private void DrawClients(bool hybridClrEnabled)
        {
            EditorGUILayout.LabelField("Connected Players", EditorStyles.boldLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            foreach (RemoteExecutionServer.ClientInfo client in RemoteExecutionServer.GetClients())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(client.Description, client.Status);
                    bool busy = m_BusySessions.Contains(client.Id);
                    using (new EditorGUI.DisabledScope(!hybridClrEnabled || busy || client.Status != "Authenticated"))
                    {
                        if (GUILayout.Button(busy ? "Running..." : "Compile, Load & Execute", GUILayout.Width(170)))
                            StartExecution(client.Id);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void StartExecution(int sessionId)
        {
            if (m_SelectedAssemblies.Count == 0)
            {
                m_Status = "请至少选择一个热更新程序集。";
                return;
            }
            if (string.IsNullOrWhiteSpace(m_Source))
            {
                m_Status = "请输入完整 C# 代码。";
                return;
            }
            if (string.IsNullOrWhiteSpace(m_EntryTypeName) || string.IsNullOrWhiteSpace(m_EntryMethodName))
            {
                m_Status = "请输入入口类型 FullName 和方法名。";
                return;
            }

            m_BusySessions.Add(sessionId);
            m_Status = "正在编译并执行……";
            string[] assemblies = m_SelectedAssemblies.ToArray();
            System.Threading.Tasks.Task operation = RemoteExecutionServer.CompileAndSend(sessionId, assemblies, m_SelectedDefines, m_Source, m_EntryTypeName, m_EntryMethodName);
            m_Operations[sessionId] = operation;
            operation.ContinueWith(task =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        m_BusySessions.Remove(sessionId);
                        m_Operations.Remove(sessionId);
                        if (task.IsFaulted) m_Status = task.Exception?.GetBaseException().Message ?? "Remote execution failed.";
                        else if (task.IsCanceled) m_Status = "Remote execution cancelled.";
                        else m_Status = "编译、加载和执行成功。";
                        Repaint();
                    };
                });
        }
    }
}
