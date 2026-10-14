using System;
using System.Threading.Tasks;
using BetterGenshinImpact.ViewModel.Pages.OneDragon;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Service.Automation;

namespace BetterGenshinImpact.Model;

public partial class OneDragonTaskItem : ObservableObject
{
    [ObservableProperty] private string _name;

    [ObservableProperty] private string _id = Guid.NewGuid().ToString();

    [ObservableProperty] private Brush _statusColor = Brushes.Gray;

    [ObservableProperty] private bool _isEnabled = true;

    [ObservableProperty] private bool _isNextTask = false;

    [ObservableProperty] private OneDragonBaseViewModel? _viewModel;

    public Func<Task>? Action { get; private set; }

    public OneDragonTaskItem(string name)
    {
        Name = name;
    }

    public OneDragonTaskItem(string name, string id) : this(name)
    {
        _id = id;
    }

    // public OneDragonTaskItem(Type viewModelType, Func<Task> action)
    // {
    //     ViewModel = App.GetService(viewModelType) as OneDragonBaseViewModel;
    //     if (ViewModel == null)
    //     {
    //         throw new ArgumentException("Invalid view model type", nameof(viewModelType));
    //     }
    //     Name = ViewModel.Title;
    //     Action = action;
    // }

    public void InitAction(OneDragonFlowConfig config)
    {
        if (config.TaskEnabledList.ContainsKey(Id))
        {
            config.TaskEnabledList[Id] = IsEnabled;
        }
        else
        {
            config.TaskEnabledList.Add(Id, IsEnabled);
            config.TaskDefinitions[Id] = Name;
        }

        switch (Name)
        {
            case "领取邮件":
                Action = async () =>
                {
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new ClaimMailRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "合成树脂":
                Action = async () =>
                {
                    AutomationExecutionScope.Current?.RecordExecution();
                    try
                    {
                        await new GoToCraftingBenchTask().GoCraftResin(config.CraftingBenchCountry,
                            CancellationContext.Instance.Cts.Token);
                    }
                    catch (Exception e)
                    {
                        TaskControl.Logger.LogError("合成树脂执行异常：" + e.Message);
                    }
                };
                break;
            case "自动秘境":
                Action = async () =>
                {
                    if (string.IsNullOrEmpty(TaskContext.Instance().Config.AutoFightConfig.StrategyName))
                    {
                        TaskContext.Instance().Config.AutoFightConfig.StrategyName = "根据队伍自动选择";
                    }

                    var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
                    if (taskSettingsPageViewModel!.GetFightStrategy(out var path))
                    {
                        TaskControl.Logger.LogError("自动秘境战斗策略{Msg}，跳过", "未配置");
                        AutomationExecutionScope.Current?.RecordSkipped("自动秘境未配置战斗策略。");
                        return;
                    }

                    var (partyName, domainName, sundaySelectedValue) = config.GetDomainConfig();
                    if (string.IsNullOrEmpty(domainName))
                    {
                        TaskControl.Logger.LogError("一条龙配置内{Msg}需要刷的秘境，跳过", "未选择");
                        AutomationExecutionScope.Current?.RecordSkipped("一条龙未选择秘境。");
                        return;
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：执行");
                    }

                    var autoDomainParam = new AutoDomainParam(0, path)
                    {
                        PartyName = partyName,
                        DomainName = domainName,
                        SundaySelectedValue = sundaySelectedValue
                    };
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new AutoDomainTask(autoDomainParam).Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "自动首领讨伐":
                Action = async () =>
                {
                    if (RecordAutomationScheduledSkip(Name, config, true)) return;
                    var totalRunCountLimited = config.AutoBossSpecifyRunCount && config.AutoBossTotalRunCountLimit > 0;
                    var remainingTotalRunCount = config.AutoBossTotalRunCountLimit - config.AutoBossCompletedRunCount;
                    if (totalRunCountLimited && remainingTotalRunCount <= 0)
                    {
                        TaskControl.Logger.LogInformation(
                            "自动首领讨伐：累计成功次数已达到上限 {Limit}，跳过",
                            config.AutoBossTotalRunCountLimit);
                        return;
                    }

                    if (string.IsNullOrEmpty(config.AutoBossStrategyName))
                    {
                        config.AutoBossStrategyName = "根据队伍自动选择";
                    }

                    var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
                    if (taskSettingsPageViewModel!.GetFightStrategy(config.AutoBossStrategyName, out var path))
                    {
                        TaskControl.Logger.LogError("自动首领讨伐战斗策略{Msg}，跳过", "未配置");
                        AutomationExecutionScope.Current?.RecordSkipped("自动首领讨伐未配置战斗策略。");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(config.AutoBossName))
                    {
                        TaskControl.Logger.LogError("一条龙配置内{Msg}需要讨伐的首领，跳过", "未选择");
                        AutomationExecutionScope.Current?.RecordSkipped("一条龙未选择首领。");
                        return;
                    }

                    AutoBossParam param = AutoBossParam.CreateWithoutDefaultConfig(path);
                    param.BossName = config.AutoBossName;
                    param.StrategyName = config.AutoBossStrategyName;
                    param.TeamName = config.AutoBossTeamName;
                    param.SpecifyRunCount = config.AutoBossSpecifyRunCount;
                    param.RunCount = totalRunCountLimited
                        ? config.AutoBossSpecifyRunCount
                            ? Math.Min(config.AutoBossRunCount, remainingTotalRunCount)
                            : remainingTotalRunCount
                        : config.AutoBossRunCount;
                    param.UseTransientResin = config.AutoBossUseTransientResin;
                    param.UseFragileResin = config.AutoBossUseFragileResin;
                    param.ReviveRetryCount = config.AutoBossReviveRetryCount;
                    param.ReturnToStatueAfterEachRound = config.AutoBossReturnToStatueAfterEachRound;
                    param.RewardRecognitionEnabled = config.AutoBossRewardRecognitionEnabled;
                    param.RewardRecognitionEnabled = config.AutoBossRewardRecognitionEnabled;
                    param.Timeout = config.AutoBossTimeout;
                    if (totalRunCountLimited)
                    {
                        param.RewardClaimedCallback = () => config.AutoBossCompletedRunCount++;
                    }
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new AutoBossTask(param).Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "自动幽境危战":
                Action = async () =>
                {
                    if (string.IsNullOrEmpty(TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName))
                    {
                        TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName = "根据队伍自动选择";
                    }

                    var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
                    if (taskSettingsPageViewModel!.GetFightStrategy(TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName, out var path))
                    {
                        TaskControl.Logger.LogError("自动幽境危战战斗策略{Msg}，跳过", "未配置");
                        AutomationExecutionScope.Current?.RecordSkipped("自动幽境危战未配置战斗策略。");
                        return;
                    }

                    AutoStygianOnslaughtParam param = new AutoStygianOnslaughtParam();
                    param.SetAutoStygianOnslaughtConfig(TaskContext.Instance().Config.AutoStygianOnslaughtConfig);
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new AutoStygianOnslaughtTask(param, path).Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "领取每日奖励":
                Action = async () =>
                {
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new GoToAdventurersGuildTask().Start(config.AdventurersGuildCountry,
                        CancellationContext.Instance.Cts.Token, config.DailyRewardPartyName);
                    await new ClaimBattlePassRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "领取尘歌壶奖励":
                Action = async () =>
                {
                    AutomationExecutionScope.Current?.RecordExecution();
                    await new GoToSereniteaPotTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "自动地脉花":
                Action = async () =>
                {
                    var shouldRunToday = config.ShouldRunLeyLineToday();
                    if (RecordAutomationScheduledSkip(Name, config, shouldRunToday)) return;
                    if (!shouldRunToday)
                    {
                        TaskControl.Logger.LogInformation("自动地脉花未在运行日期内，跳过");
                        return;
                    }

                    var taskConfig = TaskContext.Instance().Config.AutoLeyLineOutcropConfig;
                    var originalType = taskConfig.LeyLineOutcropType;
                    var originalCountry = taskConfig.Country;
                    var originalCount = taskConfig.Count;
                    var originalExhaustionMode = taskConfig.IsResinExhaustionMode;
                    var originalOpenModeCountMin = taskConfig.OpenModeCountMin;
                    var (type, country) = config.GetLeyLineConfigForToday(taskConfig);

                    try
                    {
                        taskConfig.LeyLineOutcropType = type;
                        taskConfig.Country = country;
                        taskConfig.IsResinExhaustionMode = config.LeyLineResinExhaustionMode;
                        taskConfig.OpenModeCountMin = config.LeyLineOpenModeCountMin;
                        if (config.LeyLineRunCount > 0)
                        {
                            taskConfig.Count = config.LeyLineRunCount;
                        }

                        AutoLeyLineOutcropParam param = new AutoLeyLineOutcropParam();
                        param.SetAutoLeyLineOutcropConfig(taskConfig);
                        AutomationExecutionScope.Current?.RecordExecution();
                        await new AutoLeyLineOutcropTask(param, config.LeyLineOneDragonMode)
                            .Start(CancellationContext.Instance.Cts.Token);
                    }
                    finally
                    {
                        taskConfig.LeyLineOutcropType = originalType;
                        taskConfig.Country = originalCountry;
                        taskConfig.Count = originalCount;
                        taskConfig.IsResinExhaustionMode = originalExhaustionMode;
                        taskConfig.OpenModeCountMin = originalOpenModeCountMin;
                    }
                };
                break;
            default:
                Action = () => Task.CompletedTask;
                break;
        }
    }

    internal static bool RecordAutomationScheduledSkip(string taskName, OneDragonFlowConfig config, bool shouldRunLeyLineToday)
    {
        var scope = AutomationExecutionScope.Current;
        if (scope is null) return false;
        var reason = taskName switch
        {
            "自动首领讨伐" when config.AutoBossSpecifyRunCount && config.AutoBossTotalRunCountLimit > 0
                && config.AutoBossCompletedRunCount >= config.AutoBossTotalRunCountLimit => "自动首领讨伐累计成功次数已达到上限，跳过。",
            "自动地脉花" when !shouldRunLeyLineToday => "自动地脉花未在运行日期内，跳过。",
            _ => null
        };
        if (reason is null) return false;
        scope.RecordSkipped(reason);
        return true;
    }
}
