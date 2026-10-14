using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>GUI 与 CLI 共用文件锁；各自只合并从读取快照之后发生的修改。</summary>
internal static class OneDragonConfigFile
{
    private const string CountProperty = nameof(OneDragonFlowConfig.AutoBossCompletedRunCount);
    private static readonly ConcurrentDictionary<string, FileState> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<OneDragonFlowConfig, GuiState> GuiStates = new();

    private sealed class FileState
    {
        internal readonly object Sync = new();
        internal readonly List<WeakReference<OneDragonFlowConfig>> GuiConfigs = [];
    }

    private sealed class GuiState(string path, JObject snapshot, Action<Action> dispatch)
    {
        internal string Path = path;
        internal JObject Snapshot = snapshot;
        internal bool LoadedFromDisk = File.Exists(path);
        internal volatile bool Refreshing;
        internal readonly Action<Action> Dispatch = dispatch;
    }

    internal static void TrackGui(OneDragonFlowConfig config, string path, Action<Action>? dispatch = null)
    {
        path = Path.GetFullPath(path);
        if (GuiStates.TryGetValue(config, out _)) return;
        var state = GuiStates.GetValue(config, _ => new GuiState(path, Snapshot(config), dispatch ?? GuiDispatcher()));
        var file = Files.GetOrAdd(state.Path, _ => new FileState());
        lock (file.Sync) file.GuiConfigs.Add(new(config));
    }

    internal static bool IsRefreshing(OneDragonFlowConfig config) =>
        GuiStates.TryGetValue(config, out var state) && state.Refreshing;

    internal static void SaveGui(OneDragonFlowConfig config, string defaultPath)
    {
        TrackGui(config, defaultPath);
        var state = GuiStates.GetValue(config, _ => throw new InvalidOperationException());
        // 已加载配置保持实际文件路径；GUI 重命名则使用新的名称路径。
        var path = state.Snapshot.Value<string>(nameof(OneDragonFlowConfig.Name)) == config.Name
            ? state.Path : Path.GetFullPath(defaultPath);
        var file = Files.GetOrAdd(path, _ => new FileState());
        lock (file.Sync)
        {
            var current = Snapshot(config);
            var exists = File.Exists(path);
            if (!exists && state.LoadedFromDisk && state.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("一条龙配置文件已不存在。", path);
            var hasSource = exists || state.LoadedFromDisk;
            var merged = hasSource ? Read(exists ? path : state.Path) : (JObject)current.DeepClone();
            if (hasSource)
            {
                foreach (var property in current.Properties())
                    if (!JToken.DeepEquals(property.Value, state.Snapshot[property.Name]))
                        Set(merged, property.Name, property.Value.DeepClone());
            }
            var count = ReadCount(merged);
            WriteAtomic(path, merged, exists);
            if (!state.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) file.GuiConfigs.Add(new(config));
            state.Path = path;
            state.LoadedFromDisk = true;
            state.Snapshot = current;
            RefreshCount(config, state, count);
        }
        RefreshLoadedGui(path);
    }

    internal static int SaveCli(string path, int countDelta, string expectedNextTaskId, string? nextTaskId)
    {
        if (countDelta < 0) throw new InvalidDataException("CLI 累计次数不能回退。");
        path = Path.GetFullPath(path);
        var file = Files.GetOrAdd(path, _ => new FileState());
        int count;
        lock (file.Sync)
        {
            var merged = Read(path); // 不使用 CLI 的独立配置快照覆盖用户设置。
            count = checked(ReadCount(merged) + countDelta);
            Set(merged, CountProperty, count);
            var updateNext = nextTaskId is not null
                && (merged.GetValue(nameof(OneDragonFlowConfig.NextTaskId), StringComparison.OrdinalIgnoreCase)?.Value<string>() ?? "") == expectedNextTaskId;
            if (updateNext) Set(merged, nameof(OneDragonFlowConfig.NextTaskId), nextTaskId!);
            if (countDelta != 0 || updateNext) WriteAtomic(path, merged, exists: true);
        }
        RefreshLoadedGui(path);
        return count;
    }

    private static void RefreshLoadedGui(string path)
    {
        var file = Files.GetOrAdd(path, _ => new FileState());
        OneDragonFlowConfig[] configs;
        lock (file.Sync)
        {
            file.GuiConfigs.RemoveAll(reference => !reference.TryGetTarget(out _));
            configs = file.GuiConfigs.Select(reference => reference.TryGetTarget(out var config) ? config : null)
                .Where(config => config is not null).Cast<OneDragonFlowConfig>().ToArray();
        }
        foreach (var config in configs)
        {
            if (!GuiStates.TryGetValue(config, out var state)) continue;
            void Refresh()
            {
                lock (file.Sync)
                {
                    if (!state.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
                    // 队列里只读最新值，迟到的 UI 通知不能把计数倒退。
                    try { RefreshCount(config, state, ReadCount(Read(path))); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
                }
            }
            // 不持有文件锁等待 Dispatcher，避免 GUI 保存与后台领奖之间死锁。
            state.Dispatch(Refresh);
        }
    }

    private static Action<Action> GuiDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return action =>
        {
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished) dispatcher.BeginInvoke(action);
        };
    }

    private static void RefreshCount(OneDragonFlowConfig config, GuiState state, int count)
    {
        state.Refreshing = true;
        try
        {
            state.Snapshot[CountProperty] = count;
            config.AutoBossCompletedRunCount = count;
        }
        finally { state.Refreshing = false; }
    }

    private static JObject Snapshot(OneDragonFlowConfig config) => JObject.FromObject(config);
    private static JObject Read(string path)
    {
        var json = AutomationJson.Deserialize<JObject>(File.ReadAllText(path));
        if (json.Properties().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("一条龙配置包含大小写不同的重复字段。");
        return json;
    }

    private static void Set(JObject json, string name, JToken value)
    {
        var property = json.Properties().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null) json[name] = value;
        else property.Value = value;
    }

    private static int ReadCount(JObject json)
    {
        var token = json.GetValue(CountProperty, StringComparison.OrdinalIgnoreCase);
        if (token is null) return 0;
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new InvalidDataException("一条龙累计次数无效。");
        return count;
    }

    private static void WriteAtomic(string path, JObject json, bool exists)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new UTF8Encoding(false).GetBytes(json.ToString(Formatting.Indented));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (exists) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
