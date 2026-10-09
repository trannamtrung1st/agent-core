using System.Text.Json;
using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class BrowserSnapshotContractTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"depth\":1}", true)]
    [InlineData("{\"depth\":33}", false)]
    [InlineData("{\"waitFor\":\"stable\"}", false)]
    [InlineData("{\"depth\":1,\"selector\":\"body\"}", false)]
    [InlineData("{\"depth\":1,\"depth\":2}", false)]
    public void Snapshot_has_one_bounded_schema(string json, bool expected)
    {
        using var args = JsonDocument.Parse(json);
        Assert.Equal(expected, BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserSnapshot, args.RootElement, out _, out _));
    }
    [Theory]
    [InlineData(100, true)]
    [InlineData(5000, true)]
    [InlineData(99, false)]
    [InlineData(5001, false)]
    public void Native_wait_is_bounded(int timeout, bool expected)
    {
        var args = JsonSerializer.SerializeToElement(new { condition = "stable", timeoutMs = timeout });
        Assert.Equal(expected, BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserWait, args, out _, out _));
    }
}
