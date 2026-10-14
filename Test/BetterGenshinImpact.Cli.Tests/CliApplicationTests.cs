using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Cli;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Cli.Tests;

public class CliApplicationTests
{
    private static readonly string InstallationPath = Path.GetFullPath("test-installation");
    private static readonly Guid RunId = Guid.Parse("37b03785-7049-408b-9ceb-541a8070f75d");

    [Theory]
    [InlineData("help")]
    [InlineData("schema")]
    public async Task OfflineCommands_ShouldWorkWithoutWindowsOrHost(string command)
    {
        var client = new CliApplication(
            (_, _, _) => throw new InvalidOperationException("Must not connect"),
            isWindows: () => false);
        var (code, json) = await Run(client, command);
        Assert.Equal(0, code);
        Assert.Equal(0, json["exitCode"]);
        Assert.NotEmpty((JArray)json["data"]!["commands"]!);
    }

    [Theory]
    [InlineData("run script test --request-id 37b03785-7049-408b-9ceb-541a8070f75d")]
    [InlineData("tasks list")]
    [InlineData("runs get 37b03785-7049-408b-9ceb-541a8070f75d")]
    public async Task MutationAndDiscovery_ShouldRequireExplicitInstallation(string arguments)
    {
        var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
        var (code, json) = await Run(client, arguments.Split(' '));
        Assert.Equal(CliExitCodes.Usage, code);
        Assert.Equal("invalid_arguments", json["error"]!["code"]);
    }

