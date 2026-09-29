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

/// <summary>通过启动参数或界面显式启用；服务和执行历史由当前进程持有。</summary>
internal sealed class AutomationHostedService : IHostedService, IAutomationBridgeService, IAsyncDisposable
{
    private readonly IAutomationBackend _backend;
    private readonly AutomationIdentity _identity;
    private readonly string _historyDirectory;
    private readonly Func<bool, NamedPipeServerStream> _createServer;
    private readonly ILogger<AutomationHostedService> _logger;
    private readonly bool _autoEnable;
    private readonly bool _canEnable;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _shutdownLock = new();
    private AutomationEngine? _engine;
    private Listener? _listener;
    private Task? _stopTask;
    private Task? _disposeTask;
    private int _shutdownRequested;
    private string? _listenerError;

    public AutomationHostedService(InstanceService instanceService, ILogger<AutomationHostedService> logger)
        : this(new BetterGiAutomationBackend(Global.StartUpPath), new AutomationIdentity
        {
            InstanceId = $"{instanceService.Context.ProcessId}:{instanceService.Context.StartedAt.ToUnixTimeMilliseconds()}",
            InstallationPath = Path.GetFullPath(Global.StartUpPath),
            ProcessId = instanceService.Context.ProcessId,
            WindowsSessionId = instanceService.Context.WindowsSessionId,
            StartedAt = instanceService.Context.StartedAt
        }, Global.Absolute(Path.Combine("User", "Automation", $"session-{instanceService.Context.WindowsSessionId}")),
            first => CreateProductionServer(instanceService.Context.WindowsSessionId, first), logger,
            CommandLineOptions.Instance.EnableCli, instanceService.Context.InstanceType != BetterGiInstanceType.WebView)
    {
    }

    // 测试使用真实管道和替代后端，不创建 App、窗口或游戏任务。
    internal AutomationHostedService(IAutomationBackend backend, AutomationIdentity identity, string historyDirectory,
        Func<bool, NamedPipeServerStream> createServer, ILogger<AutomationHostedService> logger,
        bool autoEnable = false, bool canEnable = true)
    {
        _backend = backend;
        _identity = identity;
        _historyDirectory = historyDirectory;
        _createServer = createServer;
        _logger = logger;
        _autoEnable = autoEnable;
        _canEnable = canEnable;
    }

    public bool IsEnabled => Volatile.Read(ref _shutdownRequested) == 0
        && Volatile.Read(ref _listener)?.RunTask is { IsCompleted: false };

    public Task StartAsync(CancellationToken cancellationToken) => _autoEnable && _canEnable
        ? EnableAsync(cancellationToken)
        : Task.CompletedTask;

    public async Task EnableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfStopping();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            if (!_canEnable) throw new InvalidOperationException("WebView 辅助进程不能启用 CLI 接口。");
            if (IsEnabled) return;

            // 监听意外停止时只重建传输，保留引擎和已接收任务，不借重试取消任务。
            if (_listener is { } previous)
            {
                Volatile.Write(ref _listener, null);
                await DisposeListenerAsync(previous).ConfigureAwait(false);
            }

            Listener? next = null;
            AutomationEngine? newEngine = null;
            try
            {
                next = new Listener(_createServer(true));
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfStopping();
                var engine = _engine ?? (newEngine = new AutomationEngine(_backend, _identity, _historyDirectory));
                var listener = next;
                listener.Server = new AutomationPipeServer(
                    () => Interlocked.Exchange(ref listener.FirstServer, null) ?? _createServer(false), engine);
                listener.RunTask = listener.Server.RunAsync(listener.Lifetime.Token);
                // 管道已在同步部分保留并开始等待连接，首轮启动异常必须回传给 GUI。
                if (listener.RunTask.IsCompleted) await listener.RunTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfStopping();
                _engine = engine;
                Volatile.Write(ref _listenerError, null);
                Volatile.Write(ref _listener, listener);
                _ = ObserveListenerAsync(listener);
                _logger.LogInformation("BetterGI CLI 已启用：Session {SessionId}", _identity.WindowsSessionId);
            }
            catch
            {
                if (next is not null) await DisposeListenerAsync(next).ConfigureAwait(false);
                if (newEngine is not null) await newEngine.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = Volatile.Read(ref _engine);
        if (engine is null)
        {
            return new AutomationBackendStatus
            {
                Ready = false,
                Reason = Volatile.Read(ref _shutdownRequested) != 0 ? "服务正在关闭。" : "CLI 接口尚未启用。"
            };
        }

        // 不持有生命周期锁调用后端：后端可能切换至调用该方法的 WPF Dispatcher。
        var response = await engine.HandleAsync(new AutomationRequest { Operation = "doctor" }, cancellationToken)
            .ConfigureAwait(false);
        var status = response.Data?.ToObject<AutomationBackendStatus>() ?? new AutomationBackendStatus
        {
            Ready = false, Reason = response.Error?.Message ?? "无法读取 CLI 接口状态。"
        };
        if (Volatile.Read(ref _shutdownRequested) != 0)
            return status with { Ready = false, Reason = "服务正在关闭。" };
        if (!IsEnabled)
            return status with { Ready = false, Reason = Volatile.Read(ref _listenerError) ?? "CLI 监听已停止，可再次启用。" };
        return status;
    }

    private async Task ObserveListenerAsync(Listener listener)
    {
        try
        {
            await listener.RunTask!.ConfigureAwait(false);
            if (Volatile.Read(ref _shutdownRequested) == 0 && ReferenceEquals(Volatile.Read(ref _listener), listener))
                Volatile.Write(ref _listenerError, "CLI 监听已停止，可再次启用。");
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _shutdownRequested) == 0 && ReferenceEquals(Volatile.Read(ref _listener), listener))
            {
                Volatile.Write(ref _listenerError, "CLI 监听发生错误：" + exception.GetBaseException().Message);
                _logger.LogError(exception, "BetterGI CLI 管道服务意外停止");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        lock (_shutdownLock) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var listener = _listener;
            Volatile.Write(ref _listener, null);
            if (listener is not null) await DisposeListenerAsync(listener).ConfigureAwait(false);
            if (_engine is not null) await _engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task DisposeListenerAsync(Listener listener)
    {
        // 即使监听任务已经失败，也要释放所有资源，使相同管道可再次启用。
        listener.Lifetime.Cancel();
        try
        {
            if (listener.Server is not null) await listener.Server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "清理已停止的 CLI 监听");
        }
        finally
        {
            listener.FirstServer?.Dispose();
            listener.Lifetime.Dispose();
        }
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
            throw new InvalidOperationException("BetterGI 正在退出，不能启用 CLI 接口。");
    }

    public ValueTask DisposeAsync()
    {
        lock (_shutdownLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        // 关闭开始前已进入的 EnableAsync 仍可能正在等待或释放此锁。
        // 未使用 AvailableWaitHandle，没有需要主动释放的操作系统句柄；由 GC 回收以免竞态。
    }

    private static NamedPipeServerStream CreateProductionServer(int sessionId, bool firstPipeInstance)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前 Windows 用户 SID。");
        return InstancePipeFactory.CreateServer(AutomationPipeNames.ForUserSession(sid, sessionId), firstPipeInstance);
    }

    private sealed class Listener(NamedPipeServerStream firstServer)
    {
        internal NamedPipeServerStream? FirstServer = firstServer;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal AutomationPipeServer? Server { get; set; }
        internal Task? RunTask { get; set; }
    }
}
