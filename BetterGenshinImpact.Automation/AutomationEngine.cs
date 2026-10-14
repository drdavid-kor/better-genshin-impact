using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Automation;

/// <summary>任务接受与终态先持久化再回包。客户端连接的生命周期不拥有已接受的执行。</summary>
public sealed class AutomationEngine : IAsyncDisposable
{
    private const int MaxLogs = 100;
    private const int MaxHistory = 4096;
    private readonly object _sync = new();
    private readonly IAutomationBackend _backend;
    private readonly AutomationIdentity _identity;
    private readonly string _historyDirectory;
    private readonly Dictionary<Guid, StoredRun> _runs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _activeCancellation;
    private Guid? _activeRunId;
    private Task? _worker;
    private Task? _shutdownTask;
    private bool _stopping;
    private int _disposed;
    private string? _historyError;

    public AutomationEngine(IAutomationBackend backend, AutomationIdentity identity, string historyDirectory)
    {
        _backend = backend;
        _identity = identity;
        _historyDirectory = Path.GetFullPath(historyDirectory);
        Directory.CreateDirectory(_historyDirectory);
        LoadHistory();
    }

    public async Task<AutomationResponse> HandleAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateTarget(request);
            object data = request.Operation switch
            {
                "doctor" => await GetDoctorAsync(cancellationToken).ConfigureAwait(false),
                "tasks.list" => new { tasks = await ListTasksAsync(request.Data, cancellationToken).ConfigureAwait(false) },
                "tasks.show" => new { task = await FindTaskAsync(RequiredString(request.Data, "kind"), RequiredString(request.Data, "name"), cancellationToken).ConfigureAwait(false) },
                "runs.start" => await StartAsync(ParseTaskRequest(request.Data), cancellationToken).ConfigureAwait(false),
                "runs.get" => GetRun(RequiredGuid(request.Data, "runId")),
                "runs.cancel" => CancelRun(RequiredGuid(request.Data, "runId")),
                _ => throw new AutomationException("unsupported_operation", "不支持的自动化操作。")
            };
            return new AutomationResponse { RequestId = request.RequestId, Ok = true, Instance = _identity, Data = AutomationJson.ToObject(data) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            var code = error switch
            {
                AutomationException known => known.Code,
                JsonException or ArgumentException or FormatException => "invalid_request",
                IOException or UnauthorizedAccessException => "storage_error",
                _ => "internal_error"
            };
            return new AutomationResponse { RequestId = request.RequestId, Instance = _identity, Error = new AutomationError(code, error.Message) };
        }
    }

    private void ValidateTarget(AutomationRequest request)
    {
        if (request.ApiVersion != 1) throw new AutomationException("protocol_mismatch", "仅支持自动化协议版本 1。");
        if (request.RequestId == Guid.Empty) throw new AutomationException("invalid_request", "请求 ID 不能为空。");
        if (request.InstanceId is not null && request.InstanceId != _identity.InstanceId)
            throw new AutomationException("instance_changed", "BetterGI 实例已变化；请重新诊断并查询原运行，不能假定任务未执行。");
        if (string.IsNullOrWhiteSpace(request.InstallationPath))
        {
            if (request.Operation != "doctor") throw new AutomationException("installation_required", "请指定 BetterGI 安装目录。");
        }
        else if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallationPath)).Equals(
                     Path.TrimEndingDirectorySeparator(Path.GetFullPath(_identity.InstallationPath)), StringComparison.OrdinalIgnoreCase))
        {
            throw new AutomationException("installation_mismatch", "当前 Session 的 BetterGI 不属于指定安装目录。");
        }
    }

    private async Task<object> GetDoctorAsync(CancellationToken cancellationToken)
    {
        var status = await _backend.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
            return new
            {
                ready = status.Ready && !_stopping && _historyError is null,
                busy = status.Busy || _activeRunId is not null,
                reason = _historyError ?? (_stopping ? "服务正在关闭。" : status.Reason),
                activeRunId = _activeRunId,
                capabilities = new[] { "script", "group", "one-dragon", "persistent-runs", "idempotency", "cancellation" }
            };
    }

    private async Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(JObject? data, CancellationToken cancellationToken)
    {
        var kind = data?["kind"] is null ? null : RequiredString(data, "kind");
        if (kind is not null) ValidateKind(kind);
        var tasks = await _backend.ListTasksAsync(cancellationToken).ConfigureAwait(false);
        return tasks.Where(t => kind is null || t.Kind == kind).ToArray();
    }

    private async Task<AutomationTaskDefinition> FindTaskAsync(string kind, string name, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        ValidateName(name);
        var matches = (await _backend.ListTasksAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => t.Kind == kind && t.Name == name).ToArray();
        if (matches.Length == 0) throw new AutomationException("task_not_found", "未找到完全匹配的任务；请先 tasks list。");
        if (matches.Length > 1) throw new AutomationException("ambiguous_task", "存在同名任务，无法确定执行目标。");
        return matches[0];
    }

    private async Task<AutomationRun> StartAsync(AutomationTaskRequest request, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (TryExisting(request, out var existing)) return existing!;
            EnsureAvailable();
        }
        var task = await FindTaskAsync(request.Kind, request.Name, cancellationToken).ConfigureAwait(false);
        if (!task.CanRun) throw new AutomationException("task_unavailable", task.UnavailableReason ?? "任务不可运行。");
        var status = await _backend.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            // 验证可 await，但登记和启动不可分割；并发相同 key 只执行一次。
            if (TryExisting(request, out var existing)) return existing!;
            EnsureAvailable();
            if (!status.Ready) throw new AutomationException("not_ready", status.Reason ?? "BetterGI 尚未就绪。");
            if (status.Busy) throw new AutomationException("busy", "BetterGI 正在执行其他任务。");
            var run = new AutomationRun
            {
                RunId = request.RequestId, RequestId = request.RequestId, InstanceId = _identity.InstanceId,
                Kind = request.Kind, Name = request.Name, CreatedAt = DateTimeOffset.UtcNow
            };
            var stored = new StoredRun { Request = Clone(request), Run = run };
            Persist(stored);
            _runs.Add(run.RunId, stored);
            _activeRunId = run.RunId;
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var executionToken = _activeCancellation.Token;
            _worker = Task.Run(() => ExecuteAsync(run.RunId, executionToken), CancellationToken.None);
            return Clone(run);
        }
    }

    private bool TryExisting(AutomationTaskRequest request, out AutomationRun? run)
    {
        run = null;
        if (!_runs.TryGetValue(request.RequestId, out var stored)) return false;
        if (stored.Request.Kind != request.Kind || stored.Request.Name != request.Name ||
            !JToken.DeepEquals(stored.Request.Parameters ?? new JObject(), request.Parameters ?? new JObject()))
            throw new AutomationException("idempotency_conflict", "该 request-id 已用于不同任务或参数。");
        run = Clone(stored.Run);
        return true;
    }

    private void EnsureAvailable()
    {
        if (_stopping) throw new AutomationException("shutting_down", "服务正在关闭。");
        if (_historyError is not null) throw new AutomationException("history_unavailable", _historyError);
        if (_activeRunId is not null) throw new AutomationException("busy", "已有 CLI 任务正在执行或取消中。");
        if (_runs.Count >= MaxHistory) throw new AutomationException("history_full", "运行历史达到 4096 条上限；请停服归档历史后再执行新任务，不得重用已归档 request-id。");
    }

    private async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        AutomationExecutionResult result;
        try
        {
            AutomationTaskRequest request;
            lock (_sync)
            {
                var stored = _runs[runId];
                request = Clone(stored.Request);
                if (!cancellationToken.IsCancellationRequested)
                    Update(runId, stored.Run with { State = AutomationRunStates.Running, StartedAt = DateTimeOffset.UtcNow });
            }
            cancellationToken.ThrowIfCancellationRequested();
            result = await _backend.ExecuteAsync(request, log => Report(runId, log), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new AutomationExecutionResult { Cancelled = true };
        }
        catch (Exception error)
        {
            result = new AutomationExecutionResult { ErrorCode = (error as AutomationException)?.Code ?? "execution_failed", ErrorMessage = error.Message };
        }

        lock (_sync)
        {
            try
            {
                var stored = _runs[runId];
                // 关闭超时已将其标记 interrupted；迟到的执行结果不得覆盖。
                if (!AutomationRunStates.IsTerminal(stored.Run.State))
                {
                    var state = _shutdown.IsCancellationRequested ? AutomationRunStates.Interrupted
                        : cancellationToken.IsCancellationRequested || result.Cancelled || stored.Run.State == AutomationRunStates.CancelRequested ? AutomationRunStates.Cancelled
                        : result.Succeeded ? AutomationRunStates.Succeeded : AutomationRunStates.Failed;
                    var error = state switch
                    {
                        AutomationRunStates.Interrupted => new AutomationError("service_stopped", "服务在任务结束前关闭；请检查游戏状态后再决定下一步。"),
                        AutomationRunStates.Failed => new AutomationError(result.ErrorCode ?? "execution_failed", result.ErrorMessage ?? "执行器报告失败。"),
                        _ => null
                    };
                    if (result.Data is not null && Encoding.UTF8.GetByteCount(AutomationJson.Serialize(result.Data)) > 16 * 1024)
                    {
                        state = AutomationRunStates.Failed;
                        error = new AutomationError("result_too_large", "执行结果超过 16 KiB 限制。");
                        result = result with { Data = null };
                    }
                    Update(runId, stored.Run with { State = state, FinishedAt = DateTimeOffset.UtcNow, Result = result.Data, Error = error });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // 无法写终态时不能继续接受新任务或在内存中声称成功。
                _historyError = "无法保存运行终态：" + error.Message;
                var stored = _runs[runId];
                _runs[runId] = stored with { Run = stored.Run with { State = AutomationRunStates.Interrupted,
                    FinishedAt = DateTimeOffset.UtcNow, Error = new AutomationError("storage_error", _historyError) } };
            }
            finally
            {
                _activeRunId = null;
                _activeCancellation?.Dispose();
                _activeCancellation = null;
            }
        }
    }

    private void Report(Guid runId, AutomationLogEntry log)
    {
        lock (_sync)
        {
            var stored = _runs[runId];
            if (AutomationRunStates.IsTerminal(stored.Run.State)) return;
            var message = log.Message ?? string.Empty;
            var next = log with
            {
                Sequence = (stored.Run.Logs.LastOrDefault()?.Sequence ?? 0) + 1,
                Message = message.Length > 1000 ? message[..1000] : message
            };
            // 日志为有界观测数据，重要状态转移时统一落盘；不阻塞每条游戏日志的磁盘写入。
            _runs[runId] = stored with { Run = stored.Run with { Logs = stored.Run.Logs.Append(next).TakeLast(MaxLogs).ToArray() } };
        }
    }

    public AutomationRun GetRun(Guid runId)
    {
        lock (_sync)
            return _runs.TryGetValue(runId, out var stored) ? Clone(stored.Run)
                : throw new AutomationException("run_not_found", "运行 ID 不存在；不要仅凭此错误就重发其他 ID 的任务。");
    }

    private AutomationRun CancelRun(Guid runId)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            var run = GetRun(runId);
            if (AutomationRunStates.IsTerminal(run.State)) return run;
            if (_activeRunId != runId || _activeCancellation is null)
                throw new AutomationException("run_not_active", "该运行不属于当前活动执行。");
            Update(runId, run with { State = AutomationRunStates.CancelRequested });
            cancellation = _activeCancellation;
        }
        // 回调可能进 UI/日志；绝不能持有引擎锁执行 CancellationToken 回调。
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* 已发出取消信号；等待实际执行退出，不能提前宣称取消完成。 */ }
        return GetRun(runId);
    }

    private void Update(Guid id, AutomationRun run)
    {
        var stored = _runs[id] with { Run = run };
        Persist(stored);
        _runs[id] = stored;
    }

    private void Persist(StoredRun stored)
    {
        var path = Path.Combine(_historyDirectory, stored.Run.RunId.ToString("D") + ".json");
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(AutomationJson.Serialize(stored));
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void LoadHistory()
    {
        foreach (var path in Directory.EnumerateFiles(_historyDirectory, "*.json"))
        {
            try
            {
                if (_runs.Count >= MaxHistory) throw new InvalidDataException("运行历史文件过多。");
                if (new FileInfo(path).Length > AutomationPipeProtocol.MaxPayloadBytes) throw new InvalidDataException("历史记录过大。");
                var stored = AutomationJson.Deserialize<StoredRun>(File.ReadAllText(path));
                if (stored.Run is null || stored.Request is null || stored.Run.RunId == Guid.Empty || stored.Run.RunId != stored.Request.RequestId ||
                    stored.Run.RequestId != stored.Run.RunId || Path.GetFileNameWithoutExtension(path) != stored.Run.RunId.ToString("D"))
                    throw new InvalidDataException("历史记录 ID 不匹配。");
                if (stored.Run.Kind != stored.Request.Kind || stored.Run.Name != stored.Request.Name ||
                    stored.Run.Kind is not ("script" or "group" or "one-dragon") || string.IsNullOrWhiteSpace(stored.Run.Name) ||
                    string.IsNullOrWhiteSpace(stored.Run.InstanceId) || stored.Run.Logs is null ||
                    stored.Run.State is not ("accepted" or "running" or "cancelRequested" or "succeeded" or "failed" or "cancelled" or "interrupted"))
                    throw new InvalidDataException("历史记录结构无效。");
                if (!AutomationRunStates.IsTerminal(stored.Run.State))
                {
                    stored = stored with { Run = stored.Run with { State = AutomationRunStates.Interrupted,
                        FinishedAt = DateTimeOffset.UtcNow, Error = new AutomationError("service_restarted", "前次服务未保存任务终态；请检查实际游戏状态。") } };
                    Persist(stored);
                }
                _runs.Add(stored.Run.RunId, stored);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                _historyError = $"运行历史损坏或不可读（{Path.GetFileName(path)}）：{error.Message}。为避免重复执行，已停止接受新任务。";
            }
        }
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            // Task.Run 避免在锁内同步执行取消回调，且让 Stop/Dispose 共享同一次关闭。
            return _shutdownTask ??= Task.Run(ShutdownCoreAsync);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Task? worker;
        lock (_sync) { _stopping = true; worker = _worker; }
        try { _shutdown.Cancel(); } catch (AggregateException) { }
        if (worker is null) return;
        await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        lock (_sync)
        {
            if (_activeRunId is { } id && !AutomationRunStates.IsTerminal(_runs[id].Run.State))
                Update(id, _runs[id].Run with { State = AutomationRunStates.Interrupted, FinishedAt = DateTimeOffset.UtcNow,
                    Error = new AutomationError("service_stopped", "服务关闭时任务尚未退出；执行结果未知。") });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await ShutdownAsync().ConfigureAwait(false);
        // 仍在取消收尾的后端可能访问关联 token，不能提前释放其来源。
        if (_worker is null || _worker.IsCompleted) _shutdown.Dispose();
        else _ = _worker.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }

    private static AutomationTaskRequest ParseTaskRequest(JObject? data)
    {
        var request = new AutomationTaskRequest
        {
            RequestId = RequiredGuid(data, "requestId"), Kind = RequiredString(data, "kind"), Name = RequiredString(data, "name"),
            Parameters = data?["parameters"] switch { null or { Type: JTokenType.Null } => null, JObject obj => (JObject)obj.DeepClone(), _ => throw new AutomationException("invalid_request", "parameters 必须为 JSON 对象。") }
        };
        ValidateKind(request.Kind);
        ValidateName(request.Name);
        if (request.Kind != "script" && request.Parameters?.HasValues == true)
            throw new AutomationException("invalid_request", "只有 script 接受 parameters；配置组和一条龙使用已保存配置。");
        if (request.Parameters is not null && Encoding.UTF8.GetByteCount(AutomationJson.Serialize(request.Parameters)) > 16 * 1024)
            throw new AutomationException("invalid_request", "parameters 不能超过 16 KiB。");
        return request;
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("script" or "group" or "one-dragon")) throw new AutomationException("invalid_request", "任务类型必须为 script、group 或 one-dragon。");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name is "." or ".." || name.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new AutomationException("invalid_request", "请使用 tasks list 返回的完整任务名称，不能使用路径。");
    }

    private static string RequiredString(JObject? data, string name) => data?[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(data.Value<string>(name))
        ? data.Value<string>(name)! : throw new AutomationException("invalid_request", $"缺少字符串字段 {name}。");

    private static Guid RequiredGuid(JObject? data, string name) => Guid.TryParse(RequiredString(data, name), out var id) && id != Guid.Empty
        ? id : throw new AutomationException("invalid_request", $"{name} 必须为非空 GUID。");

    private static T Clone<T>(T value) => AutomationJson.Deserialize<T>(AutomationJson.Serialize(value!));

    private sealed record StoredRun
    {
        public AutomationTaskRequest Request { get; init; } = new();
        public AutomationRun Run { get; init; } = new();
    }
}
