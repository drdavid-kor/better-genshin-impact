using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Parsing;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Automation;

public class AutomationExecutionScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalEnd_DoesNotFailCompletedOrCancelledJob(bool cancelled)
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        scope.RecordExecution();
        if (cancelled) context.ManualCancel();

        // TaskRunner 和连续配置组传播后的处理都走同一个记录入口。
        await Task.Run(() => AutomationExecutionScope.Current!.RecordException(
            new NormalEndException(cancelled ? "取消自动任务" : "战斗结束")));

        Assert.Empty(scope.Failures);
        Assert.Equal(cancelled, scope.IsCancellationRequested);
        Assert.Equal(1, scope.ExecutedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealFailure_IsRecordedIncludingExplicitNormalEndFailure(bool normalEnd)
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        Exception error = normalEnd
            ? new NormalEndException("任务前置条件不满足", isFailure: true)
            : new InvalidOperationException("任务前置条件不满足");

        scope.RecordException(error);

        Assert.Equal("任务前置条件不满足", Assert.Single(scope.Failures));
        Assert.False(scope.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationCancelled_IsIgnoredOnlyWhenJobWasCancelled(bool cancelled)
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        if (cancelled) context.Cancel();

        scope.RecordException(new OperationCanceledException("取消异常"));

        Assert.Equal(cancelled, scope.IsCancellationRequested);
        if (cancelled) Assert.Empty(scope.Failures);
        else Assert.Equal("取消异常", Assert.Single(scope.Failures));
    }

    [Fact]
    public void DragonGroupReference_RequiresExactFilenameAndInternalName()
    {
        var root = Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "日常采集.json"), "{\"Name\":\"其他任务\",\"Projects\":[]}");
            File.WriteAllText(Path.Combine(root, "另一个文件.json"), "{\"Name\":\"日常采集\",\"Projects\":[]}");
            var mismatch = Assert.Throws<AutomationException>(() =>
                BetterGiAutomationBackend.ValidateDragonGroupReferences(["日常采集"], root));
            Assert.Equal("invalid_configuration", mismatch.Code);
            Assert.Contains("其他任务", mismatch.Message);

            File.WriteAllText(Path.Combine(root, "日常采集.json"), "{\"Name\":\"日常采集\",\"Projects\":[]}");
            BetterGiAutomationBackend.ValidateDragonGroupReferences(["日常采集"], root);
            Assert.Throws<AutomationException>(() => BetterGiAutomationBackend.ValidateDragonGroupReferences(["不存在"], root));
            Assert.Throws<AutomationException>(() => BetterGiAutomationBackend.ValidateDragonGroupReferences(["..\\日常采集"], root));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task AutomationStartWithoutUi_FailsBeforeInteractiveStartup()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        var error = await Assert.ThrowsAsync<AutomationException>(() => ScriptService.StartGameTask());
        Assert.Equal("not_ready", error.Code);
        Assert.False(context.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, false, false, false, "窗口")]
    [InlineData(true, false, false, false, "截图器")]
    [InlineData(true, true, false, true, "初始化")]
    [InlineData(true, true, true, false, "失效")]
    public void Readiness_RejectsMissingPrerequisitesWithoutStartingCapture(bool window, bool capture,
        bool initialized, bool gameWindow, string expectedReason)
    {
        var status = BetterGiAutomationBackend.InspectReadiness(window, capture, initialized, gameWindow, false);
        Assert.False(status.Ready);
        Assert.Contains(expectedReason, status.Reason);
    }

    [Fact]
    public void Readiness_RequiresAllPrerequisitesAndKeepsBusySeparate()
    {
        var status = BetterGiAutomationBackend.InspectReadiness(true, true, true, true, true);
        Assert.True(status.Ready);
        Assert.True(status.Busy);
        Assert.Null(status.Reason);
    }

    [Theory]
    [InlineData("自动首领讨伐", true)]
    [InlineData("自动地脉花", false)]
    public void ScheduledDragonSkips_DoNotCountAsExecuted(string taskName, bool shouldRunLeyLineToday)
    {
        var context = new CancellationContext();
        var logs = new List<AutomationLogEntry>();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, logs.Add);
        using var binding = scope.Bind();
        var config = new OneDragonFlowConfig
        {
            AutoBossSpecifyRunCount = true,
            AutoBossTotalRunCountLimit = 2,
            AutoBossCompletedRunCount = 2
        };
        Assert.True(OneDragonTaskItem.RecordAutomationScheduledSkip(taskName, config, shouldRunLeyLineToday));
        Assert.Equal(0, scope.ExecutedCount);
        Assert.Equal(1, scope.SkippedCount);
        Assert.Contains("跳过", Assert.Single(logs).Message);
        Assert.Empty(scope.Failures);
    }

    [Fact]
    public void BossBelowLimit_AndScheduledLeyLine_AreNotSkipped()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        var config = new OneDragonFlowConfig
        {
            AutoBossSpecifyRunCount = true, AutoBossTotalRunCountLimit = 2, AutoBossCompletedRunCount = 1
        };
        Assert.False(OneDragonTaskItem.RecordAutomationScheduledSkip("自动首领讨伐", config, true));
        Assert.False(OneDragonTaskItem.RecordAutomationScheduledSkip("自动地脉花", config, true));
        Assert.Equal(0, scope.SkippedCount);
    }

    [Fact]
    public void ScriptSettings_PreserveIsoStringsIncludingTimezoneAndFractionalSeconds()
    {
        const string json = "{\"timestamp\":\"2026-09-29T08:09:10.1234000+08:00\",\"utc\":\"2026-09-29T00:09:10.1200Z\"}";
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var parameters = JObject.Load(reader);
        var settings = (IDictionary<string, object?>)BetterGiAutomationBackend.ParseScriptSettings(parameters);

        Assert.Equal("2026-09-29T08:09:10.1234000+08:00", Assert.IsType<string>(settings["timestamp"]));
        Assert.Equal("2026-09-29T00:09:10.1200Z", Assert.IsType<string>(settings["utc"]));
    }

    [Fact]
    public void SetAndClearWithinJob_PreserveCancellationAcrossNestedTasks()
    {
        var context = new CancellationContext();
        using var external = new CancellationTokenSource();
        using var scope = new AutomationExecutionScope(context, external.Token, _ => { });
        using var binding = scope.Bind();
        var original = context.Cts;
        external.Cancel();

        context.Clear();
        context.Set();
        context.Clear();
        context.Set();

        Assert.Same(original, context.Cts);
        Assert.True(context.IsCancellationRequested);
        Assert.True(scope.IsCancellationRequested);
        Assert.True(context.IsAutomationOwned);
    }

    [Fact]
    public async Task UnrelatedGuiContext_CannotResetOrReleaseJobCancellation()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        Task unrelated;
        using (ExecutionContext.SuppressFlow())
        {
            unrelated = Task.Run(() =>
            {
                Assert.Null(AutomationExecutionScope.Current);
                Assert.Throws<InvalidOperationException>(() => context.Set());
                context.Clear();
            });
        }
        await unrelated;

        context.ManualCancel();
        Assert.True(scope.IsCancellationRequested);
        Assert.True(context.IsManualStop);
        Assert.True(context.IsAutomationOwned);
    }

    [Fact]
    public void Dispose_ReleasesCancellationOwnershipForFollowingGuiTask()
    {
        var context = new CancellationContext();
        var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using (scope.Bind())
        {
            context.ManualCancel();
            scope.Dispose();
            Assert.Null(AutomationExecutionScope.Current);
        }
        scope.Dispose();
        context.Set();
        Assert.False(context.IsAutomationOwned);
        Assert.False(context.IsCancellationRequested);
        Assert.False(context.IsManualStop);
        context.Clear();
    }

    [Fact]
    public async Task Scope_FlowsToTaskRunnerThread_AndRecordsErrors()
    {
        var context = new CancellationContext();
        var logs = new List<AutomationLogEntry>();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, logs.Add);
        using var binding = scope.Bind();
        await Task.Run(() =>
        {
            Assert.Same(scope, AutomationExecutionScope.Current);
            AutomationExecutionScope.Current!.RecordExecution();
            new AutomationLogSink().Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null,
                new MessageTemplateParser().Parse("内部任务报告失败"), []));
        });

        Assert.Equal(1, scope.ExecutedCount);
        Assert.Equal("内部任务报告失败", Assert.Single(scope.Failures));
        Assert.Equal("error", Assert.Single(logs).Level);
    }

    [Fact]
    public void SecondScope_CannotTakeExistingJobCancellation()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        Assert.Throws<AutomationException>(() => new AutomationExecutionScope(context, CancellationToken.None, _ => { }));
        Assert.Same(scope.CancellationSource, context.Cts);
    }

    [Fact]
    public async Task ParallelRunnersWithinJob_StillRequireExclusiveGameAccess()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        Assert.True(scope.TryEnterRunner());
        Assert.False(await Task.Run(() => AutomationExecutionScope.Current!.TryEnterRunner()));
        scope.ExitRunner();
        Assert.True(scope.TryEnterRunner());
        scope.ExitRunner();
    }

    [Fact]
    public void GuiPreparationFirst_RejectsCliWithoutChangingGuiToken()
    {
        var context = new CancellationContext();
        using (var preparation = context.TryReservePreparation())
        {
            Assert.NotNull(preparation);
            context.Set();
            var original = context.Cts;
            var exception = Assert.Throws<AutomationException>(() =>
                new AutomationExecutionScope(context, CancellationToken.None, _ => { }));
            Assert.Equal("busy", exception.Code);
            Assert.Same(original, context.Cts);
            Assert.True(context.IsPreparing);
        }
        Assert.False(context.IsPreparing);
        using var next = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        Assert.True(context.IsAutomationOwned);
    }

    [Fact]
    public void CliFirst_RejectsGuiPreparation_ButAllowsOwnedNestedPreparation()
    {
        var context = new CancellationContext();
        using var scope = new AutomationExecutionScope(context, CancellationToken.None, _ => { });
        Assert.Null(context.TryReservePreparation());
        Assert.False(context.IsPreparing);
        Assert.Same(scope.CancellationSource, context.Cts);
        using (scope.Bind())
        using (var preparation = context.TryReservePreparation())
        {
            Assert.NotNull(preparation);
            Assert.False(context.IsPreparing);
            Assert.Same(scope.CancellationSource, context.Cts);
        }
    }

    [Theory]
    [InlineData("关闭游戏")]
    [InlineData("关闭软件")]
    [InlineData("关闭游戏和软件")]
    [InlineData("关机")]
    public void DragonCompletionActions_AreRejectedBeforeExecution(string action)
    {
        var config = new OneDragonFlowConfig
        {
            Name = "测试配置", CompletionAction = action,
            TaskEnabledList = new Dictionary<string, bool> { ["领取邮件"] = true }
        };
        Assert.Throws<AutomationException>(() => BetterGiAutomationBackend.ValidateDragon(config));
    }

    [Fact]
    public void EnableCliFlag_DoesNotReplaceExistingActivationArguments()
    {
        var options = CommandLineOptions.Parse(["BetterGI.exe", "--enable-cli", "--instance", "childSession", "--startGroups", "精确组名"]);
        Assert.True(options.EnableCli);
        Assert.Equal(CommandLineAction.StartGroups, options.Action);
        Assert.Equal("精确组名", Assert.Single(options.GroupNames));
        Assert.False(CommandLineOptions.Parse(["BetterGI.exe"]).EnableCli);
    }

    [Fact]
    public async Task Catalog_RejectsDuplicateNames_AndInvalidConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "User", "ScriptGroup"));
        try
        {
            var group = new JObject
            {
                ["Name"] = "重复名称",
                ["Projects"] = new JArray(new JObject { ["Name"] = "任务", ["Status"] = "Enabled", ["RunNum"] = 1 })
            };
            File.WriteAllText(Path.Combine(root, "User", "ScriptGroup", "first.json"), group.ToString());
            File.WriteAllText(Path.Combine(root, "User", "ScriptGroup", "second.json"), group.ToString());
            File.WriteAllText(Path.Combine(root, "User", "ScriptGroup", "invalid.json"), "{broken");

            var tasks = await new BetterGiAutomationBackend(root).ListTasksAsync(CancellationToken.None);
            Assert.Equal(3, tasks.Count);
            Assert.All(tasks, task => Assert.False(task.CanRun));
            Assert.Equal(2, tasks.Count(task => task.Name == "重复名称"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ScriptDiscovery_UsesFolderName_AndNeverFallsBackToDisplayName()
    {
        var root = Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "User", "JsScript", "精确文件夹");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                "{\"name\":\"展示名\",\"version\":\"1.0\",\"main\":\"main.js\",\"settings_ui\":\"settings.json\"}");
            File.WriteAllText(Path.Combine(directory, "main.js"), "// Discovery must not execute this file.");
            var backend = new BetterGiAutomationBackend(root);
            var task = Assert.Single(await backend.ListTasksAsync(CancellationToken.None));
            Assert.Equal("精确文件夹", task.Name);
            Assert.Equal("展示名", task.Details!["displayName"]!.Value<string>());
            Assert.True(task.CanRun);

            var error = await Assert.ThrowsAsync<AutomationException>(() => backend.ExecuteAsync(
                new AutomationTaskRequest { Kind = "script", Name = "展示名" }, _ => { }, CancellationToken.None));
            Assert.Equal("task_not_found", error.Code);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }
    private static void DeleteTemporaryDirectory(string root)
    {
        var intendedDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI.Automation.Tests"));
        var resolvedRoot = Path.GetFullPath(root);
        if (!resolvedRoot.StartsWith(intendedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(resolvedRoot), intendedDirectory, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(resolvedRoot), "N", out _))
            throw new InvalidOperationException("拒绝删除测试临时目录之外的路径。");
        Directory.Delete(resolvedRoot, recursive: true);
    }
}
