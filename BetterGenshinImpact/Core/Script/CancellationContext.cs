using System;
using BetterGenshinImpact.Model;
using System.Threading;
using BetterGenshinImpact.Service.Automation;

namespace BetterGenshinImpact.Core.Script;

public class CancellationContext : Singleton<CancellationContext>
{
    private readonly object _sync = new();
    public CancellationTokenSource Cts { get; private set; } = new();
    public bool IsManualStop { get; private set; }

    public bool IsCancellationRequested
    {
        get
        {
            lock (_sync) 
            {
                return !disposed && Cts.IsCancellationRequested; 
            }
        }
    }

    private bool disposed;
    private AutomationExecutionScope? _automationScope;
    private int _preparingTasks;

    internal bool IsAutomationOwned
    {
        get { lock (_sync) return _automationScope is not null; }
    }

    internal bool IsPreparing
    {
        get { lock (_sync) return _preparingTasks > 0; }
    }

    /// <summary>在旧入口尚未取得 TaskSemaphore 的游戏启动阶段，阻止 CLI 接管取消源。</summary>
    internal IDisposable? TryReservePreparation()
    {
        lock (_sync)
        {
            if (_automationScope is not null)
            {
                if (!ReferenceEquals(_automationScope, AutomationExecutionScope.Current))
                    return null;
                return new PreparationLease(null);
            }
            _preparingTasks++;
            return new PreparationLease(this);
        }
    }

    internal void BeginAutomation(AutomationExecutionScope scope)
    {
        lock (_sync)
        {
            if (_automationScope is not null || _preparingTasks > 0)
            {
                throw new BetterGenshinImpact.Automation.AutomationException("busy", "已有任务正在准备或执行。");
            }
            if (!disposed) Cts.Dispose();
            _automationScope = scope;
            Cts = scope.CancellationSource;
            IsManualStop = false;
            disposed = false;
        }
    }

    internal void EndAutomation(AutomationExecutionScope scope)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_automationScope, scope))
            {
                _automationScope = null;
                disposed = true;
            }
        }
    }

    public void Set()
    {
        lock (_sync)
        {
            if (_automationScope is not null)
            {
                if (!ReferenceEquals(_automationScope, AutomationExecutionScope.Current))
                {
                    throw new InvalidOperationException("CLI 作业正在执行，请先等待或停止该作业。");
                }
                return;
            }
            Cts = new CancellationTokenSource();
            IsManualStop = false;
            disposed = false;
        }
    }

    public void ManualCancel()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }

            IsManualStop = true;
            cts = Cts;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发 Clear 可能已释放 CTS，这里视为已取消/已清理。
        }
    }

    public void Cancel()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (disposed)
            {
                return;
            }

            cts = Cts;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发 Clear 可能已释放 CTS，这里视为已取消/已清理。
        }
    }

    public void Clear()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            // 嵌套 TaskRunner 和无关 GUI 清理都不能释放仍由 CLI 作业持有的取消源。
            if (_automationScope is not null) return;
            if (disposed)
            {
                return;
            }

            cts = Cts;
            disposed = true;
        }

        cts.Dispose();
    }

    private sealed class PreparationLease(CancellationContext? context) : IDisposable
    {
        private CancellationContext? _context = context;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _context, null);
            if (owner is null) return;
            lock (owner._sync) owner._preparingTasks--;
        }
    }
}
