# 自动化 CLI 开发

本功能以独立协议/执行状态模块、.NET tool 和宿主适配层接入 BetterGI。CLI 调用现有脚本、配置组和一条龙执行入口，避免在外部进程重建截图、键鼠或脚本引擎。

## 模块边界

| 路径 | 职责 |
| --- | --- |
| `BetterGenshinImpact.Automation` | 协议、任务与运行记录、状态管理和本地传输 |
| `BetterGenshinImpact.Cli` | `bettergi` .NET tool、参数解析、JSON 输出和退出码 |
| `BetterGenshinImpact` 中的自动化适配层 | 接入既有执行流程、UI 线程、取消和日志观测 |
| `BetterGenshinImpact/View/Pages/AutomationPage.xaml`、`BetterGenshinImpact/ViewModel/Pages/AutomationPageViewModel.cs` | AI / CLI 页面、服务状态、终端和提示词操作 |
| `BetterGenshinImpact/Service/Automation/AutomationCliPromptBuilder.cs` | 使用当前安装路径和 Session 生成提示词与终端脚本 |
| `Test/BetterGenshinImpact.Cli.Tests` | CLI 参数及行为测试 |
| `Test/BetterGenshinImpact.Automation.Tests` | 状态、持久化和协议测试 |
| `.agents/skills/bettergi-*` | 面向 AI 的运行与配置操作说明 |

具体用户命令见 [API 文档](../api/automation-cli.md)。默认不启用宿主接口；可由 **AI / CLI** 页面为当前进程即时启用，或通过启动参数 `--enable-cli` 开启。页面开关不持久保存到下一次启动。CLI 不承担主程序启动、提权或桌面分身创建。

## 构建、打包与安装

需要 Windows 和 .NET 8 SDK，主程序继续使用仓库原有的 WPF、NuGet、地图/模型及前端资源准备方式。从仓库根目录执行：

```powershell
dotnet build BetterGenshinImpact.sln -c Debug -p:Platform=x64
```

主程序的构建和发布输出随附 `CLI/bettergi.exe` 及其运行文件；仅构建主工程也会构建并复制 CLI，无需安装工具包。发布为单文件宿主时，CLI 仍以独立目录保留。可使用 `.github/scripts/verify-bundled-cli.ps1 -HostDirectory <主程序输出目录>` 检查客户端文件并运行离线 `schema`，不启动游戏。

需要单独部署 .NET tool 时，再打包并安装 `BetterGenshinImpact.Cli` 到明确的本地目录。以下使用初始包版本 `0.1.0`；后续版本以实际生成的包为准，避免从公共源安装同名包。对已有安装使用 `dotnet tool update`，不要删除工具目录来替代更新。

```powershell
dotnet pack BetterGenshinImpact.Cli/BetterGenshinImpact.Cli.csproj -c Release -o ./artifacts/cli
$packageSource = (Resolve-Path './artifacts/cli').Path
dotnet tool install BetterGenshinImpact.Cli --version 0.1.0 --tool-path ./artifacts/cli-tool --add-source $packageSource
& './artifacts/cli-tool/bettergi.exe' schema
```

将 `--install` 指向主程序构建输出中包含 `BetterGI.exe` 的目录，而不是源码根目录或 CLI 工具目录。在主程序的 **AI / CLI** 页面点击 **启动 CLI 服务**，或在管理员终端启动 `BetterGI.exe --enable-cli`，再用 CLI 的 `doctor` 与 `tasks list` 验证连接。**打开 CLI 终端** 会启用服务并运行 `doctor`；**复制 AI 提示词** 只复制当前路径、安装目录、Session、可选目标和调用规则，不启用服务。这些操作都不提交游戏任务。普通用户命令行可以在同用户、同 Session 下连接管理员宿主，且无需开放网络端口。

执行就绪还要求 WPF 窗口运行、用户已在界面启动页启用截图器、`TaskContext` 初始化完成以及游戏窗口句柄仍有效。`doctor` 返回可直接指导用户手动准备的 `reason`。CLI 路径中的 `StartGameTask` 只验证这些条件，不自动启动游戏/截图器或弹出启动确认框；就绪条件在真正执行前会再次检查。连接成功、任务可发现和可以接受新任务是不同层面的状态。

