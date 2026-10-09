using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class BrowserContractCutoverTests
{
    [Fact]
    public void Historical_receipts_remain_readable_but_cannot_resume_browser_effects()
    {
        var call = new ModelToolCall("old-effect", ToolCatalog.BrowserClick, "{\"ref\":\"el_historical\"}");
        var document = JsonNode.Parse(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [call])]))!.AsObject();
        document.Remove("BrowserContractVersion");
        var historical = new AgentRunCheckpoint(document.ToJsonString(), 1, 0, 300000);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(historical, out var messages));
        Assert.Equal(call, Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(messages!)));
        var error = Assert.Throws<AgentCoreException>(() => AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(historical));
        Assert.Contains("cannot resume", error.Message);
        Assert.Equal(call.ArgumentsJson, Assert.Single(messages![0].ToolCalls!).ArgumentsJson);
    }

    [Fact]
    public void Current_browser_checkpoint_and_old_nonbrowser_checkpoint_remain_executable()
    {
        var current = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [new("now", ToolCatalog.BrowserClose, "{}")])]), 1, 0, 300000);
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(current);
        var document = JsonNode.Parse(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "safe", Name: ToolCatalog.WorkspaceRead)]))!.AsObject();
        document.Remove("BrowserContractVersion");
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(new(document.ToJsonString(), 1, 0, 300000));
    }

    [Theory]
    [InlineData("{\"Phase\":\"waiting-signal\"}")]
    [InlineData("{\"Phase\":\"model-turn\"}")]
    [InlineData("{not-model-checkpoint")]
    public void Contract_fence_does_not_parse_unrelated_or_empty_checkpoint_phases(string payload) =>
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(new(payload, 0, 0, 1000));

    [Fact]
    public void Unreadable_model_browser_checkpoint_fails_closed() =>
        Assert.Throws<AgentCoreException>(() => AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(
            new("{\"Phase\":\"model-turn\",\"Name\":\"browser.click\",", 0, 0, 1000)));

    [Fact]
    public void Old_pinned_instructions_are_rejected_before_building_a_model_context()
    {
        var historical = SampleDefinitions.Examiner with { SystemInstructions = "Use browser.find and its opaque ref as scopeRef." };
        Assert.Throws<AgentCoreException>(() => BrowserContractCutover.EnsureCurrent(historical));
        BrowserContractCutover.EnsureCurrent(SampleDefinitions.Examiner);
    }
}
