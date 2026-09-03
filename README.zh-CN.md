# Unity Remote Execution（简体中文）

[English](README.md) · 简体中文

Unity Remote Execution 是一个面向开发工作流的 Unity Editor 与 Player TCP 桥接。业务代码注册命令，Editor 显示每个 Player 的命令目录；Editor 工具可以发送有大小限制的二进制请求并接收二进制结果。

核心包不依赖 HybridCLR。HybridCLR 编译和运行时程序集加载是条件启用的可选适配器，并且同样通过通用远程命令实现。

## 安装

在 Unity 2021.3 或更高版本项目中打开 **Window > Package Manager**，点击左上角的 **+**，选择 **Add package from git URL...**，粘贴以下地址：

```text
https://github.com/XuToWei/UnityRemoteExecution.git
```

## Player 启动

包不会向场景添加组件，也不会自动连接。由业务层在自己的启动流程或开发 UI 中启动和停止 Player 客户端：

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

`ConnectionState` 会返回 `Disconnected`、`Connecting`、`Handshaking`、`Connected` 或 `Faulted`；只有 Editor 完成协议握手后，`IsConnected` 才为 `true`。状态回调在 Unity 主线程触发。发生故障时，`LastError` 提供稳定的错误码和消息。

`Start` 会同步校验主机、端口、客户端 ID 和可选传输限制。连接期间使用相同参数重复调用不会产生新连接；故障后再次调用会重试，传入不同参数则替换当前连接。包不会自动重连，重试时机和 UI 完全由业务层控制。`Stop` 可以安全地重复调用。

Player API 不支持 Editor Play Mode。先在 **Window > Remote Execution** 中启动 Editor 监听服务，再由构建后的 Player 调用 `RemoteExecutionPlayerApi.Start`。不需要添加 `RemoteExecutionComponent` 或创建配置资产。

包本身不限制可连接的 Player 构建类型。Editor 监听地址默认使用 `127.0.0.1`，本机连接时 `Start` 通常也传入该地址。局域网使用时，应让 Editor 监听可达的本机接口（也可监听 `0.0.0.0`），向 Player 传入 Editor 机器的实际局域网地址，并按需放行防火墙端口。`0.0.0.0` 只能用于监听，不能作为 Player 的目标地址。

连接没有认证或加密。任何能访问监听端口的主机都可以任意声明身份并执行已暴露的命令。只能在可信开发网络中使用；是否从生产构建中排除或禁用该功能，由接入项目自行负责。

## Runtime 扩展接口

在启动 Player 连接前，显式注册每个具名二进制 handler：

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
                "替换运行时表格数据并重载。",
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
        // 校验 bytes，并以原子方式替换运行时表格数据。
        return Task.FromResult(new byte[0]);
    }
}
```

在 `RemoteExecutionPlayerApi.Start(...)` 之前调用一次 `TableRemoteCommands.Register()`。命令 ID 必须全局唯一；重复注册会抛出异常。静态方法不会被自动暴露，包也不再提供命令特性。

可复用模块仍可实现 `IRemoteCommandProvider`；Player 首次启动时会发现具体 Provider 类型。Provider 发现使用反射，因此 IL2CPP 项目应通过 `[Preserve]` 或 `Assets/link.xml` 保留 Provider 及其 public 无参构造函数。

## Editor API

业务 Editor 工具无需访问 socket 即可查询 Player 并执行命令：

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

`RefreshCommandsAsync(sessionId)` 会等待最新目录真正写入缓存后再完成。`GetClients()` 返回包含 Player target、显式 `IsReady` 状态和命令 metadata 的只读快照。`ClientId` 是 Player 自行声明的展示信息，不是安全身份。命令目录继续作为 Editor panel 与 Player 间的能力协商 API；核心窗口不再提供通用命令执行器。

## Editor 工具 Panel

Editor 集成实现 `IRemoteExecutionEditorPanel`，核心窗口通过 Unity `TypeCache` 自动发现：

```csharp
using RemoteExecution;
using UnityEngine;

public sealed class TableRemoteExecutionPanel : IRemoteExecutionEditorPanel
{
    public string Id => "game.tables";
    public string DisplayName => "Tables";
    public int Order => 50;

    public bool IsAvailable(RemoteExecutionEditorContext context, out string reason)
    {
        reason = context.SelectedPlayer == null ? "请选择 Player。" : string.Empty;
        return context.SelectedPlayer != null;
    }

