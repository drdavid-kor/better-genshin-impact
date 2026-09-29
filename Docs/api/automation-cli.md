# BetterGI 自动化 CLI

`bettergi` 为本机 AI Agent 和脚本提供任务发现、执行、查询、等待与取消入口。首批入口为 JavaScript 脚本、配置组、一条龙；具体游戏行为仍由现有 BetterGI 实现。它需要包含自动化桥接支持的 BetterGI 主程序，不能直接控制未修改的旧版二进制。

## 启用宿主

在已运行的 BetterGI 中打开 **AI / CLI** 页面，点击 **启动 CLI 服务**，即可为当前进程即时启用接口，无需退出重启。此开关不保存到下次启动；下次仍需从页面启用，或在启动参数中加入 `--enable-cli`。

页面还提供 **打开 CLI 终端** 和 **复制 AI 提示词**。打开终端会启用服务并运行连接诊断，不执行游戏任务。复制的提示词包含当前 CLI 绝对路径、BetterGI 安装目录和 Windows Session，可直接交给支持本机 Windows 命令的 AI 使用，无需另行安装 skill。可填写任务目标；留空时提示词只让 AI 先发现可用任务。复制本身不启用服务或提交任务。客户端随程序放在 `<安装目录>/CLI/bettergi.exe`，无需先安装全局 .NET tool 或修改 PATH。

也可以使用现有管理员终端或启动器运行：

```powershell
& 'C:\Games\BetterGI\BetterGI.exe' --enable-cli
```

自动化接口默认关闭。主程序已经运行时，直接从 **AI / CLI** 页面启用即可；再次启动一个带参数的进程不会改造原实例。CLI 自身不会启动主程序或请求提权。

执行新任务前，还需要 BetterGI 的 WPF 窗口正常运行、用户已在启动页启用截图器、`TaskContext` 初始化完成，并且所连接的原神窗口仍有效。CLI 不会自动启动游戏、启用截图器或弹出启动确认框；未满足条件时返回 `not_ready`。按照 `doctor` 的 `data.reason` 手动准备后重新诊断：未启用截图器时先在界面启用，初始化中则等待，原游戏窗口失效时重新连接游戏并启用截图器。任务发现和既有运行的查询、等待、取消仍可使用。

连接使用每 Windows 用户、每 Windows Session 的本地命名管道，仅允许同用户，并拒绝网络客户端。`--install` 指向包含 `BetterGI.exe` 的安装目录，连接时核对宿主的安装路径，避免误操作另一份安装。`--session` 默认当前 Session；不提供跨用户或网络控制。

## 命令

```text
bettergi doctor
bettergi schema
bettergi tasks list [--kind script|group|one-dragon]
bettergi tasks show <kind> <exactName>
bettergi run <kind> <exactName> --request-id <GUID> [--parameters <JSON_FILE>] [--wait]
bettergi runs get <GUID>
bettergi runs wait <GUID>
bettergi runs cancel <GUID>
```

| 全局选项 | 含义 |
| --- | --- |
| `--install <directory>` | 目标 BetterGI 安装目录；也可用 `BGI_INSTALL_DIR`。`doctor` 可省略以发现当前宿主，其余联网命令必填 |
| `--session <id>` | 目标 Windows Session，默认当前调用者的 Session |
| `--timeout <seconds>` | 连接/请求或整个等待的秒数，默认 30，范围 0.05–86400；超时不会取消宿主任务 |

`schema` 和 `help` 提供当前 CLI 的机器可读契约，无需宿主连接，也不接受全局选项。`doctor` 检查运行中的宿主连接与安装选择，不是全面的原生依赖或游戏设置扫描。所有命令的 stdout 均为单个 camelCase JSON 值；调用方在非零退出码时也应读取 JSON 错误，不应按控制台文本匹配任务状态。

任务名称必须来自 `tasks list`，再用 `tasks show` 检查详情。查找使用明确的 kind 和精确名称，没有近似匹配或默认配置回退。脚本的 `--parameters` 文件必须是 UTF-8 JSON 对象，宿主接收的序列化参数不超过 16 KiB（CLI 读取文件另有 512 KiB 上限）；配置组和一条龙沿用本机现有配置，不支持运行时参数覆盖。包含空格的名称需要正确引用；以 `--` 开头的名称可在所有选项后用 `--` 分隔位置参数。

