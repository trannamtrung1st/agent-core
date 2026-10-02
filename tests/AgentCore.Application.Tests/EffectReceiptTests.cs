using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class EffectReceiptTests
{
    [Fact]
    public void Browser_close_success_is_a_runtime_receipt()
    {
        Assert.True(EffectReceipts.TryFromToolResult(
            ToolCatalog.BrowserClose,
            """{"status":"closed"}""",
            out var receipt));
        Assert.Equal("Browser closed", receipt.Label);
        Assert.Equal("closed", receipt.Status);
    }

    [Fact]
    public void Browser_close_failure_and_read_tools_are_not_receipts()
    {
        Assert.False(EffectReceipts.TryFromToolResult(
            ToolCatalog.BrowserClose,
            """{"error":"provider_unavailable"}""",
            out _));
        Assert.False(EffectReceipts.TryFromToolResult(
            ToolCatalog.BrowserNavigate,
            """{"url":"https://example.test"}""",
            out _));
    }

    [Fact]
    public void Effect_receipts_round_trip_beside_an_empty_display()
    {
        var envelope = new ResponseEnvelope(
            string.Empty,
            null,
            [],
            ResponseSpeechMode.None,
            EffectReceipts: [new EffectReceipt(ToolCatalog.BrowserClose, "closed", "Browser closed")]);
        var loaded = ResponseEnvelopeJson.Deserialize(ResponseEnvelopeJson.Serialize(envelope));
        var receipt = Assert.Single(loaded!.EffectReceipts!);
        Assert.Equal("Browser closed", receipt.Label);
        Assert.Equal(string.Empty, loaded.DisplayText);
    }
}
