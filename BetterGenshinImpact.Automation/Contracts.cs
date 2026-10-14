using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Automation;

public sealed record AutomationIdentity
{
    public int ApiVersion { get; init; } = 1;
    public string InstanceId { get; init; } = string.Empty;
    public string InstallationPath { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public int WindowsSessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
}

public sealed record AutomationRequest
{
    public int ApiVersion { get; init; } = 1;
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public string Operation { get; init; } = string.Empty;
    public string? InstallationPath { get; init; }
    public string? InstanceId { get; init; }
    public JObject? Data { get; init; }
}

public sealed record AutomationResponse
{
    public int ApiVersion { get; init; } = 1;
    public Guid RequestId { get; init; }
    public bool Ok { get; init; }
    public AutomationIdentity? Instance { get; init; }
    public JObject? Data { get; init; }
    public AutomationError? Error { get; init; }
}

public sealed record AutomationError(string Code, string Message);

public sealed class AutomationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record AutomationBackendStatus
{
    public bool Ready { get; init; }
    public bool Busy { get; init; }
    public string? Reason { get; init; }
}

public sealed record AutomationTaskDefinition
{
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool CanRun { get; init; } = true;
    public string? UnavailableReason { get; init; }
    public JObject? Details { get; init; }
}

public sealed record AutomationTaskRequest
{
    public Guid RequestId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public JObject? Parameters { get; init; }
}

public sealed record AutomationExecutionResult
{
    public bool Succeeded { get; init; }
    public bool Cancelled { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public JObject? Data { get; init; }
}

public sealed record AutomationLogEntry
{
    public long Sequence { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Level { get; init; } = "info";
    public string Message { get; init; } = string.Empty;
}

public static class AutomationRunStates
{
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string CancelRequested = "cancelRequested";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";

    public static bool IsTerminal(string state) => state is Succeeded or Failed or Cancelled or Interrupted;
}

public sealed record AutomationRun
{
    public Guid RunId { get; init; }
    public Guid RequestId { get; init; }
    public string InstanceId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string State { get; init; } = AutomationRunStates.Accepted;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public JObject? Result { get; init; }
    public AutomationError? Error { get; init; }
    public AutomationLogEntry[] Logs { get; init; } = [];
}

public interface IAutomationBackend
{
    Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken cancellationToken);
    Task<AutomationExecutionResult> ExecuteAsync(
        AutomationTaskRequest request,
        Action<AutomationLogEntry> report,
        CancellationToken cancellationToken);
}
