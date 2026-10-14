using System.IO.Pipes;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Automation;

/// <summary>每条连接处理一个 RPC；已接受运行不依赖连接。限制并发和空闲连接等待。</summary>
public sealed class AutomationPipeServer(Func<NamedPipeServerStream> createServer, AutomationEngine engine) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(16);
    private readonly HashSet<Task> _clients = [];
    private readonly object _sync = new();
    private Task? _loop;
    private int _disposed;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) throw new InvalidOperationException("管道服务已启动。");
        return _loop = AcceptAsync(cancellationToken);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = createServer();
                    await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                    var connection = pipe;
                    var task = ServeAsync(connection, linked.Token);
                    pipe = null;
                    lock (_sync) _clients.Add(task);
                    _ = task.ContinueWith(completed => { lock (_sync) _clients.Remove(completed); }, TaskScheduler.Default);
                }
                catch
                {
                    pipe?.Dispose();
                    _slots.Release();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            linked.Cancel();
            Task[] clients;
            lock (_sync) clients = _clients.ToArray();
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                AutomationRequest request;
                // 15 秒只限制尚未交齐请求的连接；处理和写回受服务生命周期控制。
                using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    request = await AutomationPipeProtocol.ReadAsync<AutomationRequest>(pipe, readDeadline.Token).ConfigureAwait(false);
                }
                var response = await engine.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                await AutomationPipeProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or JsonException or ArgumentException)
            {
                // 损坏消息、断线与读取超时只终止当前连接，不终止执行中的任务或监听服务。
            }
            finally { _slots.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
        _slots.Dispose();
    }
}
