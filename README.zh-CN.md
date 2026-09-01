# HybridCLR Remote Execution（简体中文）

[English](README.md) · 简体中文

这是一个**仅用于开发阶段**的 Unity Package：它在 Unity Editor 与已打包的 HybridCLR Player 之间建立 TCP 桥接，用于把当前项目的热更新程序集编译后发送到已连接的 Player，并调用显式标记的开发命令。

> 请不要把它当作生产环境的远程控制或更新系统。远程连接成功后，对端可以调用 Player 中所有使用 `RemoteCallableAttribute` 显式标记的方法。

## 功能概览

- Editor 端启动 TCP 服务器，最多同时保留 4 个 Player 连接。
- Player 端通过随机挑战值和 HMAC-SHA256 令牌完成认证。
- Editor 根据连接的 Player 平台编译 HybridCLR 热更新程序集。
- 程序集通过带清单的分片传输，并在 Player 端校验长度和 SHA-256 哈希后才会加载。
- Player 只发现静态、无参数且显式标记了 `[RemoteCallable]` 的方法。
- 支持 `void`、`Task` 和 HybridCLR 项目中常用的 `UniTask` 返回类型。

## 环境要求

- Unity 2021.3 或更高版本（Package 声明的最低版本为 2021.3）。
- HybridCLR 8.13.0（由 `package.json` 声明）。
- 已启用 HybridCLR，并配置至少一个热更新程序集。
- Player 必须使用与 Editor、Unity、HybridCLR 和目标平台匹配的构建配置。
- 必须构建并运行 Development/Debug Player。组件在 Unity Editor 内运行时会主动禁用。

## 快速开始

### 1. 配置 HybridCLR

1. 在项目中启用 HybridCLR。
2. 将需要远程编译和发送的程序集加入 HybridCLR 的热更新程序集配置。
3. 为目标平台生成 HybridCLR AOT 元数据。

### 2. 创建 Player 配置

1. 在 Project 窗口中创建 `RemoteExecutionSettings`：
   `Create > HybridCLR > Remote Execution Settings`。
2. 将该资产拖到启动场景中的 `RemoteExecutionComponent`。
3. 在 Editor 的 Remote Execution 窗口中复制 **Session Token**，填入配置资产的 **Authentication Token**。
4. 检查 Editor 主机地址和端口。默认值为 `127.0.0.1:38421`。
5. 如果使用 IL2CPP 且启用了 AOT 元数据加载，将生成的元数据文件作为 `TextAsset` 添加到 **AOT Metadata Assemblies**。
6. 仅在 Development Player 中启用该组件。

建议不要把真实的认证令牌提交到公共版本库；可以在本地或开发环境中单独维护配置资产。

### 3. 启动 Editor 服务器

在 Unity 菜单中打开：

**Window > HybridCLR > Remote Execution**

在窗口中：

1. 确认 HybridCLR 状态为 `Enabled`。
2. 设置 **Bind Address**。本机开发请保持 `127.0.0.1`。
3. 设置 **Port**。填写 `0` 可让操作系统分配随机端口。
4. 确认 **Session Token** 与 Player 配置中的令牌一致。
5. 点击 **Start**。
6. 启动已配置好的 Development Player，等待它出现在 **Connected Players** 列表中。
7. 对已认证的连接点击 **Compile, Load & Execute**。

服务器会根据 Player 在握手中报告的目标平台进行编译，不要在同一个连接上发送其他平台的程序集。

### 手动代码与程序集选择

窗口中的 **Custom C# Code** 接受完整 C# 类型。入口类型的 `FullName` 和方法名需要单独填写；入口必须带 `[RemoteCallable]`，是 `static`、无参数，并返回 `void`、`Task` 或 `UniTask`。代码会编译为固定名称 `RemoteExecution.Dynamic` 的独立程序集，然后在 Player 完成加载后自动调用。

在 **Assemblies to Compile & Send** 中可以多选 HybridCLR 热更新程序集。工具会根据 Player 脚本程序集引用补齐已配置的热更新依赖，并按依赖顺序发送。**Assembly Defines** 显示当前所选程序集的编译宏，需要手动勾选。

使用独立动态程序集前，请先将 `RemoteExecution.Dynamic` 配置为 HybridCLR 热更新程序集并在构建 Player 时完成对应的 AOT/HybridCLR 配置，否则 IL2CPP Player 可能无法加载该程序集。动态程序集同名替换不受 Unity 进程内卸载支持，修改代码后通常需要重启 Player。

## 配置项

`RemoteExecutionSettings` 中的字段如下：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 是否启用 Player 端桥接。仅在满足 Development/Debug 条件时才会真正连接。 |
| `Editor Host` | `127.0.0.1` | Player 连接的 Editor 主机名或地址。 |
| `Editor Port` | `38421` | Player 连接的 TCP 端口。 |
| `Authentication Token` | 空 | 必须与 Editor 窗口中的 Session Token 完全一致。为空时不会连接。 |
| `Client Id` | 空 | Player 标识。为空时使用 `SystemInfo.deviceUniqueIdentifier`。 |
| `Max Bundle Bytes` | 128 MiB | 单次接收的程序集总大小上限，范围为 1 字节到 128 MiB。 |
| `Load AOT Metadata` | `true` | 在 IL2CPP 构建中是否加载下方列出的 AOT 元数据。 |
| `AOT Metadata Assemblies` | 空 | 要传给 `RuntimeApi.LoadMetadataForAOTAssembly` 的 `TextAsset` 列表。 |

