using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Automation;

namespace BetterGenshinImpact.Service.Automation;

/// <summary>当前 BetterGI 进程的 CLI 桥接服务；启用状态不会跨进程保存。</summary>
public interface IAutomationBridgeService
{
    bool IsEnabled { get; }
    Task EnableAsync(CancellationToken cancellationToken = default);
    Task<AutomationBackendStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