| kind | 发现来源 | name 与设置 |
| --- | --- | --- |
| `script` | `User/JsScript` 下带 `manifest.json` 的目录 | `name` 是目录名；`details.displayName` 是展示名。`details.settingsFile` 指向脚本设置定义，不是完整参数 schema |
| `group` | `User/ScriptGroup/*.json` | `name` 是配置中的组名；使用已保存的项目、参数、运行次数和队伍设置 |
| `one-dragon` | `User/OneDragon/*.json` | `name` 是配置中的名称；使用已保存的启用项和配置，完成后操作须为“无”或未设置 |

`tasks list` 返回 `data.tasks`，`tasks show` 返回 `data.task`。任务包含 `kind`、`name`、`description`、`canRun`，以及可用时的 `unavailableReason` 和 `details`。配置无法读取或不可运行时仍可能出现在列表中，但 `canRun` 为 false；重名、未启用任务的配置等不会被静默执行。

`canRun` 仅代表目录发现阶段的基础校验。一条龙在开始任何任务动作前，还会检查其已启用的自定义配置组引用：名称须为合法单个文件名，`User/ScriptGroup/<name>.json` 必须存在，文件内部组名须与引用名逐字一致。引用错误会让已接受的 run 进入 `failed`，`data.error.code` 为 `execution_failed`，具体问题见 `data.result.reportedErrors`；因此可能先发现 `canRun=true`，再在执行前因引用错误失败。实际读取各配置组时再次检查名称，不回退其他组或替用户改配置。

脚本运行时通过 `--parameters` 传入 settings 对象；不传时为空对象，使用脚本自身默认值，不读取某个 UI 选中配置组的参数。需要自定义参数时，读取该脚本目录下 `details.settingsFile` 指向的设置定义及说明后构造对象。

## JSON 与退出码

联网响应的公共字段为 `apiVersion`、`requestId`、`ok`、`instance`，另按结果包含 `data` 或 `error`。顶层 `requestId` 是本次 RPC 的关联 ID；查询、等待、取消时通常与被操作的 `data.runId` 不同，不能将其当作原运行 ID。`instance` 包含 `instanceId`、`installationPath`、`processId`、`windowsSessionId`、`startedAt` 等身份信息。CLI 添加 `exitCode`，值与进程退出码一致。`help`/`schema` 和客户端本地错误不包含宿主身份，空字段可能被省略。

运行命令的 `data` 是 run 记录，包含 `runId`、`requestId`、`instanceId`、`kind`、`name`、`state`、`createdAt`，以及可用时的 `startedAt`、`finishedAt`、`result`、`error`、`logs`。请求错误在顶层 `error`；任务执行错误在 `data.error`，两者均使用 `code` 和 `message`。日志条目包含 `sequence`、`timestamp`、`level`、`message`。

宿主执行结果 `data.result` 包含 `executedCount`、`skippedCount`、`reportedErrors`、`resultBasis`。真正跳过的项目只增加 `skippedCount`，不计入 `executedCount`。流程未报告其他错误，但全部任务跳过或没有任务进入执行时，终态为 `failed`，错误码为 `data.error.code=no_tasks_executed`；不会以辅助奖励检查冒充执行成功。计数是执行流程的观测结果，不是游戏收益的证明。

`doctor` 的 `data` 包含 `ready`、`busy`、`reason`、`activeRunId` 和 `capabilities`。即使 `ok` 为 true，也应检查 `ready` 和 `reason`；成功获取诊断结果不等于宿主可以接受新任务。

| 退出码 | 含义 |
| --- | --- |
| 0 | 请求成功；等待类命令则表示任务 `succeeded` |
| 2 | 命令或参数无效 |
| 3 | 实例不可用、平台不支持、通信或协议错误 |
| 4 | 宿主拒绝请求 |
| 5 | 等待的任务 `failed` |
| 6 | 等待的任务 `cancelled` |
| 7 | 连接、请求或等待超时；不代表任务已取消 |
| 8 | 客户端等待中断、实例变更或任务 `interrupted` |

`run`（不带 `--wait`）、`runs get`、`runs cancel` 的退出码只反映请求处理；即使查到 `failed`，请求仍可退出 0。`run --wait` 和 `runs wait` 才按任务终态返回对应退出码。Ctrl+C 仅停止客户端等待，不发送宿主取消请求。

## 执行闭环

```powershell
$install = 'C:\Games\BetterGI'
$cli = Join-Path $install 'CLI\bettergi.exe'
& $cli schema
& $cli doctor --install $install
& $cli tasks list --kind script --install $install
& $cli tasks show script '列表中返回的精确名称' --install $install

$runId = [Guid]::NewGuid().ToString()
& $cli run script '列表中返回的精确名称' --request-id $runId --install $install
& $cli runs get $runId --install $install
& $cli runs wait $runId --timeout 300 --install $install
```