    public void DrawGUI(RemoteExecutionEditorContext context)
    {
        if (!GUILayout.Button("构建并重载") || context.IsOperationRunning) return;
        int sessionId = context.SelectedPlayer.Id;
        byte[] bytes = BuildTableBytes();
        context.TryStartOperation("正在重载表格……", async cancellationToken =>
        {
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
                sessionId, "table.reload", bytes, "application/octet-stream", cancellationToken);
            if (!result.Succeeded)
                throw new System.InvalidOperationException($"[{result.Code}] {result.Message}");
            return "表格重载完成。";
        });
    }
}
```

Panel 必须是提供无参构造的 concrete 普通 C# 类，稳定 `Id` 必须全局唯一。`IsAvailable` 不能产生副作用；context 只在当前 `DrawGUI` 调用中有效，启动任务前应捕获 Player 和所有输入值。宿主保证同一 Player 在所有 panel 间同时只运行一个任务，不同 Player 可以并发。需要确定性清理时可实现 `IDisposable`；需要跨 reload 保存状态时应自行使用 `SessionState`、`EditorPrefs` 或 `ScriptableSingleton`。

带取消参数的 `RefreshCommandsAsync`、`ExecuteCommandAsync` overload 接收 `CancellationToken`。Unity 编译器调用和 Player handler 的取消仍然是协作式的。

## 可选 HybridCLR 适配器

安装 `com.code-philosophy.hybridclr` 后，包内 `versionDefines` 会自动启用条件适配器。它会在 **Window > Remote Execution** 内增加 **HybridCLR** 工具，并在 Player 注册 `hybridclr.apply-bundle`。

Remote Execution 窗口是唯一 Editor 入口：**基础**页签管理连接配置和 Player 概览；**命令**页签统一管理 Player/工具选择、每个 Player 的任务状态、结果和取消。命令页的二级工具切换栏包含 HybridCLR 与其他业务 panel，不再创建独立窗口。每个面向用户的远程操作由自身的 `IRemoteExecutionEditorPanel` 提供界面。

HybridCLR panel 提供：

- 将动态 `IHybridCLRRemoteExecutionEntry` 源码编译到 `RemoteExecution.Dynamic`；
- DLL/PDB 校验加载和接口入口执行。

动态源码示例：

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 在这里处理程序集加载完成后的业务逻辑。
        await ReloadGameLogicAsync(cancellationToken);
    }

    private static Task ReloadGameLogicAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

入口必须是 public concrete class，提供 public 无参构造函数，并且一个 bundle 中只能存在一个 `IHybridCLRRemoteExecutionEntry` 实现。`ExecuteAsync` 的异常会作为 `ENTRY_EXECUTION_FAILED` 返回 Editor；取消会沿用远程命令的取消或超时结果。

该 panel 不会编译或发送项目中的热更新程序集。动态源码引用的程序集必须已在 Player 中加载。

HybridCLR 加载不是核心协议特性。Editor 把自有的版本化 HybridCLR envelope 通过普通通用命令输入帧发送。Player 在加载前完整校验 envelope 和每个 artifact 的 hash。Bundle 必须包含且仅包含一个 public concrete `IHybridCLRRemoteExecutionEntry` 实现，并提供 public 无参构造函数。加载后，适配器会在固定的 `hybridclr.apply-bundle` 请求内调用 `Task ExecuteAsync(CancellationToken)`；动态入口不会发布到命令目录。项目仍需自行完成正常的 HybridCLR AOT metadata 配置。

完整 envelope（含 metadata）最大 128 MiB，并会完整缓冲在内存中。程序集不能从 Player AppDomain 卸载。同名程序集 hash 变化，或加载、解析入口过程中发生失败，都必须重启 Player。部分加载无法回滚；适配器会拒绝后续 apply，避免继续扩大不一致状态。

未安装 HybridCLR 时，该命令和 HybridCLR panel 都不存在。命令页仍显示其他模块提供的 panel；若没有任何实现，则显示空状态。

## 限制和行为

- Unity 2021.3 或更高版本。
- 包本身不强制要求 Development/Debug Player；构建包含范围和生产环境启用策略由接入项目控制。
- Player 不会自动启动或重连；业务代码负责 `Start`、重试和 `Stop`。
- 单帧 payload 最大 1 MiB。
- 单分片最大 60 KiB。
- command request 硬上限 128 MiB，普通业务命令默认 16 MiB。
- command response 硬上限 64 MiB，默认 16 MiB。
- 全局配置和命令 metadata 可以降低限制；命令目录发布实际有效的最小值。
- 二进制输入和输出包含总长度与 SHA-256，分片必须完整且严格有序。
- 每个 Player 同时只执行一个命令。
- `requiresMainThread: true` 只保证 handler 从 Unity 主线程开始；异步 continuation 之后的线程由 handler 自行负责。
- 超时和取消是协作式的；忽略取消的 handler 不能被安全强行中断。
- 传输没有认证或加密；任何能访问监听端口的主机都可能调用已暴露的命令。
- SHA-256 只能检测意外的传输损坏，不能认证对端，也不能抵御主动篡改。
- 不要暴露破坏性操作或修改生产数据的 handler。

## 协议

协议版本 3 使用 `URX3` frame magic，以及无认证的 `Hello(requestId) → Ready(same requestId)` 握手；`Ready` payload 必须为空。消息编号显式固定为：`Hello=1`、`Ready=2`、`Error=3`、`Ping=4`、`Pong=5`、`ListCommands=6`、`Commands=7`、command input begin/chunk/end 为 `8..10`、command result metadata/chunk/end 为 `11..13`、`CancelCommand=14`。

版本 3 只包含命令目录、通用 command request/result、取消、错误和 ping/pong，不提供 v2 认证回退；v2 与 v3 的 Editor/Player 不兼容。HybridCLR 的 `HCB1` envelope 版本 2 与核心协议相互独立。
