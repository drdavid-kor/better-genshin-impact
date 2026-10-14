# BetterGI CLI

面向 AI 和命令行工具的 BetterGI 本地客户端。需要 .NET 8；连接功能需要 Windows 和已经启动、包含自动化桥接服务的 BetterGI。客户端不会启动应用、提升权限或修改系统设置。

```powershell
bettergi schema
bettergi doctor
bettergi tasks list --install 'C:\BetterGI'
bettergi tasks show script '示例脚本' --install 'C:\BetterGI'
$runId = [guid]::NewGuid().ToString()
bettergi run script '示例脚本' --request-id $runId --install 'C:\BetterGI'
bettergi runs wait $runId --install 'C:\BetterGI' --timeout 60
```

`runId` 恒等于提交前生成的 `--request-id`。提交超时后，使用 `runs get` 查询原 ID；相同 ID 和参数可以幂等重试，不要更换 ID 再次运行。

所有输出为单个 JSON 文档，包含 `exitCode`。`run/get/cancel` 返回 0 只说明请求成功；使用 `run --wait` 或 `runs wait` 获取任务终态退出码。等待超时和 Ctrl+C 不取消任务。`runs cancel` 后仍需查询终态。

`--parameters` 接受 UTF-8 JSON 对象文件；支持的参数由 `tasks show` 和目标脚本设置定义说明。全局选项为 `--install`（也可用 `BGI_INSTALL_DIR`）、`--session` 和 `--timeout`。运行 `bettergi schema` 获取完整命令、退出码和状态契约。

源码仓库中的 `Docs/development/automation-cli.md` 与 `Docs/api/automation-cli.md` 提供开发和协议说明。
