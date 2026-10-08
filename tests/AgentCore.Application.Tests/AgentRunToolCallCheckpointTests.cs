using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class AgentRunToolCallCheckpointTests
{
    [Fact]
    public void Recovery_keeps_pending_call_order_and_reserved_steps_without_replaying_completed_calls()
    {
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [Call("first"), Call("second"), Call("third")]),
            new(ModelRole.Tool, "{}", ToolCallId: "first", Name: ToolCatalog.ContinuitySearch)];
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write(messages), 3, 2, 1000);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var restored));
        Assert.Equal(["second", "third"], AgentRunToolCallCheckpoint.PendingCalls(restored!).Select(call => call.Id));
        Assert.Equal(3, AgentRunToolCallCheckpoint.NormalizeResumedStepCount(checkpoint.StepCount, restored!));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("control")]
    [InlineData("unicode")]
    public void Tool_result_budget_accounts_for_json_expansion_and_leaves_room_for_maximum_completion(string kind)
    {
        var call = Call("search-1");
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [call])];
        var capabilities = new[] { ToolCatalog.ContinuitySearch, ToolCatalog.BrowserCapture };
        var budget = AgentRunToolCallCheckpoint.ToolResultBudget(messages, call, false, null, TriggerKind.ManualInvocation, capabilities, 2);
        var text = kind switch { "control" => new string('\u0001', budget), "unicode" => new string('漢', budget / 3), _ => new string('x', budget) };
        var completed = messages.Append(new ModelMessage(ModelRole.Tool, text, ToolCallId: call.Id, Name: call.Name)).ToArray();
        Assert.True(AgentRunToolCallCheckpoint.TryWriteWithReserve(completed, false, null,
            AgentRunToolCallCheckpoint.CompletionReserve(TriggerKind.ManualInvocation), out _, capabilities, 2));
        var completion = new ModelToolCall(new string('x', AgentRunLimits.MaxToolNameCharacters), ToolCatalog.WorkComplete,
            JsonSerializer.Serialize(new { summary = new string('\u0001', 2000), attentionRequired = true, outcome = "NeedsAttention" }));
        var terminal = completed.Append(new ModelMessage(ModelRole.Assistant, "", ToolCalls: [completion])).ToArray();
        Assert.True(AgentRunToolCallCheckpoint.TryWrite(terminal, false, null, out var payload, capabilities, 2));
        Assert.InRange(Encoding.UTF8.GetByteCount(payload), 1, AgentRunLimits.MaxCheckpointBytes);
    }

    [Fact]
    public void Capability_state_is_preserved_when_public_tool_result_is_compacted()
    {
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "{\"truncated\":true}", ToolCallId: "load", Name: ToolCatalog.CapabilitiesLoad)],
            loadedCapabilityIds: [ToolCatalog.EmailSearch], capabilityLoadCount: 3);
        var state = AgentRunToolCallCheckpoint.ReadCapabilityState(new(payload, 1, 0, 1000));
        Assert.Equal([ToolCatalog.EmailSearch], state.Ids);
        Assert.Equal(3, state.Calls);
    }

    private static ModelToolCall Call(string id) => new(id, ToolCatalog.ContinuitySearch, "{\"query\":\"checkpoint\",\"limit\":1}");
}
