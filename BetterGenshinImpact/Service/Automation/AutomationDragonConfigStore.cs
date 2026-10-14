using System;
using System.ComponentModel;
using System.IO;
using System.Text;
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

    internal AutomationDragonConfigStore(OneDragonFlowConfig config, string path)
    {
        _config = config;
        _path = Path.GetFullPath(path);
        if (!File.Exists(_path)) throw new FileNotFoundException("一条龙配置文件已不存在。", _path);
        _config.PropertyChanged += ConfigPropertyChanged;
    }

    private void ConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OneDragonFlowConfig.AutoBossCompletedRunCount)) Save();
    }

    internal void Save()
    {
        lock (_sync)
        {
            // Dispose 已解除订阅；并发到达的旧事件也不再保存。
            if (_disposed) return;
            ThrowIfFailed();
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(_config, Formatting.Indented));
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                // 替换已有文件，拒绝复活运行期间被用户删除/改名的配置。
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _failure = new AutomationException("configuration_save_failed",
                    $"无法保存一条龙配置 {Path.GetFileName(_path)}：{exception.Message}。请核对累计次数后再运行。");
                throw _failure;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
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
