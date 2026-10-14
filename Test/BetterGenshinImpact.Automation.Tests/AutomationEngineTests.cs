using System.IO.Pipes;
using BetterGenshinImpact.Automation;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Automation.Tests;

public sealed class AutomationEngineTests
{
    [Fact]
    public async Task DoctorAndCatalog_ShouldNotExecuteTasks()
    {
        await using var harness = new Harness();
        var doctor = await harness.Send("doctor");
        var list = await harness.Send("tasks.list");
        Assert.True(doctor.Ok);
        Assert.True(doctor.Data!.Value<bool>("ready"));
        Assert.Equal("每日 采集", list.Data!["tasks"]![0]!.Value<string>("name"));
        Assert.Equal(0, harness.Backend.Executions);
    }

    [Fact]
    public async Task Start_ShouldPersistBeforeAcknowledgingAndFinishWithResult()
    {
        await using var harness = new Harness();
        var id = Guid.NewGuid();
        var response = await harness.Start(id);
        Assert.True(response.Ok);
        Assert.Equal(id, response.Data!["runId"]!.ToObject<Guid>());
        Assert.True(File.Exists(Path.Combine(harness.Directory, id + ".json")));
        var run = await harness.Terminal(id);
        Assert.Equal(AutomationRunStates.Succeeded, run.State);
        Assert.Equal("流程已结束", run.Result!.Value<string>("summary"));
    }

