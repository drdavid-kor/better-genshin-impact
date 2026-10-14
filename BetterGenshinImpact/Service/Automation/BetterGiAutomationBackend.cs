using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.ViewModel.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vanara.PInvoke;

namespace BetterGenshinImpact.Service.Automation;

internal sealed class BetterGiAutomationBackend : IAutomationBackend
{
    private readonly string _installationPath;

    internal BetterGiAutomationBackend(string installationPath)
    {
        _installationPath = Path.GetFullPath(installationPath);
    }

    public async Task<AutomationBackendStatus> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return InspectReadiness(false, false, false, false, false);
        return await dispatcher.InvokeAsync(ReadReadiness).Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private static AutomationBackendStatus ReadReadiness()
    {
        var taskContext = TaskContext.Instance();
        return InspectReadiness(true, App.GetService<HomePageViewModel>()?.TaskDispatcherEnabled == true,
            taskContext.IsInitialized, taskContext.GameHandle != IntPtr.Zero && User32.IsWindow(taskContext.GameHandle),
            TaskControl.TaskSemaphore.CurrentCount == 0 || CancellationContext.Instance.IsPreparing);
    }

    internal static AutomationBackendStatus InspectReadiness(bool windowReady, bool captureEnabled,
        bool initialized, bool gameWindowValid, bool busy)
    {
        var reason = !windowReady ? "BetterGI 窗口尚未就绪或正在退出。" :
            !captureEnabled ? "请先在 BetterGI 启动页启用截图器；CLI 不会自动启动游戏或截图器。" :
            !initialized ? "截图器尚未初始化，请等待 BetterGI 完成启动。" :
            !gameWindowValid ? "原神窗口已失效，请在 BetterGI 中重新连接游戏并启用截图器。" : null;
        return new AutomationBackendStatus
        {
            Ready = reason is null, Busy = busy, Reason = reason
        };
    }

