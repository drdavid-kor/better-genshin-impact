using System;
using System.Globalization;
using System.Text;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>同一组实例参数用于提示词和交互终端，路径始终作为 PowerShell 字面量。</summary>
internal static class AutomationCliPromptBuilder
{
    internal static string QuotePowerShell(string value)
    {
        // PowerShell 也把弯引号视作字符串定界符，Windows 路径允许这些字符。
        foreach (var quote in new[] { '\'', '\u2018', '\u2019', '\u201a', '\u201b' })
            value = value.Replace(quote.ToString(), new string(quote, 2));
        return "'" + value + "'";
    }

    internal static string Create(string cliPath, string installationPath, int sessionId, string? goal)
    {
        var objective = string.IsNullOrWhiteSpace(goal)
            ? "先列出本机可用的脚本、配置组和一条龙，说明可以执行什么，等待我给出具体任务后再提交。"
            : goal.Trim();
        return $$"""
            请通过本机 BetterGI CLI 帮我完成下面的目标。你需要能执行本机 Windows 命令；如果没有此能力，请直接说明。

            我的目标：
            {{objective}}

            使用以下 PowerShell 环境，保持这个安装目录和 Windows 会话，不要换用其他 BetterGI 实例：
            ```powershell
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $bettergi = {{QuotePowerShell(cliPath)}}
            $bgiInstall = {{QuotePowerShell(installationPath)}}
            $bgiSession = {{sessionId.ToString(CultureInfo.InvariantCulture)}}
            & $bettergi schema
            & $bettergi doctor --install $bgiInstall --session $bgiSession
            & $bettergi tasks list --install $bgiInstall --session $bgiSession
            ```

            所有 CLI 输出都是 UTF-8 JSON，包括非零退出码时的输出。先查看 schema，再检查 doctor 的 data.ready、data.busy、data.reason。服务未开启时，提示我在 BetterGI 的「AI / CLI」页面点击「启动 CLI 服务」；游戏或截图器未就绪时，按 reason 指导我准备。不要重启活动任务或自动开启游戏。

            从 tasks list 返回的精确 kind/name 中选择任务，再用 tasks show <kind> <name> 核对；类型为 script、group、one-dragon。有歧义先澄清，不猜任务名。沿用已有授权，不额外运行任务或修改配置。

            每次新的执行先生成并保存一个 GUID：$runId = [guid]::NewGuid().ToString()。
            提交格式：& $bettergi run <kind> <name> --request-id $runId --install $bgiInstall --session $bgiSession。
            查询：& $bettergi runs get $runId --install $bgiInstall --session $bgiSession。
            等待：& $bettergi runs wait $runId --timeout 300 --install $bgiInstall --session $bgiSession。
            只有 script 接受 --parameters <UTF-8 JSON对象文件>；参数应来自脚本实际设置定义。group 和 one-dragon 使用已保存的配置。

            提交断线或超时，先查询原 GUID；需要重试时使用相同 GUID 和相同参数，不能换 ID 补跑。等待超时只结束等待，不取消任务。仅在我要求取消该任务时用 runs cancel $runId，然后继续等到终态；cancelRequested 不表示已经停止。

            顶层 requestId 是本次通信标识，保存的运行 ID 对应 data.runId，不要混淆。ok=true 或退出码 0 不一定表示任务成功，必须检查 data.state、data.error 和 data.logs。failed、cancelled、interrupted 均不能报告为成功；全部跳过的 no_tasks_executed 也不是完成。succeeded 仅表示流程未报告错误，不独立证明游戏奖励。

            最后报告实际选择的任务、run ID、最终状态和必要的错误信息；若还在等待，明确说明尚未完成。
            """;
    }

    internal static string CreateTerminalScript(string cliPath, string installationPath, int sessionId) => $$"""
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        $bettergi = {{QuotePowerShell(cliPath)}}
        $bgiInstall = {{QuotePowerShell(installationPath)}}
        $bgiSession = {{sessionId.ToString(CultureInfo.InvariantCulture)}}
        $env:BGI_INSTALL_DIR = $bgiInstall
        Set-Location -LiteralPath $bgiInstall
        Set-Alias -Name bettergi -Value $bettergi -Scope Global
        Write-Host 'BetterGI CLI 已连接到当前安装。可输入 bettergi schema 或 bettergi tasks list。'
        Write-Host '本终端仅做连接诊断，没有提交游戏任务。'
        & $bettergi doctor --install $bgiInstall --session $bgiSession --timeout 5
        """;

    internal static string EncodeTerminalScript(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
