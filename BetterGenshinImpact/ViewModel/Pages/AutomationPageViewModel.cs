using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class AutomationPageViewModel : ViewModel
{
    private readonly IAutomationBridgeService _bridge;
    private readonly Action<string> _copyText;
    private readonly Action<ProcessStartInfo> _openTerminal;
    private readonly Func<string, bool> _fileExists;
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _pageLifetime;
    private bool _refreshing;

    public string InstallationPath { get; }
    public string CliPath { get; }
    public int SessionId { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCliCommand))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCliCommand), nameof(OpenTerminalCommand))]
    private bool _isStarting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTerminalCommand), nameof(CopyPromptCommand))]
    private bool _cliAvailable;

    [ObservableProperty] private string _serviceStatus = "CLI 服务未启动";
    [ObservableProperty] private string _readinessText = "启动后，可让本机 AI 助手发现并调用已配置的任务。";
    [ObservableProperty] private string _feedback = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptText))]
    private string _taskGoal = string.Empty;

    public string PromptText => AutomationCliPromptBuilder.Create(CliPath, InstallationPath, SessionId, TaskGoal);

    public AutomationPageViewModel(IAutomationBridgeService bridge)
        : this(bridge, Global.StartUpPath, CurrentSessionId())
    {
    }

    internal AutomationPageViewModel(IAutomationBridgeService bridge, string installationPath, int sessionId,
        Action<string>? copyText = null, Action<ProcessStartInfo>? openTerminal = null, Func<string, bool>? fileExists = null)
    {
        _bridge = bridge;
        _copyText = copyText ?? Clipboard.SetText;
        _openTerminal = openTerminal ?? (startInfo => { using var process = Process.Start(startInfo); });
        _fileExists = fileExists ?? File.Exists;
        InstallationPath = Path.GetFullPath(installationPath);
        CliPath = Path.Combine(InstallationPath, "CLI", "bettergi.exe");
        SessionId = sessionId;
        CliAvailable = _fileExists(CliPath);
    }

    private static int CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    [RelayCommand]
    private async Task OnLoaded()
    {
        OnUnloaded();
        _pageLifetime = new CancellationTokenSource();
        _refreshTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            async (_, _) => await RefreshAsync(), Dispatcher.CurrentDispatcher);
        _refreshTimer.Start();
        await RefreshAsync();
    }

    [RelayCommand]
    private void OnUnloaded()
    {
        _refreshTimer?.Stop();
        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = null;
    }

    private bool CanStartCli() => !IsStarting && !IsEnabled;
    private bool CanOpenTerminal() => !IsStarting && CliAvailable;
    private bool CanCopyPrompt() => CliAvailable;

    [RelayCommand(CanExecute = nameof(CanStartCli))]
    private async Task StartCli()
    {
        IsStarting = true;
        Feedback = string.Empty;
        try
        {
            await _bridge.EnableAsync();
            Feedback = "CLI 服务已启动，本次运行期间有效。现在可以复制提示词给 AI 助手。";
        }
        catch (Exception exception)
        {
            Feedback = "CLI 服务启动失败：" + exception.GetBaseException().Message;
        }
        finally
        {
            IsStarting = false;
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private async Task OpenTerminal()
    {
        IsStarting = true;
        Feedback = string.Empty;
        try
        {
            if (!_fileExists(CliPath)) throw new FileNotFoundException("未找到随程序提供的 CLI，请重新构建或更新程序。", CliPath);
            await _bridge.EnableAsync();
            var script = AutomationCliPromptBuilder.CreateTerminalScript(CliPath, InstallationPath, SessionId);
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            // 用户明确打开交互终端；固定程序路径与编码脚本避免路径内容被解释为命令。
            _openTerminal(new ProcessStartInfo(shell)
            {
                UseShellExecute = true,
                WorkingDirectory = InstallationPath,
                Arguments = "-NoProfile -NoExit -EncodedCommand " + AutomationCliPromptBuilder.EncodeTerminalScript(script),
                WindowStyle = ProcessWindowStyle.Normal
            });
            Feedback = "已打开 CLI 终端。终端只运行连接诊断，不会自动提交游戏任务。";
        }
        catch (Exception exception)
        {
            Feedback = "无法打开 CLI 终端：" + exception.GetBaseException().Message;
        }
        finally
        {
            IsStarting = false;
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyPrompt))]
    private void CopyPrompt()
    {
        try
        {
            _copyText(PromptText);
            Feedback = "已复制完整提示词，可粘贴给支持本机命令执行的 AI 助手。";
        }
        catch (Exception exception)
        {
            Feedback = "复制失败，可在下方选中提示词手动复制：" + exception.GetBaseException().Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime?.Token ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            IsEnabled = _bridge.IsEnabled;
            CliAvailable = _fileExists(CliPath);
            ServiceStatus = IsEnabled ? "CLI 服务已启动" : "CLI 服务未启动";
            if (!CliAvailable)
            {
                ReadinessText = "缺少随程序提供的 CLI 文件，请重新构建或更新完整程序。";
                return;
            }
            var status = await _bridge.GetStatusAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            ReadinessText = status.Busy ? "当前有任务正在执行，请跟踪已有任务或等待其结束。"
                : status.Ready ? "连接已准备好，可以发现并执行任务。"
                : status.Reason ?? "请先在启动页启用截图器并连接游戏。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ReadinessText = "暂时无法读取状态：" + exception.GetBaseException().Message;
        }
        finally { _refreshing = false; }
    }
}
