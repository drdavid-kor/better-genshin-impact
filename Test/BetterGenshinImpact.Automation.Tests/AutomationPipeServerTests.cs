using System.Buffers.Binary;
using System.IO.Pipes;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Automation.Tests;

/// <summary>真实独立测试管道和受控后端，不连接游戏或本机 BetterGI 端点。</summary>
public class AutomationPipeServerTests
{
    [Theory]
    [InlineData("doctor")]
    [InlineData("tasks.list")]
    [InlineData("runs.start")]
    public async Task RequestTakingLongerThanReadDeadline_StillReturnsJson(string operation)
    {
        // 使用生产环境的 15 秒期限，覆盖处理令牌和随后写回的令牌。
        await using var fixture = new PipeFixture(operation, TimeSpan.FromSeconds(16));
        var response = await new AutomationPipeClient(fixture.PipeName, TimeSpan.FromSeconds(40))
            .SendAsync(fixture.Request(operation));

        Assert.True(response.Ok, response.Error?.Message);
        Assert.NotNull(response.Data);
        Assert.False(fixture.Backend.WasCancelled);
        if (operation == "runs.start")
            Assert.NotNull(response.Data!["runId"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleOrIncompleteRequest_IsClosedWithoutStoppingListener(bool incomplete)
    {
        await using var fixture = new PipeFixture();
        await using var idle = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await idle.ConnectAsync(timeout.Token);
        if (incomplete)
        {
            // 声明 64 字节消息，却只交付一个字节；期限必须覆盖整条读取。
            await idle.WriteAsync(new byte[] { 64, 0, 0, 0, (byte)'{' }, timeout.Token);
            await idle.FlushAsync(timeout.Token);
        }

        Assert.Equal(0, await idle.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(0, fixture.Backend.Calls);
        var response = await new AutomationPipeClient(fixture.PipeName, TimeSpan.FromSeconds(5))
            .SendAsync(fixture.Request("doctor"));
        Assert.True(response.Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrListenerCancellation_CancelsPendingHandler(bool cancelListener)
    {
        await using var fixture = new PipeFixture("doctor", Timeout.InfiniteTimeSpan);
        var request = new AutomationPipeClient(fixture.PipeName, TimeSpan.FromSeconds(10))
            .SendAsync(fixture.Request("doctor"));
        await fixture.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (cancelListener) fixture.Stop.Cancel();
        else await fixture.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Listener.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(fixture.Backend.WasCancelled);
        await Assert.ThrowsAnyAsync<IOException>(async () => { await request; });
    }

    [Fact]
    public async Task Shutdown_CancelsIdleRead()
    {
        await using var fixture = new PipeFixture();
        await using var idle = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await idle.ConnectAsync(timeout.Token);

        await fixture.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, await idle.ReadAsync(new byte[1], timeout.Token));
        Assert.Equal(0, fixture.Backend.Calls);
    }

    [Fact]
    public async Task Shutdown_CancelsBlockedResponseWrite()
    {
        await using var fixture = new PipeFixture();
        fixture.Backend.LargeCatalog = true;
        await using var client = new NamedPipeClientStream(".", fixture.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await AutomationPipeProtocol.WriteAsync(client, fixture.Request("tasks.list"), timeout.Token);
        var header = new byte[4];
        await client.ReadExactlyAsync(header, timeout.Token);
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(header) > 64 * 1024);
        // 不读取正文，让写回等待客户端，关闭服务必须仍能取消它。
        await fixture.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TestBackend(string? slowOperation, TimeSpan delay) : IAutomationBackend
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal bool WasCancelled;
        internal bool LargeCatalog;

        public async Task<AutomationBackendStatus> GetStatusAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (slowOperation is "doctor" or "runs.start") await DelayAsync(ct);
            return new() { Ready = true };
        }

        public async Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (slowOperation == "tasks.list") await DelayAsync(ct);
            return LargeCatalog
                ? Enumerable.Range(0, 2000).Select(i => new AutomationTaskDefinition
                    { Kind = "group", Name = i + new string('x', 128) }).ToArray()
                : [new() { Kind = "group", Name = "受控任务" }];
        }

        private async Task DelayAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
        }

        public Task<AutomationExecutionResult> ExecuteAsync(AutomationTaskRequest request,
            Action<AutomationLogEntry> report, CancellationToken ct) => Task.FromResult(new AutomationExecutionResult { Succeeded = true });
    }

    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "BetterGI-PipeServer-Tests", Guid.NewGuid().ToString("N"));
        private readonly AutomationEngine _engine;
        internal readonly string PipeName = "BetterGI-PipeServer-Test-" + Guid.NewGuid().ToString("N");
        internal readonly CancellationTokenSource Stop = new();
        internal TestBackend Backend { get; }
        internal AutomationPipeServer Server { get; }
        internal Task Listener { get; }

        internal PipeFixture(string? slowOperation = null, TimeSpan delay = default)
        {
            Backend = new TestBackend(slowOperation, delay);
            _engine = new AutomationEngine(Backend, new AutomationIdentity
            {
                InstanceId = Guid.NewGuid().ToString("N"), InstallationPath = _directory,
                ProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow
            }, _directory);
            Server = new AutomationPipeServer(() => new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024, 1024), _engine);
            Listener = Server.RunAsync(Stop.Token);
        }

        internal AutomationRequest Request(string operation) => new()
        {
            RequestId = Guid.NewGuid(), Operation = operation, InstallationPath = _directory,
            Data = operation == "runs.start" ? new JObject
            {
                ["requestId"] = Guid.NewGuid().ToString("D"), ["kind"] = "group", ["name"] = "受控任务"
            } : null
        };

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await _engine.DisposeAsync();
            Stop.Dispose();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI-PipeServer-Tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
