using System.Reflection;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service.Automation;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.ViewModel.Pages;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Automation;

[CollectionDefinition("Automation shared execution context", DisableParallelization = true)]
public sealed class AutomationSharedExecutionContextCollection
{
}

[Collection("Automation shared execution context")]
public class AutomationEntryPointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuiGroupEntry_DuringCliRun_DoesNotMutateSharedRunnerContext(bool multipleGroups)
    {
        // 跳过 WPF/App 构造，只调用应在任何 UI、文件或执行工作之前拒绝的真实入口。
        var viewModel = (ScriptControlViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ScriptControlViewModel));
        var group = new ScriptGroup { Name = "GUI 不应执行的组" };
        typeof(ScriptControlViewModel).GetField("_selectedScriptGroup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, group);
        typeof(ScriptControlViewModel).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, NullLogger<ScriptControlViewModel>.Instance);

        var runner = RunnerContext.Instance;
        var original = (runner.taskProgress, runner.PartyName, runner.IsContinuousRunGroup, runner.IsSuspend, runner.IsPreExecution);
        var currentProgress = new TaskProgress { Name = "正在运行的 CLI 作业" };
        runner.taskProgress = currentProgress;
        runner.PartyName = "CLI 当前队伍";
        runner.IsContinuousRunGroup = true;
        runner.IsSuspend = true;
        runner.IsPreExecution = true;
        try
        {
            using var scope = new AutomationExecutionScope(CancellationContext.Instance, CancellationToken.None, _ => { });
            // 不绑定 scope：用户点 GUI 命令的上下文不属于 CLI 作业。
            Assert.Null(AutomationExecutionScope.Current);
            if (multipleGroups) await viewModel.StartGroups([group]);
            else await viewModel.OnStartScriptGroupAsync();

            Assert.Same(currentProgress, runner.taskProgress);
            Assert.Equal("CLI 当前队伍", runner.PartyName);
            Assert.True(runner.IsContinuousRunGroup);
            Assert.True(runner.IsSuspend);
            Assert.True(runner.IsPreExecution);
            Assert.Same(scope.CancellationSource, CancellationContext.Instance.Cts);
            Assert.False(scope.IsCancellationRequested);
        }
        finally
        {
            (runner.taskProgress, runner.PartyName, runner.IsContinuousRunGroup, runner.IsSuspend, runner.IsPreExecution) = original;
        }
    }

    [Theory]
    [InlineData("SOneDragonFlowCommand")]
    [InlineData("SwitchAutoGeniusInvokationCommand")]
    [InlineData("SwitchAutoWoodCommand")]
    [InlineData("SwitchAutoFightCommand")]
    [InlineData("SwitchAutoDomainCommand")]
    [InlineData("SwitchAutoBossCommand")]
    [InlineData("SwitchAutoStygianOnslaughtCommand")]
    [InlineData("SwitchAutoMusicGameCommand")]
    [InlineData("SwitchAutoAlbumCommand")]
    [InlineData("SwitchAutoCookCommand")]
    [InlineData("SwitchAutoComboCommand")]
    [InlineData("SwitchAutoComboRunCommand")]
    [InlineData("SwitchAutoFishingCommand")]
    [InlineData("SwitchAutoLeyLineOutcropCommand")]
    [InlineData("SwitchArtifactSalvageCommand")]
    [InlineData("SwitchGetGridIconsCommand")]
    [InlineData("SwitchGridIconsModelAccuracyTestCommand")]
    [InlineData("RunInventoryCountComparisonCommand")]
    [InlineData("SwitchAutoRedeemCodeCommand")]
    public async Task GuiStartCommand_DuringCliRun_ReturnsNormallyBeforeMutatingUiOrLoadingTasks(string commandProperty)
    {
        // Config、策略目录和任务构造器都未初始化；忙碌必须在接触它们之前正常返回。
        var viewModel = (TaskSettingsPageViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskSettingsPageViewModel));
        viewModel.SwitchAutoDomainButtonText = "启动";
        viewModel.SwitchAutoComboRunButtonText = "测试运行";
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        using var scope = new AutomationExecutionScope(CancellationContext.Instance, CancellationToken.None, _ => { });
        var property = typeof(TaskSettingsPageViewModel).GetProperty(commandProperty)!;
        Assert.NotNull(property);
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(property.GetValue(viewModel));

        await command.ExecuteAsync(null);

        Assert.True(command.ExecutionTask!.IsCompletedSuccessfully);
        Assert.False(command.IsRunning);
        Assert.Empty(notifications);
        Assert.False(viewModel.SwitchAutoDomainEnabled);
        Assert.False(viewModel.SwitchAutoWoodEnabled);
        Assert.False(viewModel.SwitchAutoBossEnabled);
        Assert.False(viewModel.SwitchAutoFightEnabled);
        Assert.False(viewModel.SwitchAutoMusicGameEnabled);
        Assert.False(viewModel.SwitchGetGridIconsEnabled);
        Assert.Equal("启动", viewModel.SwitchAutoDomainButtonText);
        Assert.Equal("测试运行", viewModel.SwitchAutoComboRunButtonText);
        Assert.Same(scope.CancellationSource, CancellationContext.Instance.Cts);
        Assert.False(scope.IsCancellationRequested);
        Assert.False(CancellationContext.Instance.IsPreparing);
        Assert.Empty(scope.Failures);
    }

    [Fact]
    public async Task DirectSoloEntry_DuringCliRun_ReturnsBeforeTaskOrCancellationAccess()
    {
        var runner = (TaskRunner)RuntimeHelpers.GetUninitializedObject(typeof(TaskRunner));
        using var scope = new AutomationExecutionScope(CancellationContext.Instance, CancellationToken.None, _ => { });

        await runner.RunSoloTaskAsync(new ForbiddenSoloTask());

        Assert.Same(scope.CancellationSource, CancellationContext.Instance.Cts);
        Assert.False(scope.IsCancellationRequested);
        Assert.False(CancellationContext.Instance.IsPreparing);
        Assert.Empty(scope.Failures);
    }

    [Fact]
    public async Task DirectDragonEntry_DuringCliRun_DoesNotReadOrSaveSelectedConfig()
    {
        var viewModel = (OneDragonFlowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(OneDragonFlowViewModel));
        using var scope = new AutomationExecutionScope(CancellationContext.Instance, CancellationToken.None, _ => { });

        await viewModel.OnOneKeyExecute();

        Assert.Null(viewModel.SelectedConfig);
        Assert.Same(scope.CancellationSource, CancellationContext.Instance.Cts);
        Assert.False(scope.IsCancellationRequested);
        Assert.Empty(scope.Failures);
    }

    [Fact]
    public async Task DirectScriptServiceEntry_DuringCliRun_DoesNotEnumerateOrResetProjects()
    {
        var service = (ScriptService)RuntimeHelpers.GetUninitializedObject(typeof(ScriptService));
        using var scope = new AutomationExecutionScope(CancellationContext.Instance, CancellationToken.None, _ => { });

        await service.RunMulti(ForbiddenProjects());

        Assert.Same(scope.CancellationSource, CancellationContext.Instance.Cts);
        Assert.False(scope.IsCancellationRequested);
        Assert.Empty(scope.Failures);
    }

    private static IEnumerable<ScriptGroupProject> ForbiddenProjects() =>
        Enumerable.Range(0, 1).Select<int, ScriptGroupProject>(_ =>
            throw new InvalidOperationException("忙碌时不应读取项目列表。"));

    private sealed class ForbiddenSoloTask : ISoloTask
    {
        public string Name => throw new InvalidOperationException("忙碌时不应读取任务名称。");
        public Task Start(CancellationToken ct) => throw new InvalidOperationException("测试不得启动游戏任务。");
    }
}
