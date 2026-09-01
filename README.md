# HybridCLR Remote Execution
[简体中文](README.zh-CN.md)


This package provides a development-only TCP bridge between a Unity Editor and a packaged HybridCLR Player.

## Setup

1. Enable HybridCLR and configure the assemblies to be hot-update assemblies.
2. Generate HybridCLR AOT metadata for the target platform.
3. Create a `RemoteExecutionSettings` asset and assign it to a `RemoteExecutionComponent` in the boot scene.
4. Enable the component only in a Development Player. Configure the Editor host, port, and the session token shown by the Editor window.
5. In the Editor, open **Window > HybridCLR > Remote Execution**, start the server, and connect the Player.

Assemblies are sent with a manifest, lengths, SHA-256 hashes, and bounded chunks. The Player validates the complete transfer before calling `Assembly.Load` and exposing methods marked with `RemoteCallableAttribute`.

The **Window > HybridCLR > Remote Execution** window can select configured hot-update assemblies, choose their compiler defines, enter a complete C# type, and run its explicitly marked static parameterless entry method after the bundle is loaded. The custom type is compiled into the fixed `RemoteExecution.Dynamic` assembly; configure that assembly as a HybridCLR hot-update assembly before building an IL2CPP Player.

## Callable methods

```csharp
using HybridCLR.RemoteExecution;

public static class DevelopmentCommands
{
    [RemoteCallable("Run a development check")]
    public static void RunCheck()
    {
    }
}
```

The Editor can discover methods by ID and invoke only explicitly marked, static, parameterless methods. Arbitrary type or method reflection is not exposed.

## Limitations

- The bridge is intended for local development. It is disabled unless the Player is a debug/development build, the component is enabled, and a non-empty token is configured.
- Bind to `127.0.0.1` by default. HMAC authenticates the connection but does not encrypt it; use a trusted network or tunnel for LAN development.
- The assembly must have been included in the Player's HybridCLR hot-update assembly configuration and compiled for the matching Unity/HybridCLR/BuildTarget environment.
- Loaded assemblies are not unloaded by Unity. A different version with the same assembly name is rejected; restart the Player after changing source code.
- The current package sends the configured HybridCLR hot-update assembly set. It does not modify `Assets/Res`, ET, GameHot, or project-specific loaders.
