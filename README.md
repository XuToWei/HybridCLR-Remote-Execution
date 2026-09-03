# Unity Remote Execution

Unity Remote Execution is a TCP bridge between the Unity Editor and a Player, intended for development workflows. Business code registers named commands, the Editor displays each Player's command catalog, and Editor tools can send bounded binary requests and receive bounded binary results.

The core package has no HybridCLR dependency. HybridCLR compilation and runtime assembly loading are a conditional adapter implemented with the same generic command API.

## Setup

1. Add this package to a Unity 2021.3 or newer project.
2. Create **Create > Unity > Remote Execution Settings**.
3. Add `RemoteExecutionComponent` to a boot scene and assign the settings asset.
4. Configure the Editor host and port in the asset, and the matching bind address and port in **Window > Remote Execution**.
5. Start the server, then launch the Player.

The component disables itself in the Editor, but the package does not restrict which Player build types may connect. Both the Editor bind address and Player host default to `127.0.0.1`. For LAN use, bind the Editor to a reachable local interface (or `0.0.0.0`), configure the Player with the Editor machine's actual LAN address, and allow the port through the firewall. `0.0.0.0` is a bind address, not a valid Player destination.

There is no authentication or encryption. Any host that can reach the listener can identify itself arbitrarily and execute exposed commands. Use this bridge only on trusted development networks. The consuming project is responsible for excluding or disabling it in production builds when required.

## Runtime extension API

Implement `IRemoteCommandProvider` in an ordinary C# class with a public parameterless constructor:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;
using UnityEngine.Scripting;

[Preserve]
public sealed class TableRemoteCommands : IRemoteCommandProvider
{
    public TableRemoteCommands()
    {
    }

    public void RegisterCommands(IRemoteCommandRegistry registry)
    {
        registry.Register(
            new RemoteCommandDefinition(
                "table.reload",
                "Reload tables",
                "Replace the runtime table data and reload it.",
                "Tables",
                timeoutSeconds: 60,
                maxRequestBytes: 64 * 1024 * 1024,
                maxResponseBytes: 1024,
                requestContentType: "application/octet-stream",
                responseContentType: "text/plain"),
            async (context, cancellationToken) =>
            {
                await ReloadTablesAsync(context.Payload, cancellationToken);
                return RemoteCommandResult.Success("Tables reloaded.");
            });
    }

    private Task ReloadTablesAsync(byte[] data, CancellationToken cancellationToken)
    {
        // Validate the bytes and atomically replace the runtime table data.
        return Task.CompletedTask;
    }
}
```

Before connecting, the Player scans the types in all currently loaded assemblies. It constructs every concrete, closed, non-`UnityEngine.Object` implementation once, in deterministic assembly/type-name order. Constructors should be cheap and must not depend on scene-object injection.

Because discovery and construction use reflection, IL2CPP projects should apply `[Preserve]` or preserve each provider and its public parameterless constructor in the consuming project's `Assets/link.xml`.

For simple zero-payload commands, use `[RemoteCommand]` on a static parameterless method returning `void`, `Task`, or `UniTask`:

```csharp
using RemoteExecution;

public static class DevelopmentCommands
{
    [RemoteCommand("Run a development check", timeoutSeconds: 30)]
    public static void RunCheck()
    {
        UnityEngine.Debug.Log("Remote check completed.");
    }
}
```

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

The Remote Execution window is the only Editor entry point. Its **Basic** tab owns connection settings and the connected-Player overview. Its **Commands** tab owns Player/tool selection, per-Player operation state, status, and cancellation; the secondary tool switcher contains HybridCLR and other contributed panels rather than separate windows. Every user-facing remote action, including a simple zero-payload command, supplies its own `IRemoteExecutionEditorPanel`.

The HybridCLR panel provides:

- dynamic `[RemoteCommand]` source compilation into `RemoteExecution.Dynamic`;
- validated DLL/PDB loading followed by command-catalog refresh and entry execution.

The panel does not compile or send project hot-update assemblies. Any assemblies referenced by the dynamic source must already be loaded in the Player.

HybridCLR loading is not a core protocol feature. The Editor serializes a versioned HybridCLR-owned envelope and sends it through ordinary generic command input frames. The Player adapter validates the entire envelope and its per-artifact hashes before loading. After all loads succeed, it atomically publishes `[RemoteCommand]` methods from those assemblies. Projects remain responsible for their normal HybridCLR AOT metadata configuration.

The complete envelope, including metadata, is limited to 128 MiB and is buffered in memory. Assemblies cannot be unloaded from the Player AppDomain. Reapplying the same name with a different hash, or a failure after loading begins, requires restarting the Player. A partial load is contained by withholding command registration and rejecting further apply attempts, but it cannot be rolled back.

Without HybridCLR, the adapter command and HybridCLR panel are absent. The Commands tab then shows panels contributed by other modules, or an empty state when none are installed.

## Limits and behavior

- Unity 2021.3 or newer.
- The package does not enforce a Development/Debug Player requirement; consuming projects control build inclusion and production enablement.
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

Version 3 contains command-catalog discovery, generic command request/result transfer, cancellation, errors, and ping/pong. It has no v2 authentication fallback; v2 and v3 Editor/Player builds are not wire-compatible. The HybridCLR `HCB1` envelope version is independent and unchanged.

## Migration

This release intentionally uses the `RemoteExecution` namespace and package identity without compatibility shims:

| Former API | Current API |
| --- | --- |
| `HybridCLR.RemoteExecution` | `RemoteExecution` |
| `RemoteCallableAttribute` | `RemoteCommandAttribute` |
| `RemoteCallableRegistry` | `RemoteCommandRegistry` |
| **Window > HybridCLR > Remote Execution** | **Window > Remote Execution** |
| `com.xw.hybridclr.remote-execution` | `com.xw.remote-execution` |

`IRemoteAssemblyBundleLoader`, `RemoteExecutionRuntime`, the generic Editor build-provider API, and assembly-specific protocol frames have been removed. Update namespaces, asmdef references, scene components, settings assets, and custom Editor integrations directly.
