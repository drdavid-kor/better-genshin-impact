using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Automation;
using BetterGenshinImpact.ViewModel.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Automation;

public class AutomationDragonConfigStoreTests
{
    [Fact]
    public async Task RewardCount_IsSavedBeforeCallbackReturns_AndNextRunSkipsAtLimit()
    {
        using var fixture = new ConfigFixture();
        using (var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path))
        {
            await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++);
            Assert.Equal(2, fixture.Read().AutoBossCompletedRunCount);
            await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++);
            Assert.Equal(3, fixture.Read().AutoBossCompletedRunCount);
        }

        // 下一次 CLI 重新从磁盘读取，不能再计算出可消耗树脂的剩余次数。
        var nextRun = fixture.Read();
        using var scope = new AutomationExecutionScope(new CancellationContext(), CancellationToken.None, _ => { });
        using var binding = scope.Bind();
        Assert.True(OneDragonTaskItem.RecordAutomationScheduledSkip("自动首领讨伐", nextRun, true));
        Assert.Equal(0, scope.ExecutedCount);
        Assert.Equal(1, scope.SkippedCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCancellationOrFailure_DoesNotLoseAlreadyClaimedRewards(bool cancelled)
    {
        using var fixture = new ConfigFixture();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
            await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++);
            if (cancelled) throw new OperationCanceledException();
            throw new InvalidOperationException("后续任务失败");
        });

        Assert.Equal(2, fixture.Read().AutoBossCompletedRunCount);
        fixture.Config.AutoBossCompletedRunCount++;
        Assert.Equal(2, fixture.Read().AutoBossCompletedRunCount); // 结束后解除订阅。
    }

    [Fact]
    public void CliSave_UsesDiscoveredFileEvenWhenConfigNameDiffers_AndPreservesTaskSettings()
    {
        using var fixture = new ConfigFixture();
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        var viewModel = UninitializedViewModel();
        SetField(viewModel, "_automationConfigStore", store);
        fixture.Config.NextTaskId = "boss-task";

        viewModel.WriteConfig(fixture.Config);

        var saved = fixture.Read();
        Assert.Equal(fixture.Config.Name, saved.Name);
        Assert.Equal("boss-task", saved.NextTaskId);
        Assert.Equal("自动首领讨伐", saved.TaskDefinitions["boss-task"]);
        Assert.True(saved.TaskEnabledList["boss-task"]);
        Assert.Equal("boss-task", Assert.Single(saved.TaskOrder));
        Assert.Single(Directory.EnumerateFiles(fixture.Directory));
    }

    [Fact]
    public void LockedFile_FailsExecutionAndStopsFurtherSaves_WithoutCorruptingConfig()
    {
        using var fixture = new ConfigFixture();
        var original = File.ReadAllText(fixture.Path);
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        AutomationException error;
        // 不授予删除共享，模拟配置正在被其他调用方使用，阻止原子替换。
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            error = Assert.Throws<AutomationException>(() => fixture.Config.AutoBossCompletedRunCount++);
        }

        Assert.Equal("configuration_save_failed", error.Code);
        Assert.Equal(original, File.ReadAllText(fixture.Path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.tmp"));
        Assert.Same(error, Assert.Throws<AutomationException>(store.ThrowIfFailed));
        var viewModel = UninitializedViewModel();
        SetField(viewModel, "_automationConfigStore", store);
        Assert.Same(error, Assert.Throws<AutomationException>(() => viewModel.WriteConfig(fixture.Config)));
        using var scope = new AutomationExecutionScope(new CancellationContext(), CancellationToken.None, _ => { });
        scope.RecordException(error);
        Assert.Single(scope.Failures);
        Assert.False(scope.IsCancellationRequested);
    }

    [Fact]
    public void RenamedConfig_IsNotRecreatedOrSavedUnderDisplayName()
    {
        using var fixture = new ConfigFixture();
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        var renamed = fixture.Path + ".renamed";
        File.Move(fixture.Path, renamed);

        Assert.Throws<AutomationException>(() => fixture.Config.AutoBossCompletedRunCount++);

        Assert.False(File.Exists(fixture.Path));
        Assert.Equal(1, JsonConvert.DeserializeObject<OneDragonFlowConfig>(File.ReadAllText(renamed))!.AutoBossCompletedRunCount);
        Assert.Single(Directory.EnumerateFiles(fixture.Directory));
    }

    [Fact]
    public async Task CliValidationFailure_UnsubscribesPersistenceBeforeReturning()
    {
        using var fixture = new ConfigFixture();
        // 从真实 CLI 入口在执行任何任务之前失败，不构造 WPF App 或访问游戏。
        var viewModel = UninitializedViewModel();
        SetField(viewModel, "_taskList", new ObservableCollection<OneDragonTaskItem>());
        SetField(viewModel, "_scriptGroupsdefault", new ObservableCollection<ScriptGroup>());
        SetField(viewModel, "_scriptGroupPath", fixture.Directory);

        var error = await Assert.ThrowsAsync<AutomationException>(() => viewModel.RunAutomationAsync(fixture.Config, fixture.Path));

        Assert.Equal("invalid_configuration", error.Code);
        fixture.Config.AutoBossCompletedRunCount++;
        Assert.Equal(1, fixture.Read().AutoBossCompletedRunCount);
        Assert.Null(typeof(OneDragonFlowViewModel).GetField("_automationConfigStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
    }

    [Fact]
    public async Task GuiAndCliSaves_PreserveUserEditsAndRefreshLoadedRewardCount()
    {
        using var fixture = new ConfigFixture();
        var gui = fixture.Read();
        TrackAutosavingGui(gui, fixture.Path);
        using (var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path))
        {
            gui.AutoBossName = "GUI 修改的首领";
            await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++);
            Assert.Equal("GUI 修改的首领", fixture.Read().AutoBossName);
            Assert.Equal(2, gui.AutoBossCompletedRunCount);
            gui.PartyName = "GUI 新队伍";
            Assert.Equal(2, fixture.Read().AutoBossCompletedRunCount);
            await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++);
        }
        gui.AutoBossTimeout = 300; // CLI 完成后，GUI 的旧对象也不能把次数覆盖回去。
        var saved = fixture.Read();
        Assert.Equal(3, saved.AutoBossCompletedRunCount);
        Assert.Equal("GUI 修改的首领", saved.AutoBossName);
        Assert.Equal("GUI 新队伍", saved.PartyName);
        Assert.Equal(300, saved.AutoBossTimeout);
    }

    [Fact]
    public async Task DelayedGuiRefresh_StillPreservesCountsAndReadsLatestValue()
    {
        using var fixture = new ConfigFixture();
        var gui = fixture.Read();
        var queue = new ConcurrentQueue<Action>();
        TrackAutosavingGui(gui, fixture.Path, queue.Enqueue);
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, gui.AutoBossCompletedRunCount); // UI 通知尚未执行。

        gui.PartyName = "通知到达之前修改队伍";
        Assert.Equal(2, fixture.Read().AutoBossCompletedRunCount);
        await Task.Run(() => fixture.Config.AutoBossCompletedRunCount++).WaitAsync(TimeSpan.FromSeconds(5));
        while (queue.TryDequeue(out var refresh)) refresh();

        Assert.Equal(3, gui.AutoBossCompletedRunCount);
        Assert.Equal(3, fixture.Read().AutoBossCompletedRunCount);
        Assert.Equal("通知到达之前修改队伍", fixture.Read().PartyName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitGuiCountReset_IsPreservedInsteadOfBeingClampedToOldSnapshot(bool cliStillRunning)
    {
        using var fixture = new ConfigFixture();
        var gui = fixture.Read();
        TrackAutosavingGui(gui, fixture.Path);
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        fixture.Config.AutoBossCompletedRunCount++;
        if (!cliStillRunning) store.Dispose();

        gui.AutoBossCompletedRunCount = 0;
        Assert.Equal(0, fixture.Read().AutoBossCompletedRunCount);
        if (cliStillRunning)
        {
            fixture.Config.AutoBossCompletedRunCount++;
            Assert.Equal(1, fixture.Read().AutoBossCompletedRunCount);
            Assert.Equal(1, gui.AutoBossCompletedRunCount);
            Assert.Equal(1, fixture.Config.AutoBossCompletedRunCount);
        }
    }

    [Fact]
    public void MultipleLoadedGuiSnapshots_OnlyMergeChangedFields()
    {
        using var fixture = new ConfigFixture();
        var first = fixture.Read();
        var second = fixture.Read();
        TrackAutosavingGui(first, fixture.Path);
        TrackAutosavingGui(second, fixture.Path);
        first.PartyName = "第一个窗口改的队伍";
        second.AutoBossName = "第二个窗口改的首领";
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        fixture.Config.AutoBossCompletedRunCount++;

        var saved = fixture.Read();
        Assert.Equal("第一个窗口改的队伍", saved.PartyName);
        Assert.Equal("第二个窗口改的首领", saved.AutoBossName);
        Assert.Equal(2, saved.AutoBossCompletedRunCount);
        Assert.Equal(2, first.AutoBossCompletedRunCount);
        Assert.Equal(2, second.AutoBossCompletedRunCount);
    }

    [Fact]
    public void CliCountMerge_PreservesUnknownFieldsAndCaseInsensitiveCount()
    {
        using var fixture = new ConfigFixture();
        var json = JObject.Parse(File.ReadAllText(fixture.Path));
        json.Property(nameof(OneDragonFlowConfig.AutoBossCompletedRunCount))!.Remove();
        json["autoBossCompletedRunCount"] = 1;
        const string timestamp = "2026-10-14T08:09:10.1234000+08:00";
        json["userExtension"] = new JObject { ["timestamp"] = timestamp };
        File.WriteAllText(fixture.Path, json.ToString());
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);

        fixture.Config.AutoBossCompletedRunCount++;

        var saved = AutomationJson.Deserialize<JObject>(File.ReadAllText(fixture.Path));
        Assert.Equal(2, saved.Value<int>("autoBossCompletedRunCount"));
        Assert.Null(saved[nameof(OneDragonFlowConfig.AutoBossCompletedRunCount)]);
        Assert.Equal(timestamp, saved["userExtension"]!.Value<string>("timestamp"));
    }

    [Fact]
    public void GuiNextTaskEdit_IsNotClearedByCliSnapshot()
    {
        using var fixture = new ConfigFixture();
        fixture.Config.NextTaskId = "旧的标记";
        File.WriteAllText(fixture.Path, JsonConvert.SerializeObject(fixture.Config));
        var gui = fixture.Read();
        TrackAutosavingGui(gui, fixture.Path);
        using var store = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        gui.NextTaskId = "GUI 刚设置的新标记";

        fixture.Config.NextTaskId = "";
        store.Save();

        Assert.Equal("GUI 刚设置的新标记", fixture.Read().NextTaskId);
    }

    [Fact]
    public async Task ConcurrentCountWriters_MergeEveryIncrement()
    {
        using var fixture = new ConfigFixture();
        var second = fixture.Read();
        using var firstStore = new AutomationDragonConfigStore(fixture.Config, fixture.Path);
        using var secondStore = new AutomationDragonConfigStore(second, fixture.Path);

        await Task.WhenAll(Task.Run(() =>
        {
            for (var i = 0; i < 10; i++) fixture.Config.AutoBossCompletedRunCount++;
        }), Task.Run(() =>
        {
            for (var i = 0; i < 10; i++) second.AutoBossCompletedRunCount++;
        })).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(21, fixture.Read().AutoBossCompletedRunCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.tmp"));
    }

    private static void TrackAutosavingGui(OneDragonFlowConfig config, string path, Action<Action>? dispatch = null)
    {
        OneDragonConfigFile.TrackGui(config, path, dispatch);
        config.PropertyChanged += (_, _) =>
        {
            if (!OneDragonConfigFile.IsRefreshing(config)) OneDragonConfigFile.SaveGui(config, path);
        };
    }

    private static OneDragonFlowViewModel UninitializedViewModel() =>
        (OneDragonFlowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(OneDragonFlowViewModel));

    private static void SetField(OneDragonFlowViewModel viewModel, string name, object value) =>
        typeof(OneDragonFlowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, value);

    private sealed class ConfigFixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BetterGI-Dragon-Config-Tests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "实际读取文件.json");
        internal OneDragonFlowConfig Config { get; } = new()
        {
            Name = "配置展示名", AutoBossSpecifyRunCount = true, AutoBossTotalRunCountLimit = 3, AutoBossCompletedRunCount = 1,
            TaskDefinitions = new() { ["boss-task"] = "自动首领讨伐" },
            TaskEnabledList = new() { ["boss-task"] = true }, TaskOrder = ["boss-task"]
        };

        internal ConfigFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path, JsonConvert.SerializeObject(Config));
        }

        internal OneDragonFlowConfig Read() => JsonConvert.DeserializeObject<OneDragonFlowConfig>(File.ReadAllText(Path))!;

        public void Dispose()
        {
            var intendedDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BetterGI-Dragon-Config-Tests"));
            var root = System.IO.Path.GetFullPath(Directory);
            if (!string.Equals(System.IO.Path.GetDirectoryName(root), intendedDirectory, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(System.IO.Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("拒绝删除测试临时目录之外的路径。");
            System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
