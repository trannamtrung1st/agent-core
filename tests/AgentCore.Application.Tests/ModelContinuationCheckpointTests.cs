using AgentCore.Domain.Conversation;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests;

public sealed class ModelContinuationCheckpointTests
{
    [Fact]
    public void Opaque_tool_continuation_survives_durable_checkpoint_without_affecting_arguments()
    {
        var call = new ModelToolCall("call-1", "lookup", "{}", "opaque-adapter-continuation");
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [call])];
        var checkpoint = AgentRunToolCallCheckpoint.Write(messages);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(new AgentRunCheckpoint(checkpoint, 0, 0, 1000), out var restored));
        Assert.Equal(call, Assert.Single(Assert.Single(restored!).ToolCalls!));
    }
}
