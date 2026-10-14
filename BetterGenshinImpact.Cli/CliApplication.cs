using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using BetterGenshinImpact.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace BetterGenshinImpact.Cli;

public static class CliExitCodes
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int Unavailable = 3;
    public const int Rejected = 4;
    public const int TaskFailed = 5;
    public const int TaskCancelled = 6;
    public const int Timeout = 7;
    public const int Interrupted = 8;
}

public sealed record CliConnectionOptions(int SessionId, TimeSpan Timeout);

/// <summary>可测试的 CLI 边界；所有命令只输出一个 JSON 文档。</summary>
public sealed class CliApplication
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly Func<CliConnectionOptions, AutomationRequest, CancellationToken, Task<AutomationResponse>> _send;
    private readonly Func<bool> _isWindows;
    private readonly Func<string?> _installationEnvironment;
    private readonly Func<int> _sessionId;

    public CliApplication(
        Func<CliConnectionOptions, AutomationRequest, CancellationToken, Task<AutomationResponse>>? send = null,
        Func<bool>? isWindows = null,
        Func<string?>? installationEnvironment = null,
        Func<int>? sessionId = null)
    {
        _send = send ?? SendAsync;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
        _installationEnvironment = installationEnvironment ?? (() => Environment.GetEnvironmentVariable("BGI_INSTALL_DIR"));
        _sessionId = sessionId ?? (() => Process.GetCurrentProcess().SessionId);
    }

    public async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        JObject result;
        var exitCode = CliExitCodes.Success;
        ParsedCommand? command = null;
        try
        {
            command = Parse(args);
            if (command.Command is "help" or "schema")
            {
                result = Success(Schema());
            }
            else
            {
                if (!_isWindows())
                {
                    throw new CliException("unsupported_platform", "连接 BetterGI 需要 Windows。help 和 schema 可离线使用。", CliExitCodes.Unavailable);
                }

                var request = CreateRequest(command);
                var connection = new CliConnectionOptions(command.SessionId ?? _sessionId(), command.Timeout);
                var timer = Stopwatch.StartNew();
                var response = await _send(connection, request, cancellationToken).ConfigureAwait(false);
                ValidateResponse(response, request);
                result = SerializeResponse(response);
                if (!response.Ok)
                {
                    exitCode = CliExitCodes.Rejected;
                }
                else if (command.Wait)
                {
                    (result, exitCode) = await WaitAsync(command, connection, response, timer, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (CliException exception)
        {
            exitCode = exception.ExitCode;
            result = Failure(exception.Code, exception.Message, command);
        }
        catch (JsonException exception)
        {
            exitCode = CliExitCodes.Unavailable;
            result = Failure("invalid_response", exception.Message, command);
        }
        catch (AutomationException exception)
        {
            exitCode = CliExitCodes.Unavailable;
            result = Failure(exception.Code, exception.Message, command);
        }
        catch (OperationCanceledException)
        {
            exitCode = cancellationToken.IsCancellationRequested ? CliExitCodes.Interrupted : CliExitCodes.Timeout;
            result = Failure(cancellationToken.IsCancellationRequested ? "client_cancelled" : "timeout",
                "客户端等待已停止；这不会取消 BetterGI 任务。请使用 runId 查询状态。", command);
        }
        catch (TimeoutException exception)
        {
            exitCode = CliExitCodes.Timeout;
            result = Failure("timeout", exception.Message + "；任务状态未知，请使用原 runId 查询，不要更换 requestId 重试。", command);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            exitCode = CliExitCodes.Unavailable;
            result = Failure("connection_error", exception.Message, command);
        }
        catch (Exception exception)
        {
            exitCode = CliExitCodes.Unavailable;
            result = Failure("client_error", exception.GetBaseException().Message, command);
        }

        result["exitCode"] = exitCode;
        await output.WriteLineAsync(result.ToString(Formatting.None)).ConfigureAwait(false);
        return exitCode;
    }

    private async Task<(JObject, int)> WaitAsync(
        ParsedCommand command,
        CliConnectionOptions connection,
        AutomationResponse initial,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        var response = initial;
        var runId = command.RunId ?? response.Data?["runId"]?.ToObject<Guid?>()
            ?? throw new CliException("invalid_response", "服务未返回 runId。", CliExitCodes.Unavailable);
        var instanceId = response.Instance?.InstanceId
            ?? throw new CliException("invalid_response", "服务未返回实例身份。", CliExitCodes.Unavailable);

        while (true)
        {
            var state = response.Data?["state"]?.Value<string>();
            switch (state)
            {
                case "succeeded": return (SerializeResponse(response), CliExitCodes.Success);
                case "failed": return (SerializeResponse(response), CliExitCodes.TaskFailed);
                case "cancelled": return (SerializeResponse(response), CliExitCodes.TaskCancelled);
                case "interrupted": return (SerializeResponse(response), CliExitCodes.Interrupted);
                case "accepted":
                case "running":
                case "cancelRequested": break;
                default: throw new CliException("invalid_response", $"未知任务状态：{state ?? "null"}", CliExitCodes.Unavailable);
            }

            var remaining = command.Timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                var result = SerializeResponse(response);
                result["ok"] = false;
                result["error"] = new JObject
                {
                    ["code"] = "wait_timeout",
                    ["message"] = "等待超时，任务仍由 BetterGI 管理；未发送取消。使用 runs get/wait 查询原 runId。"
                };
                return (result, CliExitCodes.Timeout);
            }

            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            remaining = command.Timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }

            var request = new AutomationRequest
            {
                RequestId = Guid.NewGuid(),
                Operation = "runs.get",
                InstallationPath = command.InstallationPath,
                InstanceId = instanceId,
                Data = new JObject { ["runId"] = runId }
            };
            response = await _send(connection with { Timeout = remaining }, request, cancellationToken).ConfigureAwait(false);
            ValidateResponse(response, request);
            if (!response.Ok)
            {
                return (SerializeResponse(response), CliExitCodes.Rejected);
            }
        }
    }

    private ParsedCommand Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var wait = false;
        var literal = false;
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (literal)
            {
                positional.Add(value);
                continue;
            }
            if (value == "--")
            {
                literal = true;
                continue;
            }
            if (value is "--help" or "-h")
            {
                return new ParsedCommand { Command = "help" };
            }
            if (value == "--wait")
            {
                if (wait) throw Usage("--wait 不可重复。");
                wait = true;
                continue;
            }
            if (value.StartsWith("--", StringComparison.Ordinal))
            {
                if (value is not ("--install" or "--session" or "--timeout" or "--request-id" or "--parameters" or "--kind"))
                    throw Usage($"未知选项：{value}");
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw Usage($"选项 {value} 缺少值。");
                if (!options.TryAdd(value, args[index])) throw Usage($"选项 {value} 不可重复。");
                continue;
            }
            positional.Add(value);
        }

        if (positional.Count == 0) positional.Add("help");
        var command = positional[0];
        if (command is "help" or "schema")
        {
            if (positional.Count != 1 || wait || options.Count > 0) throw Usage($"{command} 不接受附加参数。");
            return new ParsedCommand { Command = command };
        }

        var parsed = new ParsedCommand
        {
            Command = command,
            InstallationPath = options.GetValueOrDefault("--install") ?? _installationEnvironment(),
            Wait = wait
        };
        if (!string.IsNullOrWhiteSpace(parsed.InstallationPath))
        {
            try
            {
                parsed.InstallationPath = Path.GetFullPath(parsed.InstallationPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw Usage("安装目录路径无效：" + exception.Message);
            }
        }
        else if (command != "doctor")
        {
            throw Usage("请通过 --install 或 BGI_INSTALL_DIR 指定目标 BetterGI 安装目录。");
        }

        if (options.TryGetValue("--timeout", out var timeout))
        {
            if (!double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || !double.IsFinite(seconds) || seconds < 0.05 || seconds > 86400)
                throw Usage("--timeout 必须是 0.05 到 86400 之间的秒数。");
            parsed.Timeout = TimeSpan.FromSeconds(seconds);
        }
        if (options.TryGetValue("--session", out var session))
        {
            if (!int.TryParse(session, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId) || sessionId < 0)
                throw Usage("--session 必须是非负整数 Windows Session ID。");
            parsed.SessionId = sessionId;
        }

        switch (command)
        {
            case "doctor" when positional.Count == 1:
                parsed.Operation = "doctor";
                EnsureAllowed(options, wait);
                break;
            case "tasks" when positional.Count >= 2:
                if (positional[1] == "list" && positional.Count == 2)
                {
                    parsed.Operation = "tasks.list";
                    parsed.Data = new JObject();
                    if (options.TryGetValue("--kind", out var kind)) parsed.Data["kind"] = kind;
                    EnsureAllowed(options, wait, "--kind");
                }
                else if (positional[1] == "show" && positional.Count == 4)
                {
                    parsed.Operation = "tasks.show";
                    parsed.Data = TaskData(positional[2], positional[3]);
                    EnsureAllowed(options, wait);
                }
                else throw Usage("用法：tasks list [--kind KIND] 或 tasks show KIND NAME");
                break;
            case "run" when positional.Count == 3:
                EnsureAllowed(options, false, "--request-id", "--parameters");
                if (!options.TryGetValue("--request-id", out var requestId)) throw Usage("run 必须指定 --request-id GUID；runId 等于此 GUID。");
                parsed.RunId = ParseGuid(requestId);
                parsed.Operation = "runs.start";
                parsed.Data = TaskData(positional[1], positional[2]);
                parsed.Data["requestId"] = parsed.RunId;
                if (options.TryGetValue("--parameters", out var parameters))
                {
                    try
                    {
                        var file = new FileInfo(parameters);
                        if (file.Length > 512 * 1024) throw Usage("参数文件不能超过 512 KiB。");
                        parsed.Data["parameters"] = AutomationJson.Deserialize<JObject>(File.ReadAllText(parameters));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw Usage("无法读取参数 JSON 文件：" + exception.Message);
                    }
                    catch (JsonException exception)
                    {
                        throw Usage("参数文件必须包含一个 JSON 对象：" + exception.Message);
                    }
                }
                break;
            case "runs" when positional.Count == 3:
                EnsureAllowed(options, wait);
                parsed.Operation = positional[1] switch
                {
                    "get" or "wait" => "runs.get",
                    "cancel" => "runs.cancel",
                    _ => throw Usage("用法：runs get|wait|cancel GUID")
                };
                parsed.RunId = ParseGuid(positional[2]);
                parsed.Data = new JObject { ["runId"] = parsed.RunId };
                parsed.Wait = positional[1] == "wait";
                break;
            default: throw Usage("未知命令或参数数量错误；运行 bettergi help 查看契约。");
        }
        return parsed;
    }

    private static JObject TaskData(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name)) throw Usage("任务 kind 和 name 不能为空。");
        return new JObject { ["kind"] = kind, ["name"] = name };
    }

    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw Usage("运行 ID 必须是非空 GUID。");

    private static void EnsureAllowed(Dictionary<string, string> options, bool wait, params string[] allowed)
    {
        if (wait) throw Usage("--wait 仅适用于 run；现有任务使用 runs wait GUID。");
        foreach (var key in options.Keys)
        {
            if (key is not ("--install" or "--session" or "--timeout") && !allowed.Contains(key))
                throw Usage($"此命令不接受 {key}。");
        }
    }

    private static AutomationRequest CreateRequest(ParsedCommand command) => new()
    {
        RequestId = command.Operation == "runs.start" ? command.RunId!.Value : Guid.NewGuid(),
        Operation = command.Operation,
        InstallationPath = command.InstallationPath,
        Data = command.Data
    };

    private static async Task<AutomationResponse> SendAsync(CliConnectionOptions connection, AutomationRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("无法获取当前 Windows 用户 SID。");
        var client = new AutomationPipeClient(AutomationPipeNames.ForUserSession(sid, connection.SessionId), connection.Timeout);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateResponse(AutomationResponse response, AutomationRequest request)
    {
        if (response.ApiVersion != 1 || response.RequestId != request.RequestId)
            throw new CliException("invalid_response", "响应协议版本或请求 ID 不匹配。", CliExitCodes.Unavailable);
        if (request.InstanceId is not null && response.Instance?.InstanceId != request.InstanceId)
            throw new CliException("instance_changed", "BetterGI 实例已变更；旧运行不能转交给新实例。", CliExitCodes.Interrupted);
        if (response.Ok && request.Operation.StartsWith("runs.", StringComparison.Ordinal))
        {
            var expected = request.Data?[request.Operation == "runs.start" ? "requestId" : "runId"]?.ToObject<Guid?>();
            if (expected is null || response.Data?["runId"]?.ToObject<Guid?>() != expected)
                throw new CliException("invalid_response", "服务返回的运行 ID 与请求不匹配。", CliExitCodes.Unavailable);
        }
    }

    private static JObject SerializeResponse(AutomationResponse response) => JObject.FromObject(response, JsonSerializer.Create(JsonSettings));

    private static JObject Success(JObject data) => new() { ["apiVersion"] = 1, ["ok"] = true, ["data"] = data };

    private static JObject Failure(string code, string message, ParsedCommand? command) => new()
    {
        ["apiVersion"] = 1,
        ["ok"] = false,
        ["error"] = new JObject { ["code"] = code, ["message"] = message },
        ["data"] = command?.RunId is { } runId ? new JObject { ["runId"] = runId } : null
    };

    public static JObject Schema() => new()
    {
        ["name"] = "bettergi",
        ["apiVersion"] = 1,
        ["commands"] = new JArray(
            "help", "schema", "doctor", "tasks list [--kind KIND]", "tasks show KIND NAME",
            "run KIND NAME --request-id GUID [--parameters JSON_FILE] [--wait]",
            "runs get GUID", "runs wait GUID", "runs cancel GUID"),
        ["globalOptions"] = new JObject
        {
            ["--install"] = "BetterGI 安装目录；除 help/schema/doctor 外必填，也可用 BGI_INSTALL_DIR。",
            ["--session"] = "Windows Session ID，默认当前会话。",
            ["--timeout"] = "连接/请求或整个等待的秒数，默认 30，范围 0.05..86400。"
        },
        ["semantics"] = new JArray(
            "所有 stdout 输出为一个 JSON 文档；exitCode 与进程退出码一致。",
            "runId 等于 --request-id GUID；在提交前持久记录该 GUID。",
            "同一 GUID 和相同参数重试只查询既有运行；不同参数返回冲突。",
            "提交响应丢失时，使用 runs get 原 GUID；不得换新 GUID 重跑。",
            "普通 run/get/cancel 的退出码 0 表示请求成功，任务成功必须检查 state=succeeded。",
            "run --wait 和 runs wait 按任务终态返回退出码。",
            "等待超时或 Ctrl+C 只停止客户端等待，不取消任务。",
            "runs cancel 请求取消；继续 runs wait/get 确认终态。",
            "parameters 必须是 UTF-8 JSON 对象文件；可先 tasks show 查看参数。",
            "任务名精确匹配；含空格的名称须由调用 shell 正确引用。可用 -- 分隔以 -- 开头的名称。",
            "此客户端不启动 BetterGI、不提权、不更改系统设置。"),
        ["runStates"] = new JArray("accepted", "running", "cancelRequested", "succeeded", "failed", "cancelled", "interrupted"),
        ["exitCodes"] = new JObject
        {
            ["0"] = "请求成功，或等待任务成功",
            ["2"] = "命令或参数无效",
            ["3"] = "实例不可用、平台不支持、通信/协议错误",
            ["4"] = "服务拒绝请求",
            ["5"] = "等待的任务失败",
            ["6"] = "等待的任务已取消",
            ["7"] = "连接/请求/等待超时，任务结果未知",
            ["8"] = "客户端等待被中断、实例变更、或任务 interrupted"
        }
    };

    private static CliException Usage(string message) => new("invalid_arguments", message, CliExitCodes.Usage);

    private sealed class CliException(string code, string message, int exitCode) : Exception(message)
    {
        public string Code { get; } = code;
        public int ExitCode { get; } = exitCode;
    }

    private sealed class ParsedCommand
    {
        public string Command { get; init; } = "";
        public string Operation { get; set; } = "";
        public string? InstallationPath { get; set; }
        public int? SessionId { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool Wait { get; set; }
        public Guid? RunId { get; set; }
        public JObject? Data { get; set; }
    }
}
