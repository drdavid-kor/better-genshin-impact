using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>
/// 一个 CLI 作业持有整个执行期间的任务锁和取消源；旧任务的 Set/Clear 不得撤销取消。
/// 作用域只沿该作业的异步调用链传播，不授予无关 GUI 命令重入权限。
/// </summary>
internal sealed class AutomationExecutionScope : IDisposable
{
    private static readonly AsyncLocal<AutomationExecutionScope?> Ambient = new();
    private readonly object _sync = new();
    private readonly CancellationContext _context;
    private readonly Action<AutomationLogEntry> _report;
    private readonly List<string> _failures = [];
    private int _disposed;
    private int _executedCount;
    private int _skippedCount;
    private int _runnerActive;

    internal AutomationExecutionScope(CancellationContext context, CancellationToken cancellationToken,
        Action<AutomationLogEntry> report)
    {
        _context = context;
        _report = report;
        CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            context.BeginAutomation(this);
        }
        catch
        {
            CancellationSource.Dispose();
            throw;
        }
    }

    internal static AutomationExecutionScope? Current =>
        Ambient.Value is { } scope && Volatile.Read(ref scope._disposed) == 0 ? scope : null;

    internal CancellationTokenSource CancellationSource { get; }
    internal bool IsCancellationRequested => CancellationSource.IsCancellationRequested;
    internal int ExecutedCount => Volatile.Read(ref _executedCount);
    internal int SkippedCount => Volatile.Read(ref _skippedCount);
    internal string[] Failures { get { lock (_sync) return _failures.ToArray(); } }

    internal IDisposable Bind()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Binding(previous);
    }

    internal void RecordExecution() => Interlocked.Increment(ref _executedCount);

    internal void RecordSkipped(string reason)
    {
        Interlocked.Increment(ref _skippedCount);
        _report(new AutomationLogEntry { Message = reason });
    }

    // 整个作业持有全局锁，但其继承上下文的并行子任务仍不得同时操作游戏。
    internal bool TryEnterRunner() => Interlocked.CompareExchange(ref _runnerActive, 1, 0) == 0;
    internal void ExitRunner() => Interlocked.Exchange(ref _runnerActive, 0);

    internal void RecordFailure(string message)
    {
        lock (_sync)
        {
            if (_failures.Count < 100 && !_failures.Contains(message, StringComparer.Ordinal))
            {
                _failures.Add(message);
            }
        }
    }

    internal void RecordException(Exception exception)
    {
        // 战斗结束等控制流不是失败；已取消作业的终态仍由取消源决定。
        if (exception is NormalEndException { IsFailure: false })
        {
            return;
        }
        if (exception is OperationCanceledException && IsCancellationRequested)
        {
            return;
        }
        RecordFailure(exception.GetBaseException().Message);
    }

    internal void Report(LogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var message = logEvent.RenderMessage();
        if (logEvent.Level >= LogEventLevel.Error) RecordFailure(message);
        _report(new AutomationLogEntry
        {
            Timestamp = logEvent.Timestamp,
            Level = logEvent.Level.ToString().ToLowerInvariant(),
            Message = message
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _context.EndAutomation(this);
        CancellationSource.Dispose();
    }

    private sealed class Binding(AutomationExecutionScope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

internal sealed class AutomationLogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent) => AutomationExecutionScope.Current?.Report(logEvent);
}