## 维护不变量

- 新任务必须先持久记录再响应接收；恢复时不能自动重放结果未知的任务。
- 执行记录的 `requestId` 就是 `runId`，与查询/取消 RPC 的顶层关联 ID 区分。相同 GUID 与相同请求应返回原运行；载荷冲突必须拒绝，不能另开运行。
- 状态更新不得让终态回退。取消请求、取消完成和等待超时必须分别表示。
- 同一宿主的游戏输入任务不能并行争用现有单任务执行器。CLI 应报告繁忙，不得抢占无关 UI 任务。
- 界面或 WPF 状态访问需遵循现有 Dispatcher/STA 约定。实际执行和清理完成后才记录终态；引擎保留活动记录直到终态持久化完成。
- 既有流程可能通过日志报告错误。不能仅因为返回的 `Task` 未抛异常就宣称游戏成功；保持文档中的可观测结果边界。
- 实际跳过的任务只计入 `skippedCount`；`executedCount` 不包含这些跳过项。全部跳过时返回 `no_tasks_executed`，不能通过后续辅助步骤把空执行包装为成功。
- 一条龙开始任务动作前校验全部已启用的自定义组引用：名称合法、同名 JSON 存在、内部组名逐字一致；实际读取时再次核对，防止预检后文件变化造成错误选择。`canRun` 的基础发现检查不代替此执行前校验。
- 连接核对安装目录和 Session，管道限同用户并拒绝网络客户端。诊断信息不应依赖任意外部网络服务。

宿主的 `Service/Automation/AutomationHostedService.cs` 将历史传入 `User/Automation/session-<id>`，复用现有 `InstancePipeFactory` 的本地同用户管道权限。自动化使用独立的 `BetterGI.Automation.v1.<SID>.session-<id>` 管道名称和协议，不修改既有实例 IPC v2。数据帧为 4 字节 little-endian 长度加 UTF-8 JSON，单帧上限 1 MiB。

`AutomationEngine` 的历史上限为 4096 条，满额或发现损坏记录时拒绝新任务，不自动回收幂等键；每次状态转移持久化到临时文件并替换原文件。参数与结果分别限制为 16 KiB；每个 run 只保留最近 100 条、每条最多 1000 字符的日志。这些限制需要在修改协议和文档时同步维护。

## 验证

运行 CLI 与自动化模块的定向测试，并构建主程序检查接入点：

```powershell
dotnet test Test/BetterGenshinImpact.Cli.Tests/BetterGenshinImpact.Cli.Tests.csproj -c Debug
dotnet test Test/BetterGenshinImpact.Automation.Tests/BetterGenshinImpact.Automation.Tests.csproj -c Debug
dotnet test Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~ServiceTests.Automation
dotnet build BetterGenshinImpact.sln -c Debug -p:Platform=x64
```

回归重点包括：参数错误仍输出单个 JSON、精确任务选择、请求去重/冲突、繁忙拒绝、取消收尾、wait 超时不取消、连接结果未知后的查询、重启后的 interrupted，以及安装路径不匹配拒绝。宿主适配层还需覆盖截图器/任务上下文/窗口有效性、一条龙跳过计数、引用组名称一致性和脚本参数字符串保真。打包后应从实际安装的 `bettergi.exe` 运行 `schema`，避免只验证 `dotnet run`。

skill 使用 `skill-creator` 随附的 `quick_validate.py` 验证 frontmatter、命名和未完成占位符。另以实际输出审查运行步骤，尤其检查断线重试是否沿用原 GUID、是否区分 `ok` 和任务成功、是否在未获授权时扩大操作范围。

### 验证证据边界

自动化测试、主程序构建、实际命名管道连通、真实游戏执行是不同层面的证据。测试通过不证明游戏任务完成；未运行游戏就不能写“脚本/配置组/一条龙已实测成功”。本次交付的命令输出和审查结果应随最终验证记录给出，不能从上述待执行步骤推断已经通过。

真实游戏验收应在用户授权的任务上验证三种入口，覆盖发现、提交、查询、等待和取消，并保留 run ID、终态以及游戏内效果证据。无需为验证安装而运行一个未经授权的游戏任务。
