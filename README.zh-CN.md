# Unity Remote Execution（简体中文）

Unity Remote Execution 是一个面向开发工作流的 Unity Editor 与 Player TCP 桥接。业务代码注册命令，Editor 显示每个 Player 的命令目录；Editor 工具可以发送有大小限制的二进制请求并接收二进制结果。

核心包不依赖 HybridCLR。HybridCLR 编译和运行时程序集加载是条件启用的可选适配器，并且同样通过通用远程命令实现。

## 配置

1. 将包添加到 Unity 2021.3 或更高版本项目。
2. 创建 **Create > Unity > Remote Execution Settings**。
3. 在启动场景添加 `RemoteExecutionComponent` 并赋值配置资产。
4. 在配置资产中填写 Editor 主机和端口，并在 **Window > Remote Execution** 中填写对应的监听地址和端口。
5. 启动服务器，然后运行 Player。

组件在 Editor 中自动禁用，但包本身不限制可连接的 Player 构建类型。Editor 监听地址和 Player 主机均默认使用 `127.0.0.1`。局域网使用时，应让 Editor 监听可达的本机接口（也可监听 `0.0.0.0`），让 Player 填写 Editor 机器的实际局域网地址，并按需放行防火墙端口。`0.0.0.0` 只能用于监听，不能作为 Player 的目标地址。

连接没有认证或加密。任何能访问监听端口的主机都可以任意声明身份并执行已暴露的命令。只能在可信开发网络中使用；是否从生产构建中排除或禁用该功能，由接入项目自行负责。

## Runtime 扩展接口

业务层在提供 public 无参构造函数的普通 C# 类中实现 `IRemoteCommandProvider`：

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
                "替换运行时表格数据并重载。",
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
        // 校验 bytes，并以原子方式替换运行时表格数据。
        return Task.CompletedTask;
    }
}
```

Player 建立连接前扫描当前已加载程序集中的类型。每个 Provider 必须是非抽象、非开放泛型、非 `UnityEngine.Object` 派生的普通类；Player 按程序集名和类型全名稳定排序，每种类型只创建一次。构造函数应保持轻量，不能依赖场景对象注入。

Provider 通过反射发现和构造。IL2CPP 项目应添加 `[Preserve]`，或在业务工程的 `Assets/link.xml` 中保留 Provider 和 public 无参构造函数。

简单的零 payload 命令可以使用 static `[RemoteCommand]`；方法必须无参数，返回 `void`、`Task` 或 `UniTask`：

```csharp
using RemoteExecution;

public static class DevelopmentCommands
{
    [RemoteCommand("执行开发环境检查", timeoutSeconds: 30)]
    public static void RunCheck()
    {
        UnityEngine.Debug.Log("Remote check completed.");
    }
}
```

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

Remote Execution 窗口是唯一 Editor 入口：**基础**页签管理连接配置和 Player 概览；**命令**页签统一管理 Player/工具选择、每个 Player 的任务状态、结果和取消。命令页的二级工具切换栏包含 HybridCLR 与其他业务 panel，不再创建独立窗口。每个面向用户的远程操作，包括简单的零 payload 命令，也由自身的 `IRemoteExecutionEditorPanel` 提供界面。

HybridCLR panel 提供：

- 将动态 `[RemoteCommand]` 源码编译到 `RemoteExecution.Dynamic`；
- DLL/PDB 校验加载、目录刷新和入口执行。

该 panel 不会编译或发送项目中的热更新程序集。动态源码引用的程序集必须已在 Player 中加载。

HybridCLR 加载不是核心协议特性。Editor 把自有的版本化 HybridCLR envelope 通过普通通用命令输入帧发送。Player 在加载前完整校验 envelope 和每个 artifact 的 hash；全部加载成功后，才原子发布新程序集中的 `[RemoteCommand]`。项目仍需自行完成正常的 HybridCLR AOT metadata 配置。

完整 envelope（含 metadata）最大 128 MiB，并会完整缓冲在内存中。程序集不能从 Player AppDomain 卸载。同名程序集 hash 变化，或加载开始后发生失败，都必须重启 Player。部分加载无法回滚；适配器会阻止发布本批命令并拒绝后续 apply，避免继续扩大不一致状态。

未安装 HybridCLR 时，该命令和 HybridCLR panel 都不存在。命令页仍显示其他模块提供的 panel；若没有任何实现，则显示空状态。

## 限制和行为

- Unity 2021.3 或更高版本。
- 包本身不强制要求 Development/Debug Player；构建包含范围和生产环境启用策略由接入项目控制。
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

版本 3 只包含命令目录、通用 command request/result、取消、错误和 ping/pong，不提供 v2 认证回退；v2 与 v3 的 Editor/Player 不兼容。HybridCLR 的 `HCB1` envelope 版本相互独立，本次保持不变。

## 迁移

本版本直接使用新的 `RemoteExecution` 命名空间和包名，不提供兼容 shim：

| 旧 API | 当前 API |
| --- | --- |
| `HybridCLR.RemoteExecution` | `RemoteExecution` |
| `RemoteCallableAttribute` | `RemoteCommandAttribute` |
| `RemoteCallableRegistry` | `RemoteCommandRegistry` |
| **Window > HybridCLR > Remote Execution** | **Window > Remote Execution** |
| `com.xw.hybridclr.remote-execution` | `com.xw.remote-execution` |

`IRemoteAssemblyBundleLoader`、`RemoteExecutionRuntime`、通用 Editor build-provider API 和程序集专用协议帧均已删除。请直接更新命名空间、asmdef 引用、场景组件、配置资产和自定义 Editor 集成。
