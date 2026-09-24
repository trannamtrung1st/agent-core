using AgentCore.Application.Ports;
using AgentCore.Application.Work;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Tests;

public sealed class DurableToolCallCheckpointTests
{
    [Fact]
    public void PendingCalls_returns_unanswered_calls_from_latest_assistant_batch()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
            new(ModelRole.Tool, """{"ok":true}""", ToolCallId: "a", Name: "http.request"),
        };

        var pending = DurableToolCallCheckpoint.PendingCalls(messages);
        Assert.Single(pending);
        Assert.Equal("b", pending[0].Id);
    }

    [Fact]
    public void PendingCalls_returns_every_call_when_assistant_batch_is_unanswered()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
        };

        var pending = DurableToolCallCheckpoint.PendingCalls(messages);
        Assert.Equal(["a", "b"], pending.Select(call => call.Id));
    }

    [Fact]
    public void TryResolveLegacyToolCallId_binds_prepared_approval_from_checkpoint()
    {
        var checkpoint = new WorkCheckpoint(
            DurableToolCallCheckpoint.Write(
            [
                new(ModelRole.Assistant, string.Empty, ToolCalls:
                [
                    new ModelToolCall(
                        "h1",
                        "http.request",
                        """{"method":"POST","url":"https://example.com/items","body":"SECRET"}""")
                ])
            ]),
            0,
            0,
            1000);
        var approval = new WorkApproval(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "http.request",
            """{"body":"SECRET"}""",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "POST https://example.com/items",
            DateTimeOffset.UtcNow.AddHours(1),
            WorkApprovalDecision.Pending,
            null,
            false,
            1,
            DateTimeOffset.UtcNow);

        var resolved = DurableToolCallCheckpoint.TryResolveLegacyToolCallId(
            checkpoint,
            WorkSideEffectDisposition.Prepared,
            approval.ActionHash,
            approval);

        Assert.Equal("h1", resolved);
    }

    private static ModelToolCall Call(string id) => new(id, "http.request", """{"method":"GET","url":"https://example.com"}""");
}