    [Fact]
    public async Task ConcurrentSameRequest_ShouldExecuteExactlyOnce()
    {
        await using var harness = new Harness();
        var id = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => harness.Start(id)));
        Assert.All(responses, response => Assert.True(response.Ok, response.Error?.Message));
        await harness.Terminal(id);
        Assert.Equal(1, harness.Backend.Executions);
        var repeated = await harness.Start(id);
        Assert.Equal("succeeded", repeated.Data!.Value<string>("state"));
        Assert.Equal(1, harness.Backend.Executions);
    }

    [Fact]
    public async Task SameRequestWithChangedParameters_ShouldRejectWithoutReexecution()
    {
        await using var harness = new Harness();
        var id = Guid.NewGuid();
        Assert.True((await harness.Start(id, new JObject { ["count"] = 1 })).Ok);
        await harness.Terminal(id);
        var response = await harness.Start(id, new JObject { ["count"] = 2 });
        Assert.Equal("idempotency_conflict", response.Error!.Code);
        Assert.Equal(1, harness.Backend.Executions);
    }

    [Theory]
    [InlineData("other", "每日 采集", "invalid_request")]
    [InlineData("script", "../每日", "invalid_request")]
    [InlineData("script", "不存在", "task_not_found")]
    public async Task InvalidTask_ShouldNeverExecute(string kind, string name, string code)
    {
        await using var harness = new Harness();
        var response = await harness.Send("runs.start", new { requestId = Guid.NewGuid(), kind, name });
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(0, harness.Backend.Executions);
    }

    [Fact]
    public async Task WrongInstallationOrInstance_ShouldRejectBeforeBackendExecution()
    {
        await using var harness = new Harness();
        var response = await harness.Engine.HandleAsync(new AutomationRequest
        {
            Operation = "runs.start", InstallationPath = Path.Combine(harness.Directory, "other"),
            Data = AutomationJson.ToObject(new { requestId = Guid.NewGuid(), kind = "script", name = "每日 采集" })
        });
        Assert.Equal("installation_mismatch", response.Error!.Code);
        response = await harness.Engine.HandleAsync(new AutomationRequest
        { Operation = "doctor", InstanceId = "different-boot" });
        Assert.Equal("instance_changed", response.Error!.Code);
        Assert.Equal(0, harness.Backend.Executions);
    }

    [Fact]
    public async Task BusyOrUnavailableBackend_ShouldNotAcceptRun()
    {
        await using var harness = new Harness();
        harness.Backend.Status = new() { Ready = true, Busy = true };
        Assert.Equal("busy", (await harness.Start(Guid.NewGuid())).Error!.Code);
        harness.Backend.Status = new() { Ready = false, Reason = "尚未启动" };
        Assert.Equal("not_ready", (await harness.Start(Guid.NewGuid())).Error!.Code);
        Assert.Empty(System.IO.Directory.GetFiles(harness.Directory, "*.json"));
    }

    [Fact]
    public async Task FailedExecutor_ShouldNeverBecomeSucceeded()
    {
        await using var harness = new Harness();
        harness.Backend.Execute = (_, _, _) => throw new AutomationException("recognition_failed", "没有识别到目标");
        var id = Guid.NewGuid();
        await harness.Start(id);
        var run = await harness.Terminal(id);
        Assert.Equal("failed", run.State);
        Assert.Equal("recognition_failed", run.Error!.Code);
    }

    [Fact]
    public async Task ReportedFailure_ShouldRemainFailureAfterNormalTaskReturn()
    {
        await using var harness = new Harness();
        harness.Backend.Execute = (_, _, _) => Task.FromResult(new AutomationExecutionResult
        { ErrorCode = "reported_error", ErrorMessage = "内部捕获了异常" });
        var id = Guid.NewGuid();
        await harness.Start(id);
        Assert.Equal("failed", (await harness.Terminal(id)).State);
    }

    [Fact]
    public async Task Cancellation_ShouldHoldBusyUntilCleanupAndNeverCancelAnotherRun()
    {
        await using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Backend.Execute = async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); }
            await cleaned.Task;
            return new AutomationExecutionResult { Succeeded = true };
        };
        var id = Guid.NewGuid();
        await harness.Start(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var response = await harness.Send("runs.cancel", new { runId = id });
        Assert.Equal("cancelRequested", response.Data!.Value<string>("state"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("busy", (await harness.Start(Guid.NewGuid())).Error!.Code);
        Assert.Equal("run_not_found", (await harness.Send("runs.cancel", new { runId = Guid.NewGuid() })).Error!.Code);
        cleaned.SetResult();
        Assert.Equal("cancelled", (await harness.Terminal(id)).State);
        Assert.Equal("cancelled", (await harness.Send("runs.cancel", new { runId = id })).Data!.Value<string>("state"));
    }

    [Fact]
    public async Task Logs_ShouldBeBoundedOrderedAndReturnedWithTerminalResult()
    {
        await using var harness = new Harness();
        harness.Backend.Execute = (_, report, _) =>
        {
            for (var index = 0; index < 150; index++) report(new AutomationLogEntry { Message = new string('测', 2000) });
            return Task.FromResult(new AutomationExecutionResult { Succeeded = true });
        };
        var id = Guid.NewGuid();
        await harness.Start(id);
        var run = await harness.Terminal(id);
        Assert.Equal(100, run.Logs.Length);
        Assert.Equal(51, run.Logs[0].Sequence);
        Assert.Equal(150, run.Logs[^1].Sequence);
        Assert.All(run.Logs, item => Assert.Equal(1000, item.Message.Length));
    }

    [Fact]
    public async Task Restart_ShouldPreserveCompletedIdempotency()
    {
        await using var harness = new Harness();
        var id = Guid.NewGuid();
        await harness.Start(id);
        await harness.Terminal(id);
        await harness.Engine.ShutdownAsync();
        var secondBackend = new FakeBackend();
        await using var restarted = new AutomationEngine(secondBackend, harness.Identity with { InstanceId = "boot-2" }, harness.Directory);
        var response = await restarted.HandleAsync(harness.Request("runs.start", new { requestId = id, kind = "script", name = "每日 采集" }));
        Assert.True(response.Ok);
        Assert.Equal("succeeded", response.Data!.Value<string>("state"));
        Assert.Equal("boot-1", response.Data!.Value<string>("instanceId"));
        Assert.Equal(0, secondBackend.Executions);
    }

    [Fact]
    public async Task Restart_ShouldMarkUnfinishedRunInterruptedWithoutReplaying()
    {
        await using var harness = new Harness();
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(harness.Directory, id + ".json"), AutomationJson.Serialize(new
        {
            request = new AutomationTaskRequest { RequestId = id, Kind = "script", Name = "每日 采集" },
            run = new AutomationRun { RunId = id, RequestId = id, InstanceId = "old", Kind = "script", Name = "每日 采集", State = "running" }
        }));
        await using var restarted = new AutomationEngine(harness.Backend, harness.Identity, harness.Directory);
        var run = restarted.GetRun(id);
        Assert.Equal("interrupted", run.State);
        Assert.Equal("service_restarted", run.Error!.Code);
        Assert.Equal(0, harness.Backend.Executions);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"request\":null,\"run\":null}")]
    public async Task CorruptHistory_ShouldPreventNewExecution(string json)
    {
        await using var harness = new Harness();
        File.WriteAllText(Path.Combine(harness.Directory, Guid.NewGuid() + ".json"), json);
        await using var restarted = new AutomationEngine(harness.Backend, harness.Identity, harness.Directory);
        var response = await restarted.HandleAsync(harness.Request("runs.start", new { requestId = Guid.NewGuid(), kind = "script", name = "每日 采集" }));
        Assert.Equal("history_unavailable", response.Error!.Code);
        Assert.Equal(0, harness.Backend.Executions);
    }

    [Fact]
    public async Task Shutdown_ShouldRecordInterruptionAfterCooperativeCleanup()
    {
        await using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Backend.Execute = async (_, _, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new() { Succeeded = true }; };
        var id = Guid.NewGuid();
        await harness.Start(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await harness.Engine.ShutdownAsync();
        Assert.Equal("interrupted", harness.Engine.GetRun(id).State);
        Assert.Equal("shutting_down", (await harness.Start(Guid.NewGuid())).Error!.Code);
    }

    [Fact]
    public async Task RealPipe_ShouldDiscoverExecuteQueryAndSurviveClientDisconnect()
    {
        await using var harness = new Harness();
        var pipeName = "bgi-test-" + Guid.NewGuid().ToString("N");
        await using var server = new AutomationPipeServer(() => new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), harness.Engine);
        var listening = server.RunAsync();
        var client = new AutomationPipeClient(pipeName, TimeSpan.FromSeconds(3));
        Assert.True((await client.SendAsync(harness.Request("doctor"))).Ok);
        await using (var malformed = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await malformed.ConnectAsync(3000);
            await malformed.WriteAsync(new byte[4]); // 长度为零的无效帧只能关闭当前连接。
            var closed = new byte[1];
            using var frameDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await malformed.ReadAsync(closed, frameDeadline.Token));
        }
        Assert.True((await client.SendAsync(harness.Request("doctor"))).Ok);
        var id = Guid.NewGuid();
        await using (var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await connection.ConnectAsync(3000);
            await AutomationPipeProtocol.WriteAsync(connection, harness.Request("runs.start", new { requestId = id, kind = "script", name = "每日 采集" }), CancellationToken.None);
            // 不读响应就断开，模拟调用方退出；执行不能被连接取消。
        }
        var deadline = DateTime.UtcNow.AddSeconds(4);
        AutomationResponse response;
        do
        {
            response = await client.SendAsync(harness.Request("runs.get", new { runId = id }));
            if (response.Ok && response.Data!.Value<string>("state") == "succeeded") break;
            await Task.Delay(10);
        } while (DateTime.UtcNow < deadline);
        Assert.True(response.Ok, response.Error?.Message);
        Assert.Equal("succeeded", response.Data!.Value<string>("state"));
        Assert.Equal(1, harness.Backend.Executions);
    }

    private sealed class FakeBackend : IAutomationBackend
    {
        public int Executions;
        public AutomationBackendStatus Status { get; set; } = new() { Ready = true };
        public Func<AutomationTaskRequest, Action<AutomationLogEntry>, CancellationToken, Task<AutomationExecutionResult>> Execute { get; set; }
            = (_, _, _) => Task.FromResult(new AutomationExecutionResult { Succeeded = true, Data = new JObject { ["summary"] = "流程已结束" } });
        public Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
        public Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutomationTaskDefinition>>([new() { Kind = "script", Name = "每日 采集" }]);
        public Task<AutomationExecutionResult> ExecuteAsync(AutomationTaskRequest request, Action<AutomationLogEntry> report, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Executions); return Execute(request, report, cancellationToken); }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "BetterGI-Automation-Tests", Guid.NewGuid().ToString("N"));
        public FakeBackend Backend { get; } = new();
        public AutomationIdentity Identity { get; }
        public AutomationEngine Engine { get; }
        public Harness()
        {
            Identity = new() { InstanceId = "boot-1", InstallationPath = Directory, ProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow };
            Engine = new(Backend, Identity, Directory);
        }
        public AutomationRequest Request(string operation, object? data = null) => new()
        { Operation = operation, InstallationPath = Directory, Data = data is null ? null : AutomationJson.ToObject(data) };
        public Task<AutomationResponse> Send(string operation, object? data = null) => Engine.HandleAsync(Request(operation, data));
        public Task<AutomationResponse> Start(Guid id, JObject? parameters = null) => Send("runs.start", new { requestId = id, kind = "script", name = "每日 采集", parameters });
        public async Task<AutomationRun> Terminal(Guid id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var run = Engine.GetRun(id);
                if (AutomationRunStates.IsTerminal(run.State)) return run;
                await Task.Delay(10);
            } while (DateTime.UtcNow < deadline);
            throw new TimeoutException("测试任务未在期限内结束。");
        }
        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            // 唯一生成的临时测试目录；不触及任何用户安装数据。
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI-Automation-Tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(Directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
