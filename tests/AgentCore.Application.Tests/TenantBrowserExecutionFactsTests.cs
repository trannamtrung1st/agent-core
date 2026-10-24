using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class TenantBrowserExecutionFactsTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Only_matching_settled_nonreplayable_browser_receipts_contribute_bounded_registered_outcomes(bool matching, bool settled, bool replay)
    {
        var call = new ModelToolCall("exact-close", ToolCatalog.RequestAuthorization,
            """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{}}}""");
        var receipt = JsonSerializer.Serialize(new {
            approvalStatus = "approved", outcome = settled ? "completed" : "indeterminate", commandCompleted = settled, replayAllowed = replay,
            browser = new { status = "closed", effectAttempted = true, effectConfirmedBySdk = true,
                observation = new { url = "https://private.test/secret", content = "PRIVATE-PAGE-WITNESS" } }
        });
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, receipt, ToolCallId: matching ? call.Id : "another-call", Name: call.Name)];
        var facts = RunExecutionFacts.Current(Guid.NewGuid(), [], messages);
        using var json = JsonDocument.Parse(facts[facts.IndexOf('{')..]);
        var outcomes = json.RootElement.GetProperty("toolOutcomes").EnumerateArray().ToArray();
        if (matching && settled && !replay)
        {
            var outcome = Assert.Single(outcomes); Assert.Equal(ToolCatalog.BrowserClose, outcome.GetProperty("tool").GetString());
            Assert.Equal("succeeded", outcome.GetProperty("outcome").GetString()); Assert.Equal(1, outcome.GetProperty("count").GetInt32());
        }
        else Assert.Empty(outcomes);
        Assert.DoesNotContain("private.test", facts); Assert.DoesNotContain("PRIVATE-PAGE-WITNESS", facts);
        Assert.DoesNotContain("sign-out confirmed", facts);
    }
}
