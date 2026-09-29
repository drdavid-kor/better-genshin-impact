using System.Buffers.Binary;
using BetterGenshinImpact.Automation;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Automation.Tests;

public class AutomationProtocolTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1048577)]
    public async Task Frame_ShouldRejectInvalidLengthBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => AutomationPipeProtocol.ReadAsync<AutomationRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Frame_ShouldRoundTripUnicodeAndRejectTruncation()
    {
        var request = new AutomationRequest { Operation = "tasks.show", InstallationPath = "E:\\中文 目录" };
        using var stream = new MemoryStream();
        await AutomationPipeProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(request, await AutomationPipeProtocol.ReadAsync<AutomationRequest>(stream, CancellationToken.None));
        using var truncated = new MemoryStream(stream.ToArray()[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => AutomationPipeProtocol.ReadAsync<AutomationRequest>(truncated, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"operation\":\"doctor\",\"operation\":\"runs.start\"}")]
    [InlineData("{} {}")]
    public void Json_ShouldRejectAmbiguousDocuments(string json)
    {
        Assert.ThrowsAny<JsonException>(() => AutomationJson.Deserialize<AutomationRequest>(json));
    }

    [Fact]
    public async Task Client_ShouldBoundConnectionWait()
    {
        var client = new AutomationPipeClient("bgi-absent-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(new AutomationRequest { Operation = "doctor" }));
    }

    [Fact]
    public void Json_ShouldPreserveDateLikeParameterStringsExactly()
    {
        const string value = "2026-09-29T12:00:00.1200+08:00";
        var json = AutomationJson.ToObject(new { parameters = new { value } });
        var token = json["parameters"]!["value"]!;
        Assert.Equal(Newtonsoft.Json.Linq.JTokenType.String, token.Type);
        Assert.Equal(value, token.ToObject<string>());
    }
}
