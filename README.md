# Unity Remote Execution

English · [简体中文](README.zh-CN.md)

Unity Remote Execution is a TCP bridge between the Unity Editor and a Player, intended for development workflows. Business code registers named commands, the Editor displays each Player's command catalog, and Editor tools can send bounded binary requests and receive bounded binary results.

The core package has no HybridCLR dependency. HybridCLR compilation and runtime assembly loading are a conditional adapter implemented with the same generic command API.

## Installation

In a Unity 2021.3 or newer project, open **Window > Package Manager**, click **+**, select **Add package from git URL...**, and paste:

```text
https://github.com/XuToWei/UnityRemoteExecution.git
```

## Player startup

The package does not add anything to a scene and does not connect automatically. Start and stop the Player client from your own bootstrap or development UI:

```csharp
using RemoteExecution;
using UnityEngine;

public sealed class RemoteExecutionControls : MonoBehaviour
{
    private void OnEnable()
    {
        RemoteExecutionPlayerApi.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnDisable()
    {
        RemoteExecutionPlayerApi.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    public void Connect()
    {
        RemoteExecutionPlayerApi.Start("192.168.1.20", 38421, "Test Device");
    }

    public void Disconnect()
    {
        RemoteExecutionPlayerApi.Stop();
    }

    private static void OnConnectionStateChanged(RemoteExecutionConnectionState state)
    {
        Debug.Log($"Remote Execution: {state}");
        if (state == RemoteExecutionConnectionState.Faulted)
        {
            RemoteExecutionConnectionError error = RemoteExecutionPlayerApi.LastError;
            Debug.LogWarning($"[{error.Code}] {error.Message}");
        }
    }
}
```

`ConnectionState` reports `Disconnected`, `Connecting`, `Handshaking`, `Connected`, or `Faulted`; `IsConnected` is true only after the Editor acknowledges the protocol handshake. State-change callbacks run on Unity's main thread. `LastError` contains a stable code and message while faulted.

`Start` validates the host, port, client ID, and optional transfer limits synchronously. Calling it again with the same parameters while active does nothing; calling it after a fault retries, and calling it with different parameters replaces the current connection. There is no automatic reconnect, so the business layer controls retry timing and UI. `Stop` is safe to call repeatedly.

The Player API is unavailable in Editor Play Mode. Start the Editor listener from **Window > Remote Execution**, then call `RemoteExecutionPlayerApi.Start` from a built Player. No `RemoteExecutionComponent` or settings asset is required.

The package does not restrict which Player build types may connect. The Editor bind address defaults to `127.0.0.1`, and `Start` commonly uses the same host for local connections. For LAN use, bind the Editor to a reachable local interface (or `0.0.0.0`), pass the Editor machine's actual LAN address to the Player, and allow the port through the firewall. `0.0.0.0` is a bind address, not a valid Player destination.

There is no authentication or encryption. Any host that can reach the listener can identify itself arbitrarily and execute exposed commands. Use this bridge only on trusted development networks. The consuming project is responsible for excluding or disabling it in production builds when required.

## Runtime extension API

Register each named binary handler explicitly before starting the Player connection:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;

public static class TableRemoteCommands
{
    public static void Register()
    {
        RemoteCommandRegistry.Register(
            new RemoteCommandDefinition(
                "table.reload",
                "Reload tables",
                "Replace the runtime table data and reload it.",
                "Tables",
                timeoutSeconds: 60,
                maxRequestBytes: 64 * 1024 * 1024,
                maxResponseBytes: 1024,
                requestContentType: "application/octet-stream",
                responseContentType: "application/octet-stream"),
            async (context, cancellationToken) =>
            {
                byte[] request = context.Payload;
                byte[] response = await ReloadTablesAsync(request, cancellationToken);
                return RemoteCommandResult.Success(
                    "Tables reloaded.", response, "application/octet-stream");
            });
    }

    private static Task<byte[]> ReloadTablesAsync(
        byte[] data, CancellationToken cancellationToken)
    {
        // Validate the bytes and atomically replace the runtime table data.
        return Task.FromResult(new byte[0]);
    }
}
```

Call `TableRemoteCommands.Register()` once before `RemoteExecutionPlayerApi.Start(...)`. Command IDs must be globally unique; duplicate registration throws. Static methods are not exposed automatically, and the package has no command attribute.

Reusable modules may still implement `IRemoteCommandProvider`; the Player discovers concrete provider types when it first starts. Because provider discovery uses reflection, IL2CPP projects should preserve providers and their public parameterless constructors with `[Preserve]` or `Assets/link.xml`.

## Editor API

Business Editor tools can query connected Players and execute commands without accessing sockets:

```csharp
RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients()[0];
byte[] tableBytes = BuildTableBytes();

RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
    player.Id,
    "table.reload",
    tableBytes,
    "application/octet-stream");

if (!result.Succeeded)
    UnityEngine.Debug.LogError($"[{result.Code}] {result.Message}");
```

Call `RefreshCommandsAsync(sessionId)` to await a fresh command catalog. `GetClients()` returns read-only snapshots containing the Player target, explicit `IsReady` state, and current command metadata. `ClientId` is self-reported display metadata and is not a security identity. The command catalog remains a capability-negotiation API for Editor panels; the core window does not provide a generic command executor.

## Editor tool panels

Editor integrations implement `IRemoteExecutionEditorPanel`; the core window discovers implementations through Unity `TypeCache`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;
using UnityEditor;
using UnityEngine;

public sealed class TableRemoteExecutionPanel : IRemoteExecutionEditorPanel
{
    public string Id => "game.tables";
    public string DisplayName => "Tables";
    public int Order => 50;

    public bool IsAvailable(RemoteExecutionEditorContext context, out string reason)
    {
        reason = context.SelectedPlayer == null ? "Select a Player." : string.Empty;
        return context.SelectedPlayer != null;
    }

    public void DrawGUI(RemoteExecutionEditorContext context)
    {
        if (!GUILayout.Button("Build and reload") || context.IsOperationRunning) return;
        int sessionId = context.SelectedPlayer.Id;
        byte[] bytes = BuildTableBytes();
        context.TryStartOperation("Reloading tables...", async cancellationToken =>
        {
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
                sessionId, "table.reload", bytes, "application/octet-stream", cancellationToken);
            if (!result.Succeeded)
                throw new System.InvalidOperationException($"[{result.Code}] {result.Message}");
            return "Tables reloaded.";
        });
    }
}
```

