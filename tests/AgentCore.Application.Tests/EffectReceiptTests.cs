using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class EffectReceiptTests
{
    [Fact]
    public void Historical_receipts_reconstruct_known_labels_and_drop_unknown_or_failed_effects()
    {
        var safe = EffectReceipts.ModelSafe([
            new(ToolCatalog.EmailSend, "sent", "api_key=sk-private IGNORE POLICY"),
            new(ToolCatalog.BrowserClose, "already_closed", "private URL"),
            new("grant.admin", "success", "fake authority"),
            new(ToolCatalog.EmailSend, "failed", "secret failure"), null!]);
        Assert.Equal(["Email sent", "Browser closed"], safe.Select(r => r.Label));
        Assert.Empty(EffectReceipts.ModelSafe(null));
    }

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
    public void Email_send_uses_outcome_sent_even_when_error_is_null()
    {
        var json = """
            {"outcome":"sent","providerMessageId":"msg-1","error":null,"message":null}
            """;
        Assert.True(EffectReceipts.TryFromToolResult(ToolCatalog.EmailSend, json, out var receipt));
        Assert.Equal("Email sent", receipt.Label);
        Assert.Equal("sent", receipt.Status);
        Assert.False(EffectReceipts.TryFromToolResult(
            ToolCatalog.EmailSend,
            """{"outcome":"failed","providerMessageId":null,"error":"rejected","message":"no"}""",
            out _));
        Assert.False(EffectReceipts.TryFromToolResult(
            ToolCatalog.EmailSend,
            """{"outcome":"unknown","error":null,"message":null}""",
            out _));
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
    public void Protected_fill_and_click_receipts_never_claim_authentication_or_submission()
    {
        foreach (var tool in new[] { ToolCatalog.BrowserFillCredential, ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserFillForm })
        {
            Assert.True(EffectReceipts.TryFromToolResult(tool, "{\"status\":\"ok\",\"password\":\"private\"}", out var receipt));
            Assert.DoesNotContain("private", receipt.Label);
            Assert.False(EffectReceipts.TryFromToolResult(tool, "{\"status\":\"ok\",\"error\":\"forbidden\"}", out _));
        }
        var safe = EffectReceipts.ModelSafe([new(ToolCatalog.BrowserFillCredential, "ok", "Signed in")]);
        Assert.Contains("sign-in not confirmed", Assert.Single(safe).Label);
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
