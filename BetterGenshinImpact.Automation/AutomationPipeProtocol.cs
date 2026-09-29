using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace BetterGenshinImpact.Automation;

public static class AutomationPipeNames
{
    public static string ForUserSession(string sid, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 180 || sid.Any(c => !(char.IsAsciiDigit(c) || c is 'S' or '-')) || sessionId < 0)
            throw new ArgumentException("Windows SID 或 Session ID 无效。");
        return $"BetterGI.Automation.v1.{sid}.session-{sessionId}";
    }
}

/// <summary>独立于实例 IPC v2 的有界 UTF-8 JSON 协议：4 字节 little-endian 长度 + JSON。</summary>
public static class AutomationPipeProtocol
{
    public const int MaxPayloadBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = Utf8.GetBytes(AutomationJson.Serialize(value!));
        if (payload.Length is <= 0 or > MaxPayloadBytes) throw new InvalidDataException("自动化消息超过 1 MiB 限制。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPayloadBytes) throw new InvalidDataException("自动化消息长度无效。");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return AutomationJson.Deserialize<T>(Utf8.GetString(payload));
    }
}

public sealed class AutomationPipeClient(string pipeName, TimeSpan timeout)
{
    public async Task<AutomationResponse> SendAsync(AutomationRequest request, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            await AutomationPipeProtocol.WriteAsync(pipe, request, timeoutSource.Token).ConfigureAwait(false);
            var response = await AutomationPipeProtocol.ReadAsync<AutomationResponse>(pipe, timeoutSource.Token).ConfigureAwait(false);
            if (response.RequestId != request.RequestId || response.ApiVersion != 1)
                throw new AutomationException("protocol_mismatch", "自动化响应的版本或请求 ID 不匹配。");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接或请求超时；任务可能已被接受，请使用原 request-id 查询。");
        }
    }
}
