namespace BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception; // TODO: change this namespace to BetterGenshinImpact.GameTask.Common.Exception

// 默认表示正常提前结束；取消由任务令牌判定。前置条件不满足等失败必须显式标记。
public class NormalEndException(string message) : System.Exception(message)
{
    public NormalEndException(string message, bool isFailure) : this(message)
    {
        IsFailure = isFailure;
    }

    public bool IsFailure { get; }
}
