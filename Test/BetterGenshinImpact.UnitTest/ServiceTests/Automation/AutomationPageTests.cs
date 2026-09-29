using System.Diagnostics;
using System.Text;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Service.Automation;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Automation;

public class AutomationPageTests
{
    [Fact]
    public async Task StartFromGui_EnablesCurrentBridge_AndShowsGamePreparationSeparately()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateViewModel(bridge);
        await viewModel.StartCliCommand.ExecuteAsync(null);

        Assert.Equal(1, bridge.EnableCount);
        Assert.True(viewModel.IsEnabled);
        Assert.False(viewModel.StartCliCommand.CanExecute(null));
        Assert.True(viewModel.CopyPromptCommand.CanExecute(null));
        Assert.Contains("截图器", viewModel.ReadinessText);
        Assert.Contains("已启动", viewModel.ServiceStatus);
    }

    [Fact]
    public async Task EnableFailure_DoesNotClaimReady_AndAllowsRetry()
    {
        var bridge = new FakeBridge { EnableError = new IOException("同会话管道已被占用") };
        var viewModel = CreateViewModel(bridge);
        await viewModel.StartCliCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsEnabled);
        Assert.False(viewModel.IsStarting);
        Assert.True(viewModel.StartCliCommand.CanExecute(null));
        Assert.Contains("管道已被占用", viewModel.Feedback);

        bridge.EnableError = null;
        await viewModel.StartCliCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsEnabled);
    }

    [Fact]
    public async Task OpenTerminal_FirstEnablesBridge_AndPassesOnlyEncodedDiagnosticCommands()
    {
        var bridge = new FakeBridge();
        ProcessStartInfo? launch = null;
        var viewModel = CreateViewModel(bridge, launch: info =>
        {
            Assert.True(bridge.IsEnabled);
            launch = info;
        });
        await viewModel.OpenTerminalCommand.ExecuteAsync(null);
        Assert.NotNull(launch);
        Assert.True(launch.UseShellExecute);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", launch.FileName);
        Assert.Equal(ProcessWindowStyle.Normal, launch.WindowStyle);
        var encoded = launch.Arguments.Split(' ')[^1];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("--session $bgiSession", script);
        Assert.Contains("$bgiSession = 27", script);
        Assert.Contains("& $bettergi doctor", script);
        Assert.DoesNotContain("& $bettergi run ", script);
        Assert.Contains(AutomationCliPromptBuilder.QuotePowerShell(viewModel.CliPath), script);
    }

    [Fact]
    public async Task MissingBundledCli_PreventsOpeningTerminal_OrCopyingMisleadingPrompt()
    {
        var bridge = new FakeBridge();
        var launched = false;
        var viewModel = CreateViewModel(bridge, launch: _ => launched = true, fileExists: _ => false);
        Assert.False(viewModel.OpenTerminalCommand.CanExecute(null));
        Assert.False(viewModel.CopyPromptCommand.CanExecute(null));
        // 文件可能在按钮状态检查后被移走，命令自身仍须校验。
        await viewModel.OpenTerminalCommand.ExecuteAsync(null);
        Assert.False(launched);
        Assert.Equal(0, bridge.EnableCount);
        Assert.Contains("CLI", viewModel.ReadinessText);
    }

    [Fact]
    public void CopyPrompt_PreservesTheExactTargetAndGoal_WithoutEnablingOrRunningAnything()
    {
        var bridge = new FakeBridge();
        string? clipboard = null;
        var viewModel = CreateViewModel(bridge, copy: text => clipboard = text);
        viewModel.TaskGoal = "请运行我配置的每日采集，并等待结果。";
        viewModel.CopyPromptCommand.Execute(null);

        Assert.Equal(viewModel.PromptText, clipboard);
        Assert.Contains(viewModel.TaskGoal, clipboard);
        Assert.Contains(AutomationCliPromptBuilder.QuotePowerShell(viewModel.InstallationPath), clipboard);
        Assert.Contains(AutomationCliPromptBuilder.QuotePowerShell(viewModel.CliPath), clipboard);
        Assert.Contains("$bgiSession = 27", clipboard);
        Assert.Equal(0, bridge.EnableCount);
        Assert.False(bridge.IsEnabled);
    }

    [Fact]
    public void ClipboardFailure_KeepsPreviewAvailable_AndDoesNotThrow()
    {
        var viewModel = CreateViewModel(new FakeBridge(), copy: _ => throw new System.Runtime.InteropServices.ExternalException("busy"));
        viewModel.CopyPromptCommand.Execute(null);
        Assert.Contains("手动复制", viewModel.Feedback);
        Assert.Contains("等待我给出具体任务", viewModel.PromptText);
    }

    [Fact]
    public async Task Refresh_AfterListenerStops_RemovesEnabledState()
    {
        var bridge = new FakeBridge();
        var viewModel = CreateViewModel(bridge);
        await viewModel.StartCliCommand.ExecuteAsync(null);
        bridge.IsEnabled = false;
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsEnabled);
        Assert.True(viewModel.StartCliCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("E:\\我的 原神\\O'Brien\\$(whoami);`test")]
    [InlineData("E:\\弯引号‘’‚‛\\游戏")]
    public async Task PromptAndTerminalPaths_ArePowerShellLiterals_ThatRoundTripWithoutExecution(string path)
    {
        // 用系统 Windows PowerShell 的解析器校验路径 AST；绝不执行生成的终端脚本。
        var literal = AutomationCliPromptBuilder.QuotePowerShell(path);
        var parser = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
            "$source=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(literal)) + "')); " +
            "$tokens=$null; $errors=$null; " +
            "$ast=[System.Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors); " +
            "if($errors.Count -ne 0){exit 1}; " +
            "$value=$ast.Find({param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst]},$true).Value; " +
            "[Console]::Write([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($value)))";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(AutomationCliPromptBuilder.EncodeTerminalScript(parser));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await errors);
        Assert.Equal(path, Encoding.Unicode.GetString(Convert.FromBase64String(await output)));
    }

    private static AutomationPageViewModel CreateViewModel(FakeBridge bridge, Action<string>? copy = null,
        Action<ProcessStartInfo>? launch = null, Func<string, bool>? fileExists = null) =>
        new(bridge, @"E:\测试 目录\O'Brien", 27, copy ?? (_ => { }), launch ?? (_ => { }), fileExists ?? (_ => true));

    private sealed class FakeBridge : IAutomationBridgeService
    {
        public bool IsEnabled { get; set; }
        public int EnableCount;
        public Exception? EnableError;
        public Task EnableAsync(CancellationToken cancellationToken = default)
        {
            EnableCount++;
            if (EnableError is not null) throw EnableError;
            IsEnabled = true;
            return Task.CompletedTask;
        }
        public Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutomationBackendStatus { Ready = false, Reason = IsEnabled ? "请在启动页启用截图器。" : "请先启动 CLI 服务。" });
    }
}