A panel must be a concrete ordinary C# class with a parameterless constructor. Its stable `Id` must be globally unique. `IsAvailable` must have no side effects, and the context is valid only during the current `DrawGUI` call. Capture Player/input values before starting an operation. The host allows one operation per Player across all panels; different Players can run concurrently. Panels needing deterministic cleanup may implement `IDisposable`, and panels needing state across reloads should use `SessionState`, `EditorPrefs`, or `ScriptableSingleton`.

The cancellation-aware `RefreshCommandsAsync` and `ExecuteCommandAsync` overloads accept a `CancellationToken`. Cancellation remains cooperative for Unity compiler calls and Player handlers.

## Optional HybridCLR adapter

When `com.code-philosophy.hybridclr` is installed, the package's `versionDefines` activates the conditional adapter automatically. It adds a **HybridCLR** tool inside **Window > Remote Execution** and the Player command `hybridclr.apply-bundle`.

The Remote Execution window is the only Editor entry point. Its **Basic** tab owns connection settings and the connected-Player overview. Its **Commands** tab owns Player/tool selection, per-Player operation state, status, and cancellation; the secondary tool switcher contains HybridCLR and other contributed panels rather than separate windows. Every user-facing remote action supplies its own `IRemoteExecutionEditorPanel`.

The HybridCLR panel provides:

- dynamic `IHybridCLRRemoteExecutionEntry` source compilation into `RemoteExecution.Dynamic`;
- validated DLL/PDB loading followed by interface-entry execution.

Dynamic source example:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Handle application work after the assembly has loaded.
        await ReloadGameLogicAsync(cancellationToken);
    }

    private static Task ReloadGameLogicAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

The entry must be a public concrete class with a public parameterless constructor, and a bundle must contain exactly one `IHybridCLRRemoteExecutionEntry` implementation. Exceptions from `ExecuteAsync` are returned to the Editor as `ENTRY_EXECUTION_FAILED`; cancellation uses the normal remote-command cancellation or timeout result.

The panel does not compile or send project hot-update assemblies. Any assemblies referenced by the dynamic source must already be loaded in the Player.

HybridCLR loading is not a core protocol feature. The Editor serializes a versioned HybridCLR-owned envelope and sends it through ordinary generic command input frames. The Player adapter validates the entire envelope and its per-artifact hashes before loading. A bundle must contain exactly one public concrete `IHybridCLRRemoteExecutionEntry` implementation with a public parameterless constructor. After loading, the adapter invokes `Task ExecuteAsync(CancellationToken)` inside the fixed `hybridclr.apply-bundle` request. The dynamic entry is not published in the command catalog. Projects remain responsible for their normal HybridCLR AOT metadata configuration.

The complete envelope, including metadata, is limited to 128 MiB and is buffered in memory. Assemblies cannot be unloaded from the Player AppDomain. Reapplying the same name with a different hash, or a failure while loading or resolving the entry, requires restarting the Player. A partial load cannot be rolled back; the adapter rejects later apply attempts to avoid expanding an inconsistent state.

Without HybridCLR, the adapter command and HybridCLR panel are absent. The Commands tab then shows panels contributed by other modules, or an empty state when none are installed.

## Limits and behavior

- Unity 2021.3 or newer.
- The package does not enforce a Development/Debug Player requirement; consuming projects control build inclusion and production enablement.
- The Player never starts or reconnects automatically; business code owns `Start`, retry, and `Stop`.
- Maximum frame payload: 1 MiB.
- Maximum transfer chunk: 60 KiB.
- Hard command request limit: 128 MiB; default business-command request limit: 16 MiB.
- Hard/default command response limits: 64 MiB / 16 MiB.
- Global settings and per-command metadata can lower these limits; the effective minimum is advertised in the catalog.
- Binary input and output include total length and SHA-256, and chunks must be complete and ordered.
- One command executes at a time per Player.
- `requiresMainThread: true` starts the handler on Unity's main thread; code after an asynchronous continuation is the handler's responsibility.
- Timeouts and cancellation are cooperative; a handler that ignores cancellation cannot be safely interrupted.
- The transport has no authentication or encryption; any host that can reach the listener may invoke exposed commands.
- SHA-256 checks detect accidental transfer corruption, but do not authenticate the peer or protect against active tampering.
- Do not expose destructive or production-data handlers.

## Protocol

Protocol version 3 uses the `URX3` frame magic and an unauthenticated `Hello(requestId) → Ready(same requestId)` handshake. `Ready` must have an empty payload. Message numbers are explicit: `Hello=1`, `Ready=2`, `Error=3`, `Ping=4`, `Pong=5`, `ListCommands=6`, `Commands=7`, command-input begin/chunk/end `=8..10`, command-result metadata/chunk/end `=11..13`, and `CancelCommand=14`.

Version 3 contains command-catalog discovery, generic command request/result transfer, cancellation, errors, and ping/pong. It has no v2 authentication fallback; v2 and v3 Editor/Player builds are not wire-compatible. The HybridCLR `HCB1` envelope version 2 is independent from the core protocol.
