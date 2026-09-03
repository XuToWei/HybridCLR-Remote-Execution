using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution.HybridCLR
{
    internal sealed class HybridCLRRemoteExecutionPanel : IRemoteExecutionEditorPanel
    {
        private static readonly SemaphoreSlim s_BuildLock = new SemaphoreSlim(1, 1);
        private const string DefaultSource = @"using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;
using UnityEngine;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Debug.Log(""Remote execution entry completed."");
        return Task.CompletedTask;
    }
}";
        private Vector2 m_SourceScroll;
        private string m_Source = DefaultSource;
        public string Id => "remote-execution.hybridclr";
        public string DisplayName => "HybridCLR";
        public int Order => 100;

        public bool IsAvailable(RemoteExecutionEditorContext context,
            out string unavailableReason)
        {
            bool enabled = global::HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable;
            unavailableReason = enabled ? string.Empty : "HybridCLR is disabled in project settings.";
            return enabled;
        }

        public void DrawGUI(RemoteExecutionEditorContext context)
        {
            DrawSource();
            RemoteExecutionClientInfo player = context.SelectedPlayer;
            string capabilityProblem = GetCapabilityProblem(player);
            using (new EditorGUI.DisabledScope(context.IsOperationRunning ||
                capabilityProblem != null))
            {
                if (GUILayout.Button("Compile, Apply & Execute"))
                    StartOperation(context, player);
            }
            if (capabilityProblem != null)
                EditorGUILayout.HelpBox(capabilityProblem, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Source must contain exactly one public IHybridCLRRemoteExecutionEntry implementation with a public parameterless constructor. Loaded assemblies cannot be unloaded; a different build of the same assembly or a partial load requires restarting the Player.",
                MessageType.Warning);
        }

        private void DrawSource()
        {
            EditorGUILayout.LabelField("Source Code", EditorStyles.boldLabel);
            m_SourceScroll = EditorGUILayout.BeginScrollView(m_SourceScroll,
                GUILayout.MinHeight(180), GUILayout.MaxHeight(360));
            m_Source = EditorGUILayout.TextArea(m_Source, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private static string GetCapabilityProblem(RemoteExecutionClientInfo player)
        {
            if (player == null) return "Select a connected Player.";
            if (!player.IsReady) return "The selected Player is not ready.";
            RemoteCommandSnapshot applyCommand = player.Commands.FirstOrDefault(command =>
                string.Equals(command.Id, HybridCLRBundleCodec.ApplyCommandId,
                    StringComparison.Ordinal));
            if (applyCommand == null)
                return "Refresh the command catalog or use a Player containing the HybridCLR adapter.";
            if (!applyCommand.Executable) return "The Player HybridCLR adapter is unavailable.";
            if (!string.Equals(applyCommand.RequestContentType,
                HybridCLRBundleCodec.ContentType, StringComparison.OrdinalIgnoreCase))
                return "The Player HybridCLR adapter uses an incompatible content type.";
            if (applyCommand.MaxRequestBytes <= 0)
                return "The Player does not allow HybridCLR bundle input.";
            return null;
        }

        private void StartOperation(RemoteExecutionEditorContext context,
            RemoteExecutionClientInfo player)
        {
            int sessionId = player.Id;
            var request = new HybridCLRRemoteBuildRequest(player.Target, m_Source);
            context.TryStartOperation("Compiling, applying and executing...",
                token => RunAsync(sessionId, request, token));
        }

        private static async Task<string> RunAsync(int sessionId,
            HybridCLRRemoteBuildRequest request, CancellationToken cancellationToken)
        {
            await RemoteExecutionEditorApi.RefreshCommandsAsync(sessionId, cancellationToken);
            RemoteExecutionClientInfo client = FindReadyClient(sessionId);
            RemoteCommandSnapshot applyCommand = client.Commands.FirstOrDefault(command =>
                string.Equals(command.Id, HybridCLRBundleCodec.ApplyCommandId,
                    StringComparison.Ordinal));
            ValidateApplyCommand(applyCommand);

            await s_BuildLock.WaitAsync(cancellationToken);
            HybridCLRRemoteBuildOutput output;
            try
            {
                output = await HybridCLRRemoteExecutionCompiler.BuildAsync(request,
                    cancellationToken);
            }
            finally { s_BuildLock.Release(); }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] envelope = HybridCLRBundleCodec.Encode(new HybridCLRBundle(
                Guid.NewGuid(), output.Target, output.Artifacts));
            if (envelope.Length > applyCommand.MaxRequestBytes)
                throw new InvalidOperationException(
                    $"HybridCLR bundle exceeds the Player limit of {applyCommand.MaxRequestBytes} bytes.");
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
                sessionId, HybridCLRBundleCodec.ApplyCommandId, envelope,
                HybridCLRBundleCodec.ContentType, cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    $"Player apply or execution failed [{result.Code}]: {result.Message}");
            return string.IsNullOrWhiteSpace(result.Message)
                ? "Compile, apply and execution completed." : result.Message;
        }

        private static RemoteExecutionClientInfo FindReadyClient(int sessionId)
        {
            return RemoteExecutionEditorApi.GetClients().FirstOrDefault(client =>
                       client.Id == sessionId && client.IsReady)
                   ?? throw new InvalidOperationException("Player is no longer connected.");
        }

        private static void ValidateApplyCommand(RemoteCommandSnapshot command)
        {
            if (command == null)
                throw new InvalidOperationException(
                    "The selected Player does not expose the HybridCLR adapter command.");
            if (!command.Executable)
                throw new InvalidOperationException("The HybridCLR adapter command is unavailable.");
            if (!string.Equals(command.RequestContentType, HybridCLRBundleCodec.ContentType,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The Player HybridCLR adapter uses an incompatible content type.");
            if (command.MaxRequestBytes <= 0)
                throw new InvalidOperationException(
                    "The Player does not allow HybridCLR bundle input.");
        }
    }
}
