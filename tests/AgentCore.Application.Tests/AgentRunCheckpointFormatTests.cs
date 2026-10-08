using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class AgentRunCheckpointFormatTests
{
    [Fact]
    public void Capability_state_survives_compacted_results_and_legacy_defaults_to_empty()
    {
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "{\"truncated\":true}", ToolCallId: "load", Name: ToolCatalog.CapabilitiesLoad)],
            loadedCapabilityIds: [ToolCatalog.EmailSearch], capabilityLoadCount: 3);
        var state = AgentRunToolCallCheckpoint.ReadCapabilityState(new(payload, 1, 0, 1000));
        Assert.Equal([ToolCatalog.EmailSearch], state.Ids);
        Assert.Equal(3, state.Calls);
        var legacy = AgentRunToolCallCheckpoint.ReadCapabilityState(new(AgentRunToolCallCheckpoint.Write([]), 0, 0, 1000));
        Assert.Empty(legacy.Ids);
        Assert.Equal(0, legacy.Calls);
        Assert.False(AgentRunToolCallCheckpoint.TryWrite([new(ModelRole.User, new string('x', AgentRunLimits.MaxCheckpointBytes))], false, null, out _, state.Ids, state.Calls));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void Capability_checkpoint_rejects_invalid_load_counts(int calls)
    {
        var payload = AgentRunToolCallCheckpoint.Write([], loadedCapabilityIds: [ToolCatalog.EmailSearch], capabilityLoadCount: calls);
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => AgentRunToolCallCheckpoint.ReadCapabilityState(new(payload, 0, 0, 1000)));
    }

    [Fact]
    public void PendingCalls_returns_unanswered_calls_from_latest_assistant_batch()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
            new(ModelRole.Tool, """{"ok":true}""", ToolCallId: "a", Name: "http.request"),
        };

        var pending = AgentRunToolCallCheckpoint.PendingCalls(messages);
        Assert.Single(pending);
        Assert.Equal("b", pending[0].Id);
    }

    [Fact]
    public void ReservedToolSteps_sums_assistant_tool_call_batches()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
            new(ModelRole.Tool, "{}", ToolCallId: "a", Name: "http.request"),
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("c")]),
        };

        Assert.Equal(3, AgentRunToolCallCheckpoint.ReservedToolSteps(messages));
    }

    [Fact]
    public void NormalizeResumedStepCount_raises_legacy_undercount_to_assistant_batch_size()
    {
        var messages = new List<ModelMessage>
        {
            new(
                ModelRole.Assistant,
                string.Empty,
                ToolCalls: Enumerable.Range(1, 12)
                    .Select(index => Call($"h{index}"))
                    .ToArray()),
        };

        Assert.Equal(12, AgentRunToolCallCheckpoint.NormalizeResumedStepCount(1, messages));
    }

    [Fact]
    public void PendingCalls_returns_every_call_when_assistant_batch_is_unanswered()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
        };

        var pending = AgentRunToolCallCheckpoint.PendingCalls(messages);
        Assert.Equal(["a", "b"], pending.Select(call => call.Id));
    }

    [Fact]
    public void Write_keeps_capture_text_and_drops_image_bytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x53, 0x45, 0x43, 0x52, 0x45, 0x54 };
        var payload = AgentRunToolCallCheckpoint.Write(
        [
            new ModelMessage(
                ModelRole.Tool,
                """{"artifactId":"abc","byteSize":10}""",
                Parts: [new ModelImageContent("image/png", png, "capture.png")],
                ToolCallId: "cap",
                Name: ToolCatalog.BrowserCapture)
        ]);

        Assert.Contains("artifactId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(png), payload, StringComparison.Ordinal);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(new AgentRunCheckpoint(payload, 0, 0, 1), out var messages));
        Assert.Null(Assert.Single(messages!).Parts);
    }

    private static ModelToolCall Call(string id) => new(id, "http.request", """{"method":"GET","url":"https://example.com"}""");
}
