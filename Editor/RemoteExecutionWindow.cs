using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionWindow : EditorWindow
    {
        private static readonly string[] s_SectionLabels = { "基础", "命令" };
        private string m_Address = "127.0.0.1";
        private int m_Port = 38421;
        [SerializeField] private string m_SelectedPanelId;
        [SerializeField] private WindowSection m_SelectedSection = WindowSection.Basic;
        private int m_SelectedSessionId;
        private Vector2 m_BasicScroll;
        private Vector2 m_CommandsScroll;
        private readonly List<PanelEntry> m_Panels = new List<PanelEntry>();
        private readonly Dictionary<int, OperationState> m_Operations =
            new Dictionary<int, OperationState>();
        private bool m_Enabled;
        private int m_ContextGeneration;
        private string m_WindowStatus;

        [MenuItem("Window/Remote Execution")]
        private static void Open()
        {
            GetWindow<RemoteExecutionWindow>("Remote Execution");
        }

        private void OnEnable()
        {
            m_Enabled = true;
            m_ContextGeneration++;
            DiscoverPanels();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            m_Enabled = false;
            m_ContextGeneration++;
            EditorApplication.update -= OnEditorUpdate;
            CancelAndDetachOperations();
            DisposePanels();
        }

        private void OnEditorUpdate()
        {
            IReadOnlyList<RemoteExecutionClientInfo> clients = RemoteExecutionEditorApi.GetClients();
            var liveSessions = new HashSet<int>(clients.Select(client => client.Id));
            foreach (KeyValuePair<int, OperationState> pair in m_Operations.ToArray())
            {
                OperationState state = pair.Value;
                if (!liveSessions.Contains(pair.Key) && state.IsRunning)
                    state.Cancel();
                state.ObserveCompletion();
                if (!liveSessions.Contains(pair.Key) && !state.IsRunning)
                {
                    state.Dispose();
                    m_Operations.Remove(pair.Key);
                }
            }
            Repaint();
        }

        private void OnGUI()
        {
            IReadOnlyList<RemoteExecutionClientInfo> clients = RemoteExecutionEditorApi.GetClients();
            DrawHeader();
            DrawSectionToolbar();
            EditorGUILayout.Space(6);
            switch (m_SelectedSection)
            {
                case WindowSection.Basic:
                    DrawBasicSection(clients);
                    break;
                case WindowSection.Commands:
                    DrawCommandsSection(clients);
                    break;
                default:
                    m_SelectedSection = WindowSection.Basic;
                    DrawBasicSection(clients);
                    break;
            }
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Unity Remote Execution", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                bool isRunning = RemoteExecutionServer.IsRunning;
                var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontStyle = FontStyle.Bold
                };
                statusStyle.normal.textColor = isRunning
                    ? (EditorGUIUtility.isProSkin
                        ? new Color(0.45f, 0.85f, 0.52f)
                        : new Color(0.08f, 0.50f, 0.16f))
                    : (EditorGUIUtility.isProSkin
                        ? new Color(1f, 0.48f, 0.43f)
                        : new Color(0.72f, 0.10f, 0.08f));
                GUILayout.Label(isRunning
                    ? $"Listening · {RemoteExecutionServer.Port}"
                    : "Stopped", statusStyle);
            }
        }

        private void DrawSectionToolbar()
        {
            int selected = GUILayout.Toolbar((int)m_SelectedSection,
                s_SectionLabels, GUILayout.Height(24));
            m_SelectedSection = Enum.IsDefined(typeof(WindowSection), selected)
                ? (WindowSection)selected : WindowSection.Basic;
        }

        private void DrawBasicSection(IReadOnlyList<RemoteExecutionClientInfo> clients)
        {
            m_BasicScroll = EditorGUILayout.BeginScrollView(m_BasicScroll);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            DrawServerControls();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(6);
            DrawConnectedPlayersOverview(clients);
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(m_WindowStatus))
                EditorGUILayout.HelpBox(m_WindowStatus, MessageType.Error);
            EditorGUILayout.HelpBox(
                "No authentication or encryption. Any host that can reach this listener can execute exposed commands. Use only on trusted networks, and exclude or disable this feature in production builds as needed.",
                MessageType.Warning);
        }

        private void DrawConnectedPlayersOverview(
            IReadOnlyList<RemoteExecutionClientInfo> clients)
        {
            EditorGUILayout.LabelField("Connected Players", EditorStyles.boldLabel);
            if (clients.Count == 0)
            {
                EditorGUILayout.HelpBox("No Players are connected.", MessageType.None);
                return;
            }
            foreach (RemoteExecutionClientInfo client in clients)
            {
                OperationState operation = GetOperation(client.Id);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(client.Description, EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField(client.Status, GUILayout.Width(210));
                    }
                    EditorGUILayout.LabelField("Target", client.Target);
                    EditorGUILayout.LabelField("Command catalog",
                        client.CommandsUpdatedAt == default(DateTime)
                            ? "Not received"
                            : client.CommandsUpdatedAt.ToLocalTime().ToString("G"));
                    if (operation != null && operation.IsActive)
                        EditorGUILayout.LabelField("Operation", operation.Status);
                }
            }
        }

        private void DrawCommandsSection(IReadOnlyList<RemoteExecutionClientInfo> clients)
        {
            RemoteExecutionClientInfo selectedPlayer = DrawPlayerSelector(clients);
            PanelEntry selectedPanel = DrawPanelSelector();
            EditorGUILayout.Space(6);

            OperationState operation = selectedPlayer == null
                ? null : GetOperation(selectedPlayer.Id);
            int contextGeneration = m_ContextGeneration;
            var context = new RemoteExecutionEditorContext(
                selectedPlayer,
                operation != null && operation.IsActive,
                operation?.Status ?? string.Empty,
                (status, callback) => TryStartOperation(selectedPlayer,
                    status, callback, contextGeneration));
            try
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                m_CommandsScroll = EditorGUILayout.BeginScrollView(m_CommandsScroll);
                DrawSelectedPanel(selectedPanel, context);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            catch (ExitGUIException) { throw; }
            finally { context.Invalidate(); }

            DrawOperationFooter(selectedPlayer, operation);
            if (!string.IsNullOrEmpty(m_WindowStatus))
                EditorGUILayout.HelpBox(m_WindowStatus, MessageType.Error);
        }

        private void DrawServerControls()
        {
            using (new EditorGUI.DisabledScope(RemoteExecutionServer.IsRunning))
            {
                m_Address = EditorGUILayout.TextField("Bind Address", m_Address);
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_Port = EditorGUILayout.IntField("Port", m_Port);
                    if (GUILayout.Button("Random", GUILayout.Width(72)))
                    {
                        try
                        {
                            m_Port = FindRandomAvailablePort(m_Address);
                            m_WindowStatus = null;
                        }
                        catch (Exception exception)
                        {
                            SetWindowStatus("Random port selection failed: " + exception.Message);
                        }
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(RemoteExecutionServer.IsRunning))
                {
                    if (GUILayout.Button("Start"))
                    {
                        try
                        {
                            RemoteExecutionServer.Start(m_Address, m_Port);
                            m_WindowStatus = null;
                        }
                        catch (Exception exception)
                        {
                            SetWindowStatus("Server start failed: " + exception.Message);
                        }
                    }
                }
                using (new EditorGUI.DisabledScope(!RemoteExecutionServer.IsRunning))
                {
                    if (GUILayout.Button("Stop"))
                    {
                        CancelAllOperations();
                        RemoteExecutionServer.Stop();
                    }
                }
            }
        }

        private static int FindRandomAvailablePort(string address)
        {
            if (!IPAddress.TryParse(address, out IPAddress ip))
                throw new InvalidOperationException("Invalid bind address.");
            byte[] randomBytes = new byte[2];
            using (var random = RandomNumberGenerator.Create())
            {
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    random.GetBytes(randomBytes);
                    int port = 49152 +
                        ((randomBytes[0] | randomBytes[1] << 8) & 0x3FFF);
                    var listener = new TcpListener(ip, port);
                    try
                    {
                        listener.Start();
                        return port;
                    }
                    catch (SocketException exception) when (
                        exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                    }
                    finally { listener.Stop(); }
                }
            }
            throw new InvalidOperationException(
                "Could not find an available random port.");
        }

        private RemoteExecutionClientInfo DrawPlayerSelector(
            IReadOnlyList<RemoteExecutionClientInfo> clients)
        {
            EditorGUILayout.LabelField("Player", EditorStyles.boldLabel);
            if (clients.Count == 0)
            {
                m_SelectedSessionId = 0;
                EditorGUILayout.HelpBox("No Players are connected.", MessageType.None);
                return null;
            }
            int current = -1;
            for (int i = 0; i < clients.Count; i++)
                if (clients[i].Id == m_SelectedSessionId) { current = i; break; }
            if (current < 0)
            {
                current = 0;
                for (int i = 0; i < clients.Count; i++)
                    if (clients[i].IsReady) { current = i; break; }
            }
            string[] labels = clients.Select(client =>
            {
                OperationState state = GetOperation(client.Id);
                string operation = state != null && state.IsActive ? " — Running" : string.Empty;
                return $"{client.Description} — {client.Status}{operation}";
            }).ToArray();
            current = EditorGUILayout.Popup(current, labels);
            RemoteExecutionClientInfo selected = clients[Math.Max(0, current)];
            m_SelectedSessionId = selected.Id;
            return selected;
        }

        private PanelEntry DrawPanelSelector()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("工具", EditorStyles.boldLabel);
            if (m_Panels.Count == 0) return null;
            int selected = m_Panels.FindIndex(panel =>
                string.Equals(panel.Id, m_SelectedPanelId, StringComparison.Ordinal));
            if (selected < 0) selected = 0;
            string[] labels = CreatePanelLabels();
            float estimatedWidth = labels.Sum(label =>
                EditorStyles.miniButton.CalcSize(new GUIContent(label)).x + 8f);
            float availableWidth = Math.Max(0f, position.width - 24f);
            if (m_Panels.Count <= 6 && estimatedWidth <= availableWidth)
                selected = GUILayout.Toolbar(selected, labels, GUILayout.Height(23));
            else
                selected = EditorGUILayout.Popup(selected, labels);
            PanelEntry panel = m_Panels[selected];
            m_SelectedPanelId = panel.Id;
            return panel;
        }

        private string[] CreatePanelLabels()
        {
            var duplicateNames = new HashSet<string>(m_Panels
                .GroupBy(panel => panel.DisplayName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key), StringComparer.Ordinal);
            return m_Panels.Select(panel => duplicateNames.Contains(panel.DisplayName)
                ? $"{panel.DisplayName} ({panel.Id})" : panel.DisplayName).ToArray();
        }

        private void DrawSelectedPanel(PanelEntry panel, RemoteExecutionEditorContext context)
        {
            if (panel == null)
            {
                EditorGUILayout.HelpBox("No editor tools are available.", MessageType.Warning);
                return;
            }
            if (panel.Error != null)
            {
                EditorGUILayout.HelpBox(panel.Error, MessageType.Error);
                return;
            }
            try
            {
                if (!panel.Instance.IsAvailable(context, out string reason))
                {
                    EditorGUILayout.HelpBox(string.IsNullOrWhiteSpace(reason)
                        ? "This tool is unavailable." : reason, MessageType.Info);
                    return;
                }
                panel.Instance.DrawGUI(context);
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                panel.Quarantine(exception);
                EditorGUILayout.HelpBox(panel.Error, MessageType.Error);
            }
        }

        private void DrawOperationFooter(RemoteExecutionClientInfo player, OperationState operation)
        {
            if (player == null || operation == null || string.IsNullOrEmpty(operation.Status)) return;
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(operation.Status);
                if (operation.IsRunning)
                {
                    using (new EditorGUI.DisabledScope(operation.IsCancelling))
                    {
                        if (GUILayout.Button(operation.IsCancelling ? "Cancelling..." : "Cancel",
                            GUILayout.Width(100)))
                            operation.Cancel();
                    }
                }
            }
        }

        private bool TryStartOperation(RemoteExecutionClientInfo player,
            string status, Func<CancellationToken, Task<string>> callback, int generation)
        {
            if (!m_Enabled || generation != m_ContextGeneration || player == null ||
                !player.IsReady || callback == null)
                return false;
            OperationState existing = GetOperation(player.Id);
            if (existing != null && existing.IsActive) return false;
            existing?.Dispose();
            var state = new OperationState(status);
            m_Operations[player.Id] = state;
            try
            {
                state.Start(callback(state.Token));
            }
            catch (Exception exception)
            {
                state.Start(Task.FromException<string>(exception));
            }
            return true;
        }

        private OperationState GetOperation(int sessionId)
        {
            return m_Operations.TryGetValue(sessionId, out OperationState state) ? state : null;
        }

        private void SetWindowStatus(string status)
        {
            m_WindowStatus = status;
        }

        private void CancelAllOperations()
        {
            foreach (OperationState state in m_Operations.Values)
                if (state.IsRunning) state.Cancel();
        }

        private void CancelAndDetachOperations()
        {
            foreach (OperationState state in m_Operations.Values)
            {
                if (state.IsRunning) state.Cancel();
                state.DisposeWhenCompleted();
            }
            m_Operations.Clear();
        }

        private void DiscoverPanels()
        {
            DisposePanels();
            var candidates = new List<PanelEntry>();
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IRemoteExecutionEditorPanel>()
                .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal))
            {
                if (!IsPanelType(type)) continue;
                try
                {
                    var panel = (IRemoteExecutionEditorPanel)Activator.CreateInstance(type);
                    candidates.Add(new PanelEntry(panel, type));
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[Unity.RemoteExecution] editor panel '{type.AssemblyQualifiedName}' " +
                        $"could not be created: {exception.GetBaseException().Message}");
                }
            }

            foreach (IGrouping<string, PanelEntry> group in candidates
                .Where(entry => entry.IsValid)
                .GroupBy(entry => entry.Id, StringComparer.Ordinal))
            {
                PanelEntry[] entries = group.ToArray();
                if (entries.Length > 1)
                {
                    string types = string.Join(", ", entries.Select(entry => entry.TypeIdentity));
                    Debug.LogError($"[Unity.RemoteExecution] editor panel ID '{group.Key}' " +
                        $"is duplicated: {types}");
                    foreach (PanelEntry entry in entries) entry.Dispose();
                    continue;
                }
                m_Panels.Add(entries[0]);
            }
            foreach (PanelEntry invalid in candidates.Where(entry => !entry.IsValid))
            {
                Debug.LogError($"[Unity.RemoteExecution] editor panel '{invalid.TypeIdentity}' " +
                    $"has invalid metadata: {invalid.Error}");
                invalid.Dispose();
            }
            m_Panels.Sort(PanelEntry.Compare);
            if (!m_Panels.Any(panel => string.Equals(panel.Id, m_SelectedPanelId,
                StringComparison.Ordinal)))
                m_SelectedPanelId = m_Panels.Count == 0 ? string.Empty : m_Panels[0].Id;
        }

        private static bool IsPanelType(Type type)
        {
            return type != null && type.IsClass && !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                type.GetConstructor(BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null) != null;
        }

        private void DisposePanels()
        {
            for (int i = m_Panels.Count - 1; i >= 0; i--) m_Panels[i].Dispose();
            m_Panels.Clear();
        }

        private enum WindowSection
        {
            Basic,
            Commands
        }

        private sealed class PanelEntry : IDisposable
        {
            private bool m_Disposed;

            internal PanelEntry(IRemoteExecutionEditorPanel instance, Type type)
            {
                Instance = instance;
                TypeIdentity = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
                try
                {
                    Id = instance.Id;
                    DisplayName = instance.DisplayName;
                    Order = instance.Order;
                    if (string.IsNullOrWhiteSpace(Id) || Id.Length > 256 ||
                        Id.Any(char.IsControl))
                        Error = "ID must contain 1..256 printable characters.";
                    else if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 256)
                        Error = "Display name must contain 1..256 characters.";
                }
                catch (Exception exception) { Error = exception.GetBaseException().Message; }
            }

            internal IRemoteExecutionEditorPanel Instance { get; }
            internal string TypeIdentity { get; }
            internal string Id { get; }
            internal string DisplayName { get; }
            internal int Order { get; }
            internal string Error { get; private set; }
            internal bool IsValid => Error == null;

            internal void Quarantine(Exception exception)
            {
                if (Error != null) return;
                Error = $"Panel '{DisplayName}' failed: {exception.GetBaseException().Message}";
                Debug.LogError($"[Unity.RemoteExecution] {Error}\n{exception}");
            }

            internal static int Compare(PanelEntry left, PanelEntry right)
            {
                int result = left.Order.CompareTo(right.Order);
                if (result == 0) result = StringComparer.Ordinal.Compare(
                    left.DisplayName, right.DisplayName);
                if (result == 0) result = StringComparer.Ordinal.Compare(left.Id, right.Id);
                return result != 0 ? result : StringComparer.Ordinal.Compare(
                    left.TypeIdentity, right.TypeIdentity);
            }

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                if (Instance is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception exception)
                    {
                        Debug.LogError($"[Unity.RemoteExecution] editor panel '{TypeIdentity}' " +
                            $"dispose failed: {exception.Message}");
                    }
                }
            }
        }

        private sealed class OperationState : IDisposable
        {
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
            private bool m_Observed;
            private bool m_Disposed;

            internal OperationState(string status)
            {
                Status = status;
            }

            internal string Status { get; private set; }
            internal Task<string> OperationTask { get; private set; }
            internal CancellationToken Token => m_Cancellation.Token;
            internal bool IsRunning => OperationTask != null && !OperationTask.IsCompleted;
            internal bool IsActive => OperationTask != null && !m_Observed;
            internal bool IsCancelling { get; private set; }

            internal void Start(Task<string> task)
            {
                OperationTask = task ?? System.Threading.Tasks.Task.FromException<string>(
                    new InvalidOperationException("Editor operation returned no task."));
            }

            internal void Cancel()
            {
                if (!IsRunning || IsCancelling) return;
                IsCancelling = true;
                Status = "Cancelling...";
                m_Cancellation.Cancel();
            }

            internal void ObserveCompletion()
            {
                if (m_Observed || OperationTask == null || !OperationTask.IsCompleted) return;
                m_Observed = true;
                if (OperationTask.IsCanceled)
                    Status = "Operation cancelled.";
                else if (OperationTask.IsFaulted)
                    Status = OperationTask.Exception?.GetBaseException().Message ?? "Operation failed.";
                else
                    Status = string.IsNullOrWhiteSpace(OperationTask.Result)
                        ? "Operation completed." : OperationTask.Result;
            }

            internal void DisposeWhenCompleted()
            {
                if (OperationTask == null || OperationTask.IsCompleted)
                {
                    ObserveCompletion();
                    Dispose();
                    return;
                }
                OperationTask.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                if (!m_Cancellation.IsCancellationRequested && IsRunning) m_Cancellation.Cancel();
                m_Cancellation.Dispose();
            }
        }
    }

}