    internal static void EnsureExecutionReady()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            throw new AutomationException("not_ready", "BetterGI 窗口尚未就绪或正在退出。");
        var status = dispatcher.CheckAccess() ? ReadReadiness() : dispatcher.Invoke(ReadReadiness);
        if (!status.Ready) throw new AutomationException("not_ready", status.Reason!);
    }

    public Task<IReadOnlyList<AutomationTaskDefinition>> ListTasksAsync(CancellationToken ct)
    {
        var tasks = ReadCatalog(ct).Select(entry => entry.Definition).ToArray();
        return Task.FromResult<IReadOnlyList<AutomationTaskDefinition>>(tasks);
    }

    public async Task<AutomationExecutionResult> ExecuteAsync(AutomationTaskRequest request,
        Action<AutomationLogEntry> report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Kind != "script" && request.Parameters is { Count: > 0 })
        {
            throw new AutomationException("invalid_parameters", "只有 script 任务接受 settings 参数。");
        }

        // 执行前重新发现，拒绝文件被删除、重名或配置变更，绝不回退到 UI 选中项。
        var matches = ReadCatalog(ct).Where(entry =>
            entry.Definition.Kind == request.Kind && entry.Definition.Name == request.Name).ToArray();
        if (matches.Length != 1)
        {
            throw new AutomationException(matches.Length == 0 ? "task_not_found" : "ambiguous_task",
                $"找不到唯一的 {request.Kind} 任务：{request.Name}");
        }
        var entry = matches[0];
        if (!entry.Definition.CanRun)
        {
            throw new AutomationException("task_unavailable", entry.Definition.UnavailableReason ?? "任务不可执行。");
        }
        if (!await TaskControl.TaskSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new AutomationException("busy", "BetterGI 正在执行其他独立任务。");
        }

        try
        {
            using var scope = new AutomationExecutionScope(CancellationContext.Instance, ct, report);
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new AutomationException("not_ready", "BetterGI 窗口尚未就绪。");
            try
            {
                // WPF 入口必须在 STA 上调用，并显式绑定异步执行上下文。
                await dispatcher.InvokeAsync(async () =>
                {
                    using var binding = scope.Bind();
                    ct.ThrowIfCancellationRequested();
                    EnsureExecutionReady();
                    switch (request.Kind)
                    {
                        case "script":
                            await RunScriptAsync(entry, request.Parameters, scope);
                            break;
                        case "group":
                            await RunGroupAsync(entry);
                            break;
                        case "one-dragon":
                            var config = ReadDragon(entry.Path);
                            ValidateDragon(config);
                            await new OneDragonFlowViewModel().RunAutomationAsync(config);
                            break;
                        default:
                            throw new AutomationException("invalid_kind", "不支持的任务类型。");
                    }
                }).Task.Unwrap().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scope.IsCancellationRequested)
            {
                // 只有委托真实退出后才报告取消；仍在停止中的任务继续占有任务锁。
            }
            catch (NormalEndException exception) when (!exception.IsFailure)
            {
                // 连续配置组会将正常结束向外传播，不能在最外层重新记成错误。
                // 手动取消仍由 scope 的取消源决定终态。
            }
            catch (Exception exception)
            {
                scope.RecordException(exception);
                report(new AutomationLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = "error",
                    Message = exception.GetBaseException().Message
                });
            }
            finally
            {
                // 即便启动阶段失败，也恢复上下文。TaskRunner 已负责自己的输入/截图清理。
                RunnerContext.Instance.Reset();
                TaskContext.Instance().CurrentScriptProject = null;
            }

            var failures = scope.Failures;
            var cancelled = scope.IsCancellationRequested;
            var succeeded = !cancelled && failures.Length == 0 && scope.ExecutedCount > 0;
            return new AutomationExecutionResult
            {
                Succeeded = succeeded,
                Cancelled = cancelled,
                ErrorCode = cancelled ? "cancelled" : failures.Length > 0 ? "execution_failed" :
                    scope.ExecutedCount == 0 ? "no_tasks_executed" : null,
                ErrorMessage = cancelled ? "任务已停止。" : failures.FirstOrDefault() ??
                    (scope.ExecutedCount == 0 ? "任务流程结束，但全部任务均被跳过或未执行。" : null),
                Data = new JObject
                {
                    ["executedCount"] = scope.ExecutedCount,
                    ["skippedCount"] = scope.SkippedCount,
                    ["reportedErrors"] = new JArray(failures),
                    ["resultBasis"] = "执行流程及报告的异常/错误；未独立验证游戏内奖励。"
                }
            };
        }
        finally
        {
            TaskControl.TaskSemaphore.Release();
        }
    }

    private static async Task RunScriptAsync(CatalogEntry entry, JObject? parameters, AutomationExecutionScope scope)
    {
        var script = new ScriptProject(Path.GetFileName(entry.Path));
        var project = new ScriptGroupProject(script)
        {
            JsScriptSettingsObject = ParseScriptSettings(parameters)
        };
        // 与调度器一致设置 GroupInfo，脚本内的路径任务可以读取默认队伍配置。
        var group = new ScriptGroup { Name = $"CLI: {entry.Definition.Name}" };
        group.AddProject(project);
        await ScriptService.StartGameTask();
        scope.CancellationSource.Token.ThrowIfCancellationRequested();
        await new TaskRunner().RunThreadAsync(async () =>
        {
            scope.RecordExecution();
            TaskContext.Instance().CurrentScriptProject = project;
            await project.Run();
        });
    }

    internal static ExpandoObject ParseScriptSettings(JObject? parameters) => parameters is null
        ? new ExpandoObject()
        : JsonConvert.DeserializeObject<ExpandoObject>(parameters.ToString(Formatting.None),
            new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;

    private static async Task RunGroupAsync(CatalogEntry entry)
    {
        var group = ScriptGroup.FromJson(await File.ReadAllTextAsync(entry.Path));
        var service = App.GetService<IScriptService>()
            ?? throw new AutomationException("not_ready", "脚本服务尚未就绪。");
        await service.RunMulti(ScriptControlViewModel.GetNextProjects(group), group.Name);
    }

    private List<CatalogEntry> ReadCatalog(CancellationToken ct)
    {
        var entries = new List<CatalogEntry>();
        var scriptRoot = Path.Combine(_installationPath, "User", "JsScript");
        if (Directory.Exists(scriptRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(scriptRoot).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                var manifestFile = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestFile)) continue;
                try
                {
                    var manifest = Manifest.FromJson(File.ReadAllText(manifestFile));
                    manifest.Validate(directory);
                    entries.Add(new(new AutomationTaskDefinition
                    {
                        Kind = "script", Name = name, Description = manifest.Description,
                        Details = new JObject
                        {
                            ["displayName"] = manifest.Name,
                            ["version"] = manifest.Version,
                            ["settingsFile"] = manifest.SettingsUi
                        }
                    }, directory));
                }
                catch (Exception exception)
                {
                    entries.Add(Unavailable("script", name, directory, exception.Message));
                }
            }
        }

        AddJsonTasks("group", "ScriptGroup", path =>
        {
            var group = ScriptGroup.FromJson(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(group.Name)) throw new InvalidDataException("配置组名称为空。");
            var runnable = group.Projects.Any(project => project.Status == "Enabled" && project.RunNum > 0);
            return new AutomationTaskDefinition
            {
                Kind = "group", Name = group.Name, Description = $"调度器配置组，{group.Projects.Count} 个项目。",
                CanRun = runnable, UnavailableReason = runnable ? null : "配置组没有启用的任务。",
                Details = new JObject { ["projectCount"] = group.Projects.Count }
            };
        });
        AddJsonTasks("one-dragon", "OneDragon", path =>
        {
            var config = ReadDragon(path);
            string? reason = null;
            try { ValidateDragon(config); }
            catch (Exception exception) { reason = exception.Message; }
            return new AutomationTaskDefinition
            {
                Kind = "one-dragon", Name = config.Name, Description = "一条龙配置。",
                CanRun = reason is null, UnavailableReason = reason,
                Details = new JObject { ["completionAction"] = config.CompletionAction }
            };
        });

        // 内容名称重复时，所有候选均不可执行，防止 First() 悄悄选择错误配置。
        var duplicates = entries.GroupBy(entry => (entry.Definition.Kind, entry.Definition.Name))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return entries.Select(entry => duplicates.Contains((entry.Definition.Kind, entry.Definition.Name))
            ? entry with { Definition = entry.Definition with { CanRun = false, UnavailableReason = "存在重复名称，请先在 BetterGI 中重命名。" } }
            : entry).ToList();

        void AddJsonTasks(string kind, string folder, Func<string, AutomationTaskDefinition> read)
        {
            var directory = Path.Combine(_installationPath, "User", folder);
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try { entries.Add(new(read(path), path)); }
                catch (Exception exception) { entries.Add(Unavailable(kind, Path.GetFileNameWithoutExtension(path), path, exception.Message)); }
            }
        }
    }

    private static CatalogEntry Unavailable(string kind, string name, string path, string reason) =>
        new(new AutomationTaskDefinition { Kind = kind, Name = name, Description = "配置无法读取。", CanRun = false, UnavailableReason = reason }, path);

    private static OneDragonFlowConfig ReadDragon(string path) =>
        JsonConvert.DeserializeObject<OneDragonFlowConfig>(File.ReadAllText(path))
        ?? throw new InvalidDataException("一条龙配置为空。");

    internal static void ValidateDragon(OneDragonFlowConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new AutomationException("invalid_configuration", "一条龙配置名称为空。");
        if (!string.IsNullOrEmpty(config.CompletionAction) && config.CompletionAction != "无")
            throw new AutomationException("unsupported_completion_action", "CLI 不执行关闭软件、游戏或关机操作；请将一条龙完成后操作设置为“无”。");
        if (config.TaskEnabledList is null || !config.TaskEnabledList.Any(item => item.Value))
            throw new AutomationException("empty_task", "一条龙配置没有启用的任务。");
    }

    internal static void ValidateDragonGroupReferences(IEnumerable<string> groupNames, string groupDirectory)
    {
        foreach (var name in groupNames.Distinct(StringComparer.Ordinal))
        {
            // 现有一条龙用名称拼接文件名；CLI 必须先验证该文件确实表示同名组。
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name != Path.GetFileName(name))
                throw new AutomationException("invalid_configuration", $"一条龙引用的配置组名称不是有效文件名：{name}");
            var path = Path.Combine(groupDirectory, name + ".json");
            if (!File.Exists(path))
                throw new AutomationException("invalid_configuration", $"一条龙引用的配置组文件不存在：{name}.json");
            var group = ScriptGroup.FromJson(File.ReadAllText(path));
            if (!string.Equals(group.Name, name, StringComparison.Ordinal))
                throw new AutomationException("invalid_configuration",
                    $"一条龙引用 {name}.json，但文件内配置组名称为 {group.Name}；已拒绝执行，请先在 BetterGI 中修正配置。");
        }
    }

    private sealed record CatalogEntry(AutomationTaskDefinition Definition, string Path);
}