认证令牌不会写入网络加密层。它只用于握手认证；TCP 内容本身未加密。

## 声明可调用方法

在需要从远程端触发的程序集内添加 `RemoteCallableAttribute`：

```csharp
using System.Threading.Tasks;
using HybridCLR.RemoteExecution;

public static class DevelopmentCommands
{
    [RemoteCallable("执行开发环境检查")]
    public static void RunCheck()
    {
        // 开发检查逻辑
    }

    [RemoteCallable("执行异步开发任务", timeoutSeconds: 60)]
    public static async Task RunAsyncCheck()
    {
        await Task.Yield();
        // 异步开发逻辑
    }
}
```

方法需要满足以下条件：

- 必须是 `static`。
- 不能有参数。
- 不能是泛型方法，也不能包含泛型参数。
- 返回值必须是 `void`、`Task` 或 `UniTask`（包括泛型 `UniTask<T>`）。
- `Description` 不能为空白，长度为 1 到 1024 个字符。
- `timeoutSeconds` 必须在 1 到 3600 之间。
- `async void` 不受支持。

方法 ID 由以下内容组成：

```text
程序集名称::声明类型的 FullName::方法名称
```

因此，不同程序集中的相同类型名通常不会冲突；重复的完整 ID 会导致方法发现失败。方法发现会扫描当前 AppDomain 中已加载的程序集以及刚刚加载的程序集，结果按 ID 排序。

`timeoutSeconds` 会作为方法元数据发送给协议客户端。目前 Player 端的调用实现不会自动根据该值取消正在执行的任务；如果命令需要超时控制，应在命令自身或上层客户端中实现。

## 运行流程

一次典型的连接和发送过程如下：

1. Player 发送 `Hello`，报告客户端 ID、目标平台、Unity 版本和 HybridCLR 版本。
2. Editor 发送随机 `Challenge`。
3. Player 使用配置的令牌计算 HMAC-SHA256 并发送 `Authenticate`。
4. 认证成功后，Editor 返回 `Ready`。
5. Editor 编译已配置的热更新程序集并发送 `LoadManifest`。
6. 每个 DLL（以及可选的 PDB）按 `AssemblyBegin`、多个 `AssemblyChunk`、`AssemblyEnd` 的顺序传输。
7. Player 校验清单、长度、分片顺序和 SHA-256 哈希。
8. 收到 `LoadComplete` 后，Player 调用 `Assembly.Load`，刷新可调用方法，并返回应用结果。

协议还定义了 `ListMethods` 和 `Invoke`，用于查询和调用已登记方法。当前内置 Editor 窗口提供程序集编译发送、加载和自定义代码执行。

## 安全注意事项

- 默认绑定到 `127.0.0.1`，建议本机开发时保持此设置。
- HMAC 只负责认证，不负责加密。若必须进行局域网开发，请使用可信网络或额外的安全隧道。
- 不要在生产 Player 中启用此组件，也不要在不可信网络上暴露监听端口。
- 认证成功的对端可以调用所有标记了 `[RemoteCallable]` 的方法。不要标记删除文件、执行任意外部命令、修改生产数据等高风险操作。
- 传输大小受限于清单和 `Max Bundle Bytes`；单个协议帧最多 1 MiB，单个分片最多 60 KiB。
- 令牌应视为敏感的开发凭据，不要将其发布到日志、截图或公共仓库。

## 常见问题

### Player 没有出现在连接列表中

检查以下项目：

- Player 是 Development/Debug 构建，而不是普通发布构建。
- `RemoteExecutionComponent` 在启动场景中，配置资产已赋值且 `Enabled` 为 `true`。
- `Authentication Token` 非空，并且与当前 Editor 窗口中的 Session Token 完全一致。
- `Editor Host`、端口和防火墙规则正确。
- Editor 服务器已经点击 **Start**，且绑定地址不是错误的本机网卡地址。
- Unity Editor 中的组件不会连接；必须运行打包后的 Player。

### 启动服务器失败

- 确认 HybridCLR 已启用。
- 检查 Bind Address 是否为有效 IP 地址，端口是否在 `0..65535` 范围内且未被占用。
- 确认令牌不为空。

### 编译或发送失败

- 检查目标平台是否与 Player 报告的平台一致。
- 确认目标平台已生成对应的 Player 脚本编译输出。
- 确认 HybridCLR 配置中至少有一个热更新程序集，并且程序集 DLL 已出现在编译输出中。
- 确认程序集总大小没有超过 `Max Bundle Bytes`。

### 加载程序集失败

- 检查 AOT 元数据是否为正确目标平台生成，并已添加到配置资产。
- 确认 DLL 与 Player 使用匹配的 Unity、HybridCLR 和 BuildTarget 配置。
- Unity 进程不会卸载已加载的程序集。如果相同程序集名称对应了不同 DLL，Player 会拒绝加载；修改源码后请重启 Player。
- 通过日志查看具体的哈希、清单或 `Assembly.Load` 错误。

## 限制

- 这是开发工具，不是热更新发布系统。
- 当前发送的是 HybridCLR 配置中的热更新程序集集合，不会修改 `Assets/Res`、`ET`、`GameHot` 或项目自定义的加载器。
- 已加载程序集不会在 Unity 进程内卸载；同名但不同版本的程序集需要重启 Player 后才能加载。
- 当前协议只支持无参数调用，不支持向远程方法传递参数或返回业务数据。
