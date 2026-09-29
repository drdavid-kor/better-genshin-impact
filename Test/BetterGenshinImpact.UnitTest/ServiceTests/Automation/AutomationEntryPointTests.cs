using System.Reflection;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service.Automation;
using BetterGenshinImpact.ViewModel.Pages;
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
            await Assert.ThrowsAsync<InvalidOperationException>(() => multipleGroups
                ? viewModel.StartGroups([group])
                : viewModel.OnStartScriptGroupAsync());

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
}
