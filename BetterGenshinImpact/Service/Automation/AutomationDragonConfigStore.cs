using System;
using System.ComponentModel;
using System.IO;
using BetterGenshinImpact.Automation;
using BetterGenshinImpact.Core.Config;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>CLI 配置在领奖回调返回之前落盘；不依赖 UI 的属性订阅或 Dispatcher 队列。</summary>
internal sealed class AutomationDragonConfigStore : IDisposable
{
    private readonly object _sync = new();
    private readonly OneDragonFlowConfig _config;
    private readonly string _path;
    private AutomationException? _failure;
    private bool _disposed;
    private bool _refreshing;
    private int _savedCount;
    private string _savedNextTaskId;

    internal AutomationDragonConfigStore(OneDragonFlowConfig config, string path)
    {
        _config = config;
        _path = Path.GetFullPath(path);
        if (!File.Exists(_path)) throw new FileNotFoundException("一条龙配置文件已不存在。", _path);
        _savedCount = config.AutoBossCompletedRunCount;
        _savedNextTaskId = config.NextTaskId;
        _config.PropertyChanged += ConfigPropertyChanged;
    }

    private void ConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_refreshing && e.PropertyName == nameof(OneDragonFlowConfig.AutoBossCompletedRunCount)) Save();
    }

    internal void Save()
    {
        lock (_sync)
        {
            // Dispose 已解除订阅；并发到达的旧事件也不再保存。
            if (_disposed) return;
            ThrowIfFailed();
            try
            {
                var nextTaskId = _config.NextTaskId != _savedNextTaskId ? _config.NextTaskId : null;
                _refreshing = true;
                try
                {
                    var count = OneDragonConfigFile.SaveCli(_path, _config.AutoBossCompletedRunCount - _savedCount,
                        _savedNextTaskId, nextTaskId);
                    _savedCount = count;
                    _savedNextTaskId = _config.NextTaskId;
                    _config.AutoBossCompletedRunCount = count;
                }
                finally { _refreshing = false; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or OverflowException)
            {
                _failure = new AutomationException("configuration_save_failed",
                    $"无法保存一条龙配置 {Path.GetFileName(_path)}：{exception.Message}。请核对累计次数后再运行。");
                throw _failure;
            }
        }
    }

    internal void ThrowIfFailed()
    {
        lock (_sync)
        {
            if (_failure is not null) throw _failure;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _config.PropertyChanged -= ConfigPropertyChanged;
        }
    }
}