    [Theory]
    [InlineData("--timeout", "NaN")]
    [InlineData("--timeout", "Infinity")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "86401")]
    [InlineData("--session", "-1")]
    [InlineData("--session", "invalid")]
    public async Task Parser_ShouldRejectInvalidGlobalOptions(string option, string value)
    {
        var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
        var (code, _) = await Run(client, "doctor", option, value);
        Assert.Equal(CliExitCodes.Usage, code);
    }

    [Theory]
    [InlineData("doctor --wait")]
    [InlineData("doctor --parameters invalid")]
    [InlineData("doctor --unknown value")]
    [InlineData("doctor --timeout 1 --timeout 2")]
    [InlineData("doctor excess")]
    public async Task Parser_ShouldRejectUnexpectedOrDuplicateOptions(string arguments)
    {
        var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
        var (code, _) = await Run(client, arguments.Split(' '));
        Assert.Equal(CliExitCodes.Usage, code);
    }

    [Fact]
    public async Task HostCommands_ShouldRejectUnsupportedPlatform()
    {
        var client = new CliApplication(isWindows: () => false);
        var (code, json) = await Run(client, "doctor");
        Assert.Equal(CliExitCodes.Unavailable, code);
        Assert.Equal("unsupported_platform", json["error"]!["code"]);
    }

    [Fact]
    public async Task Run_ShouldPreserveExactUnicodeNameAndIdempotencyId()
    {
        AutomationRequest? observed = null;
        var client = CreateClient((request, _) =>
        {
            observed = request;
            return Task.FromResult(Response(request, "accepted"));
        });
        var (code, json) = await Run(client, "run", "group", "  每日 路线  ", "--request-id", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(0, code);
        Assert.Equal("runs.start", observed!.Operation);
        Assert.Equal("  每日 路线  ", observed.Data!["name"]);
        Assert.Equal(RunId, observed.Data["requestId"]!.ToObject<Guid>());
        Assert.Equal(RunId, observed.RequestId);
        Assert.Equal(InstallationPath, observed.InstallationPath);
        Assert.Equal("accepted", json["data"]!["state"]);
    }

    [Fact]
    public async Task Run_ShouldLoadParametersFromJsonObjectFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "{\"party\":\"中文 队伍\",\"count\":2}");
            AutomationRequest? observed = null;
            var client = CreateClient((request, _) =>
            {
                observed = request;
                return Task.FromResult(Response(request, "accepted"));
            });
            var (code, _) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--parameters", file, "--install", InstallationPath);
            Assert.Equal(0, code);
            Assert.Equal("中文 队伍", observed!.Data!["parameters"]!["party"]);
            Assert.Equal(2, observed.Data["parameters"]!["count"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Run_ShouldRejectParameterArrays()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "[]");
            var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
            var (code, _) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--parameters", file, "--install", InstallationPath);
            Assert.Equal(CliExitCodes.Usage, code);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Parameters_ShouldPreserveIsoStringsExactly()
    {
        var file = Path.GetTempFileName();
        try
        {
            const string value = "2026-09-29T03:04:05.0000000+08:00";
            await File.WriteAllTextAsync(file, "{\"timestamp\":\"" + value + "\"}");
            AutomationRequest? observed = null;
            var client = CreateClient((request, _) =>
            {
                observed = request;
                return Task.FromResult(Response(request, "accepted"));
            });
            var (code, _) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--parameters", file, "--install", InstallationPath);
            Assert.Equal(0, code);
            var parameter = observed!.Data!["parameters"]!["timestamp"]!;
            Assert.Equal(JTokenType.String, parameter.Type);
            Assert.Equal(value, parameter.Value<string>());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("{\"key\":1,\"key\":2}")]
    [InlineData("{\"nested\":{\"key\":1,\"key\":2}}")]
    [InlineData("{\"key\":1} {}")]
    public async Task Parameters_ShouldRejectAmbiguousJsonBeforeSending(string json)
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, json);
            var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
            var (code, result) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--parameters", file, "--install", InstallationPath);
            Assert.Equal(CliExitCodes.Usage, code);
            Assert.Equal("invalid_arguments", result["error"]!["code"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Parameters_ShouldRejectExcessiveDepthBeforeSending()
    {
        var file = Path.GetTempFileName();
        try
        {
            var json = string.Concat(Enumerable.Repeat("{\"child\":", 33)) + "0" + new string('}', 33);
            await File.WriteAllTextAsync(file, json);
            var client = CreateClient((_, _) => throw new InvalidOperationException("Must not connect"));
            var (code, result) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--parameters", file, "--install", InstallationPath);
            Assert.Equal(CliExitCodes.Usage, code);
            Assert.Equal("invalid_arguments", result["error"]!["code"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task EnvironmentInstallation_ShouldBeUsedAndExplicitValueShouldOverrideIt()
    {
        var observed = new List<string?>();
        var client = new CliApplication((_, request, _) =>
        {
            observed.Add(request.InstallationPath);
            return Task.FromResult(Response(request));
        }, () => true, () => InstallationPath, () => 4);
        Assert.Equal(0, (await Run(client, "tasks", "list")).Code);
        var other = Path.GetFullPath("other-installation");
        Assert.Equal(0, (await Run(client, "tasks", "list", "--install", other)).Code);
        Assert.Equal(new[] { InstallationPath, other }, observed);
    }

    [Theory]
    [InlineData("succeeded", CliExitCodes.Success)]
    [InlineData("failed", CliExitCodes.TaskFailed)]
    [InlineData("cancelled", CliExitCodes.TaskCancelled)]
    [InlineData("interrupted", CliExitCodes.Interrupted)]
    public async Task Wait_ShouldReturnExitCodeForActualTerminalState(string state, int expected)
    {
        var client = CreateClient((request, _) => Task.FromResult(Response(request, state)));
        var (code, json) = await Run(client, "runs", "wait", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(expected, code);
        Assert.Equal(expected, json["exitCode"]);
        Assert.Equal(state, json["data"]!["state"]);
    }

    [Fact]
    public async Task Get_ShouldReturnSuccessWhenReadingFailedRun()
    {
        var client = CreateClient((request, _) => Task.FromResult(Response(request, "failed")));
        var (code, json) = await Run(client, "runs", "get", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(0, code);
        Assert.Equal("failed", json["data"]!["state"]);
    }

    [Fact]
    public async Task RunWait_ShouldPinInstanceAndOnlyPollAfterOneSubmission()
    {
        var requests = new List<AutomationRequest>();
        var client = CreateClient((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Response(request, requests.Count == 1 ? "running" : "succeeded"));
        });
        var (code, json) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--wait", "--install", InstallationPath);
        Assert.Equal(0, code);
        Assert.Equal(new[] { "runs.start", "runs.get" }, requests.Select(x => x.Operation));
        Assert.Equal("test-instance", requests[1].InstanceId);
        Assert.Equal(RunId, requests[1].Data!["runId"]!.ToObject<Guid>());
        Assert.Equal("succeeded", json["data"]!["state"]);
    }

    [Fact]
    public async Task WaitTimeout_ShouldKeepRunIdAndNeverSendCancel()
    {
        var operations = new List<string>();
        var client = CreateClient((request, _) =>
        {
            operations.Add(request.Operation);
            return Task.FromResult(Response(request, "running"));
        });
        var (code, json) = await Run(client, "runs", "wait", RunId.ToString(), "--timeout", "0.05", "--install", InstallationPath);
        Assert.Equal(CliExitCodes.Timeout, code);
        Assert.Equal(RunId, json["data"]!["runId"]!.ToObject<Guid>());
        Assert.Equal("running", json["data"]!["state"]);
        Assert.Equal("wait_timeout", json["error"]!["code"]);
        Assert.DoesNotContain("runs.cancel", operations);
    }

    [Fact]
    public async Task LostSubmissionResponse_ShouldExposeKnownRunIdAndNeverRetry()
    {
        var calls = 0;
        var client = CreateClient((_, _) =>
        {
            calls++;
            throw new TimeoutException("connection timeout");
        });
        var (code, json) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(CliExitCodes.Timeout, code);
        Assert.Equal(RunId, json["data"]!["runId"]!.ToObject<Guid>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClientCancellation_ShouldNotCancelServerTask()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var client = CreateClient((_, _) =>
        {
            calls++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var output = new StringWriter();
        var code = await client.RunAsync(["runs", "wait", RunId.ToString(), "--install", InstallationPath], output, cts.Token);
        var json = JObject.Parse(output.ToString());
        Assert.Equal(CliExitCodes.Interrupted, code);
        Assert.Equal("client_cancelled", json["error"]!["code"]);
        Assert.Equal(RunId, json["data"]!["runId"]!.ToObject<Guid>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RequestRejection_ShouldRemainMachineReadable()
    {
        var client = CreateClient((request, _) => Task.FromResult(new AutomationResponse
        {
            RequestId = request.RequestId,
            Ok = false,
            Error = new AutomationError("busy", "An existing task owns the game.")
        }));
        var (code, json) = await Run(client, "doctor");
        Assert.Equal(CliExitCodes.Rejected, code);
        Assert.Equal("busy", json["error"]!["code"]);
    }

    [Fact]
    public async Task InvalidResponseId_ShouldNeverReportSuccess()
    {
        var client = CreateClient((_, _) => Task.FromResult(new AutomationResponse { RequestId = Guid.NewGuid(), Ok = true }));
        var (code, json) = await Run(client, "doctor");
        Assert.Equal(CliExitCodes.Unavailable, code);
        Assert.Equal("invalid_response", json["error"]!["code"]);
    }

    [Fact]
    public async Task Wait_ShouldRejectReplacedInstance()
    {
        var count = 0;
        var client = CreateClient((request, _) => Task.FromResult(Response(request,
            ++count == 1 ? "running" : "succeeded", count == 1 ? "test-instance" : "new-instance")));
        var (code, json) = await Run(client, "runs", "wait", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(CliExitCodes.Interrupted, code);
        Assert.Equal("instance_changed", json["error"]!["code"]);
    }

    [Fact]
    public async Task RunResponse_ShouldRejectAnUnrelatedRun()
    {
        var client = CreateClient((request, _) => Task.FromResult(Response(request, "succeeded") with
        {
            Data = new JObject { ["runId"] = Guid.NewGuid(), ["state"] = "succeeded" }
        }));
        var (code, json) = await Run(client, "run", "script", "test", "--request-id", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(CliExitCodes.Unavailable, code);
        Assert.Equal("invalid_response", json["error"]!["code"]);
        Assert.Equal(RunId, json["data"]!["runId"]!.ToObject<Guid>());
    }

    [Fact]
    public async Task Wait_ShouldRejectUnknownStates()
    {
        var client = CreateClient((request, _) => Task.FromResult(Response(request, "completed")));
        var (code, json) = await Run(client, "runs", "wait", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(CliExitCodes.Unavailable, code);
        Assert.Equal("invalid_response", json["error"]!["code"]);
    }

    [Fact]
    public async Task Cancel_ShouldRequestOnlyCancellationWithoutClaimingCompletion()
    {
        var calls = 0;
        var client = CreateClient((request, _) =>
        {
            calls++;
            Assert.Equal("runs.cancel", request.Operation);
            return Task.FromResult(Response(request, "cancelRequested"));
        });
        var (code, json) = await Run(client, "runs", "cancel", RunId.ToString(), "--install", InstallationPath);
        Assert.Equal(0, code);
        Assert.Equal("cancelRequested", json["data"]!["state"]);
        Assert.Equal(1, calls);
    }

    private static CliApplication CreateClient(Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> send) =>
        new(async (_, request, token) => AutomationJson.Deserialize<AutomationResponse>(
            AutomationJson.Serialize(await send(request, token))), () => true, () => null, () => 4);

    private static AutomationResponse Response(AutomationRequest request, string? state = null, string instanceId = "test-instance") => new()
    {
        RequestId = request.RequestId,
        Ok = true,
        Instance = new AutomationIdentity
        {
            InstanceId = instanceId,
            InstallationPath = InstallationPath,
            ProcessId = 123,
            WindowsSessionId = 4,
            StartedAt = DateTimeOffset.UtcNow
        },
        Data = state is null ? new JObject() : new JObject { ["runId"] = RunId, ["state"] = state }
    };

    private static async Task<(int Code, JObject Json)> Run(CliApplication client, params string[] args)
    {
        using var output = new StringWriter();
        var code = await client.RunAsync(args, output);
        var json = JObject.Parse(output.ToString());
        Assert.Equal(code, json["exitCode"]!.Value<int>());
        return (code, json);
    }
}
