using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>仅在显式 --enable-cli 启动时开放同用户、同 Session 的自动化管道。</summary>
internal sealed class AutomationHostedService(InstanceService instanceService, ILogger<AutomationHostedService> logger)
    : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private AutomationEngine? _engine;
    private AutomationPipeServer? _server;
    private NamedPipeServerStream? _firstServer;
    private Task? _runTask;
    private Task? _stopTask;
    private readonly object _shutdownLock = new();
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CommandLineOptions.Instance.EnableCli || instanceService.Context.InstanceType == BetterGiInstanceType.WebView)
            return Task.CompletedTask;

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。");
        var context = instanceService.Context;
        var pipeName = AutomationPipeNames.ForUserSession(sid, context.WindowsSessionId);
        // 同步保留第一个服务端，若同 Session 已有自动化端点，启动即明确失败。
        _firstServer = InstancePipeFactory.CreateServer(pipeName, firstPipeInstance: true);
        _engine = new AutomationEngine(new BetterGiAutomationBackend(Global.StartUpPath), new AutomationIdentity
        {
            InstanceId = $"{context.ProcessId}:{context.StartedAt.ToUnixTimeMilliseconds()}",
            InstallationPath = Path.GetFullPath(Global.StartUpPath),
            ProcessId = context.ProcessId,
            WindowsSessionId = context.WindowsSessionId,
            StartedAt = context.StartedAt
        }, Global.Absolute(Path.Combine("User", "Automation", $"session-{context.WindowsSessionId}")));
        _server = new AutomationPipeServer(() => Interlocked.Exchange(ref _firstServer, null)
            ?? InstancePipeFactory.CreateServer(pipeName, firstPipeInstance: false), _engine);
        _runTask = RunServerAsync();
        logger.LogInformation("BetterGI CLI 已启用：Session {SessionId}，管道 {PipeName}", context.WindowsSessionId, pipeName);
        return Task.CompletedTask;
    }

    private async Task RunServerAsync()
    {
        try { await _server!.RunAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogError(exception, "BetterGI CLI 管道服务意外停止"); }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownLock) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _lifetime.Cancel();
        if (_engine is not null) await _engine.ShutdownAsync().ConfigureAwait(false);
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        if (_runTask is not null) await _runTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
        _firstServer?.Dispose();
        _lifetime.Dispose();
    }
}
