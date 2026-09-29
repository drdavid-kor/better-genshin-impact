using System.IO.Pipes;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Service.Automation;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Automation;

public class AutomationBridgeLifecycleTests
{
    [Fact]
    public async Task GuiEnable_AfterNormalStartup_IsConcurrentAndIdempotent()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Service.StartAsync(CancellationToken.None);
        Assert.False(fixture.Service.IsEnabled);
        Assert.False((await fixture.Service.GetStatusAsync()).Ready);
        Assert.Equal(0, fixture.FirstReservations);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Service.EnableAsync()));
        Assert.True(fixture.Service.IsEnabled);
        Assert.Equal(1, fixture.FirstReservations);
        Assert.True((await fixture.Doctor()).Ok);
        Assert.True((await fixture.Service.GetStatusAsync()).Ready);
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [Fact]
    public async Task ExplicitStartupFlag_StillEnablesTheSameBridge()
    {
        await using var fixture = new BridgeFixture(autoEnable: true);
        await fixture.Service.StartAsync(CancellationToken.None);
        await fixture.Service.EnableAsync();
        Assert.True(fixture.Service.IsEnabled);
        Assert.Equal(1, fixture.FirstReservations);
        var response = await fixture.Doctor();
        Assert.Equal(fixture.Identity.InstanceId, response.Instance!.InstanceId);
    }

    [Fact]
    public async Task StartupFailure_AfterPipeReservation_ReleasesPipeAndCanRetry()
    {
        await using var fixture = new BridgeFixture();
        File.WriteAllText(fixture.HistoryDirectory, "该文件阻止创建历史目录");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.EnableAsync());
        Assert.False(fixture.Service.IsEnabled);
        using (fixture.ReservePipe()) { }

        File.Delete(fixture.HistoryDirectory);
        await fixture.Service.EnableAsync();
        Assert.True((await fixture.Doctor()).Ok);
        Assert.True(fixture.Service.IsEnabled);
    }

    [Fact]
    public async Task CancelledEnable_CleansPartialResources_AndDoesNotPoisonRetry()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new BridgeFixture(afterFirstReservation: () => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.EnableAsync(cancellation.Token));
        Assert.False(fixture.Service.IsEnabled);
        using (fixture.ReservePipe()) { }

        await fixture.Service.EnableAsync();
        Assert.True((await fixture.Doctor()).Ok);
    }

    [Fact]
    public async Task ButtonCancellation_AfterEnable_DoesNotOwnListenerLifetime()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new BridgeFixture();
        await fixture.Service.EnableAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.True(fixture.Service.IsEnabled);
        Assert.True((await fixture.Doctor()).Ok);
    }

    [Fact]
    public async Task ShutdownDuringEnable_PreventsPublication_AndReleasesReservedPipe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var fixture = new BridgeFixture(afterFirstReservation: () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试未释放启动阶段。");
        });
        var enable = Task.Run(() => fixture.Service.EnableAsync());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Service.StopAsync(CancellationToken.None);
        release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => enable);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Service.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnableAsync());
        using (fixture.ReservePipe()) { }
    }

    [Fact]
    public async Task HostStopAndDispose_AreConcurrentAndIdempotent()
    {
        await using var fixture = new BridgeFixture();
        await fixture.Service.EnableAsync();
        await Task.WhenAll(fixture.Service.StopAsync(CancellationToken.None),
            fixture.Service.StopAsync(CancellationToken.None), fixture.Service.DisposeAsync().AsTask(),
            fixture.Service.DisposeAsync().AsTask());
        Assert.False(fixture.Service.IsEnabled);
        Assert.False((await fixture.Service.GetStatusAsync()).Ready);
        using (fixture.ReservePipe()) { }
    }

    [Fact]
    public async Task Status_IncludesHistoryFailureInsteadOfOnlyBackendReadiness()
    {
        await using var fixture = new BridgeFixture();
        Directory.CreateDirectory(fixture.HistoryDirectory);
        File.WriteAllText(Path.Combine(fixture.HistoryDirectory, Guid.NewGuid().ToString("D") + ".json"), "{broken");
        await fixture.Service.EnableAsync();
        Assert.True(fixture.Service.IsEnabled);
        var status = await fixture.Service.GetStatusAsync();
        Assert.False(status.Ready);
        Assert.False(string.IsNullOrEmpty(status.Reason));
        Assert.False((await fixture.Doctor()).Data!.Value<bool>("ready"));
    }

    private sealed class TestBackend : IAutomationBackend
    {
        internal int Executions;
        public Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AutomationBackendStatus { Ready = true });
        public Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationTaskDefinition>>([]);
        public Task<AutomationExecutionResult> ExecuteAsync(AutomationTaskRequest request, Action<AutomationLogEntry> report,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Executions);
            throw new InvalidOperationException("生命周期测试不得启动任何任务。");
        }
    }

    private sealed class BridgeFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Lifecycle.Tests", Guid.NewGuid().ToString("N"));
        private readonly string _pipeName = "BetterGI.Automation.Lifecycle.Tests." + Guid.NewGuid().ToString("N");
        internal string HistoryDirectory => Path.Combine(_root, "history");
        internal AutomationHostedService Service { get; }
        internal AutomationIdentity Identity { get; }
        internal TestBackend Backend { get; } = new();
        internal int FirstReservations;

        internal BridgeFixture(bool autoEnable = false, Action? afterFirstReservation = null)
        {
            Directory.CreateDirectory(_root);
            Identity = new AutomationIdentity
            {
                InstanceId = Guid.NewGuid().ToString("N"), InstallationPath = _root,
                ProcessId = Environment.ProcessId, WindowsSessionId = 424242, StartedAt = DateTimeOffset.UtcNow
            };
            Service = new AutomationHostedService(Backend, Identity, HistoryDirectory, first =>
            {
                var pipe = InstancePipeFactory.CreateServer(_pipeName, first);
                try
                {
                    if (first)
                    {
                        Interlocked.Increment(ref FirstReservations);
                        afterFirstReservation?.Invoke();
                    }
                    return pipe;
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
            }, NullLogger<AutomationHostedService>.Instance, autoEnable);
        }

        internal NamedPipeServerStream ReservePipe() => InstancePipeFactory.CreateServer(_pipeName, true);

        internal Task<AutomationResponse> Doctor() => new AutomationPipeClient(_pipeName, TimeSpan.FromSeconds(5))
            .SendAsync(new AutomationRequest { Operation = "doctor", InstallationPath = _root });

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            var intendedDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Lifecycle.Tests"));
            var resolvedRoot = Path.GetFullPath(_root);
            if (!resolvedRoot.StartsWith(intendedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(resolvedRoot), intendedDirectory, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolvedRoot), "N", out _))
                throw new InvalidOperationException("拒绝删除测试临时目录之外的路径。");
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
