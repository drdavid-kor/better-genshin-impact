using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using BetterGenshinImpact.Cli;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Automation.Tests;

/// <summary>真实 CLI 子进程 → 真实命名管道 → 持久化引擎；执行器受控，不操作游戏。</summary>
public class CliPipeIntegrationTests
{
    [Theory]
    [InlineData(false, 0, "succeeded")]
    [InlineData(true, 5, "failed")]
    public async Task CliProcess_ShouldDiscoverSubmitWaitAndReplayOriginalResult(bool fail, int exitCode, string state)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new PipeFixture(fail);
        var catalog = await fixture.Cli("tasks", "list");
        Assert.Equal(0, catalog.Code);
        Assert.Equal("日常 采集", catalog.Json["data"]!["tasks"]![0]!.Value<string>("name"));
        var id = Guid.NewGuid().ToString("D");
        var run = await fixture.Cli("run", "group", "日常 采集", "--request-id", id, "--wait");
        Assert.Equal(exitCode, run.Code);
        Assert.Equal(state, run.Json["data"]!.Value<string>("state"));
        var replay = await fixture.Cli("run", "group", "日常 采集", "--request-id", id, "--wait");
        Assert.Equal(exitCode, replay.Code);
        Assert.Equal(id, replay.Json["data"]!.Value<string>("runId"));
        Assert.Equal(1, fixture.Backend.Executions);
    }

    [Fact]
    public async Task CliProcess_ShouldLeaveTimedOutJobRunningThenCancelOnlyThatJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var fixture = new PipeFixture(false, waitForCancellation: true);
        var id = Guid.NewGuid().ToString("D");
        Assert.Equal(0, (await fixture.Cli("run", "group", "日常 采集", "--request-id", id)).Code);
        var wait = await fixture.Cli("runs", "wait", id, "--timeout", "0.15");
        Assert.Equal(7, wait.Code);
        var current = await fixture.Cli("runs", "get", id);
        Assert.Equal("running", current.Json["data"]!.Value<string>("state"));
        Assert.Equal(0, (await fixture.Cli("runs", "cancel", id)).Code);
        var stopped = await fixture.Cli("runs", "wait", id);
        Assert.Equal(6, stopped.Code);
        Assert.Equal("cancelled", stopped.Json["data"]!.Value<string>("state"));
        Assert.Equal(1, fixture.Backend.Executions);
    }

    private sealed class TestBackend(bool fail, bool waitForCancellation) : IAutomationBackend
    {
        public int Executions;
        public Task<AutomationBackendStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new AutomationBackendStatus { Ready = true });
        public Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AutomationTaskDefinition>>(
            [new() { Kind = "group", Name = "日常 采集" }]);
        public async Task<AutomationExecutionResult> ExecuteAsync(AutomationTaskRequest request, Action<AutomationLogEntry> report, CancellationToken ct)
        {
            Interlocked.Increment(ref Executions);
            report(new() { Message = "这是受控测试执行器，不操作游戏。" });
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            if (fail) throw new InvalidOperationException("受控任务失败");
            return new() { Succeeded = true };
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class PipeFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "BetterGI-CLI-Integration", Guid.NewGuid().ToString("N"));
        // 独立合成 Session 仅作为测试管道名，不碰本机真实 BetterGI 端点。
        private readonly int _session = Random.Shared.Next(1_000_000_000, int.MaxValue);
        private readonly AutomationEngine _engine;
        private readonly AutomationPipeServer _server;
        internal TestBackend Backend { get; }

        public PipeFixture(bool fail, bool waitForCancellation = false)
        {
            Backend = new TestBackend(fail, waitForCancellation);
            _engine = new AutomationEngine(Backend, new AutomationIdentity
            { InstanceId = Guid.NewGuid().ToString("N"), InstallationPath = _directory, WindowsSessionId = _session, ProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow }, _directory);
            using var identity = WindowsIdentity.GetCurrent();
            var pipeName = AutomationPipeNames.ForUserSession(identity.User!.Value, _session);
            _server = new AutomationPipeServer(() => new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), _engine);
            _ = _server.RunAsync();
        }

        public async Task<(int Code, JObject Json)> Cli(params string[] arguments)
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe");
            var info = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add("--runtimeconfig");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BetterGenshinImpact.Automation.Tests.runtimeconfig.json"));
            info.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--install");
            info.ArgumentList.Add(_directory);
            info.ArgumentList.Add("--session");
            info.ArgumentList.Add(_session.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var raw = await stdout;
            Assert.True(string.IsNullOrWhiteSpace(await stderr), await stderr);
            var json = AutomationJson.Deserialize<JObject>(raw);
            Assert.Equal(process.ExitCode, json.Value<int>("exitCode"));
            return (process.ExitCode, json);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _engine.DisposeAsync();
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI-CLI-Integration")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_directory, true);
        }
    }
}