以上命令默认当前 Windows Session。从页面复制的提示词指定了 Session 时，对 `doctor`、任务和运行命令保留其中的 `--session <id>`；离线 `schema` 不接受该选项。

每一次新的执行生成一个 GUID，执行记录的 `data.requestId` 与 `data.runId` 相同。调用方应在提交前保存该值，以及安装目录、Session、kind、名称与参数。重复提交同一 GUID、同一任务和参数不会重复执行；同一 GUID 配合不同任务或参数会报冲突。

提交时如果连接断开或超时，先查询原 GUID。不能通过生成新 GUID 来“重试”结果未知的执行，否则可能重复消耗游戏资源。只有明确决定启动另一次执行时才生成新 GUID。

`run --wait` 将提交与等待合并。长任务也可以先提交，再分多次 `runs wait`；某一次等待结束或超时不会改变宿主任务状态。

```powershell
& $cli runs cancel $runId --install $install
& $cli runs wait $runId --timeout 300 --install $install
```

取消是协作式请求。收到 `cancelRequested` 后继续查询或等待，直到终态；不能将“已收到取消请求”报告为“已经停止”。取消只能针对该 run，不应为了启动 CLI 任务去取消无关的界面任务。

## 状态与结果

| 状态 | 含义 | 终态 |
| --- | --- | --- |
| `accepted` | 已持久接收，等待进入执行 | 否 |
| `running` | 已进入现有 BetterGI 执行流程 | 否 |
| `cancelRequested` | 已请求取消，等待流程结束 | 否 |
| `succeeded` | 流程结束，未观测到任务错误 | 是 |
| `failed` | 任务执行失败，查看错误详情 | 是 |
| `cancelled` | 取消后的流程已结束 | 是 |
| `interrupted` | 宿主中断，无法确认原任务正常收尾 | 是 |

响应 `ok` 表示请求得到处理，不能据此推断游戏任务完成。必须进一步检查 `data.state`。`succeeded` 也只代表既有执行流程没有报告错误，不是对奖励、背包、角色位置或其他游戏目标的独立验证。需要此类结论时，应另行核对游戏状态。

宿主重启后不会自动重放未完成任务。恢复后应先读取原 run 的记录，再根据实际游戏状态决定下一步。一条龙配置若包含关闭 BetterGI、关机等妨碍状态收尾的结束动作会被拒绝；CLI 不会替用户静默修改配置。

## 持久化与边界

运行记录保存在目标安装的 `User/Automation/session-<id>/<runId>.json`。每个 Session 独立记录，接收和状态转移先落盘。启动恢复时，没有终态的历史运行改为 `interrupted`，不自动续跑。去重依赖这些记录；删除或归档记录后，不能再把原 ID 当作可安全重试的 key。

历史上限为 4096 条，到达上限后拒绝新任务，保留既有记录供查询。历史损坏或无法读取时也停止接受新任务，避免将已执行任务误认为新任务。维护时先停止宿主、备份并归档记录，且永久避免重用已归档的 request ID；不要在 Agent 的正常重试流程中自动清空历史。

每个 run 返回最近 100 条日志，每条最多 1000 字符；日志是有界观测信息，在重要状态转移时落盘，不是完整实时审计日志。执行结果对象上限为 16 KiB，传输单条消息上限为 1 MiB。

常见请求错误包括 `installation_mismatch`、`task_not_found`、`ambiguous_task`、`task_unavailable`、`busy`、`not_ready`、`idempotency_conflict`、`run_not_found`、`history_full` 和 `history_unavailable`。应按 code 处理，并保留 message 供用户诊断；尤其不能仅凭 `run_not_found` 就更换 GUID 重跑结果未知的任务。

## Skills

仓库附带两个可单独复制的自包含 skill：

- [bettergi-tasks](../../.agents/skills/bettergi-tasks/SKILL.md)：任务发现与执行闭环。
- [bettergi-setup](../../.agents/skills/bettergi-setup/SKILL.md)：安装、启用及诊断。

复制到目标 Agent 支持的 skill 目录后，仍需单独安装 CLI 和匹配宿主。skill 继承用户的既有任务授权，不要求对每个正常步骤重复确认，也不授权安装未知脚本、修改无关配置或执行额外任务。

构建和验证步骤见 [开发文档](../development/automation-cli.md)。
