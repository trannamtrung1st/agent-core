using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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
    public void ReservedToolSteps_sums_assistant_tool_call_batches()
    {
        var messages = new List<ModelMessage>
        {
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("a"), Call("b")]),
            new(ModelRole.Tool, "{}", ToolCallId: "a", Name: "http.request"),
            new(ModelRole.Assistant, string.Empty, ToolCalls: [Call("c")]),
        };

        Assert.Equal(3, DurableToolCallCheckpoint.ReservedToolSteps(messages));
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

        Assert.Equal(12, DurableToolCallCheckpoint.NormalizeResumedStepCount(1, messages));
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

    [Fact]
    public void TryResolveLegacyToolCallId_succeeded_uses_action_hash_not_first_tool_result()
    {
        const string argsA =
            """{"method":"POST","url":"https://example.com/items/a","body":"FIRST"}""";
        const string argsB =
            """{"method":"POST","url":"https://example.com/items/b","body":"SECOND"}""";
        var hashB = ToolActionHash.Compute(
            ToolCatalog.HttpRequest,
            JsonSerializer.Deserialize<JsonElement>(argsB));
        var checkpoint = new WorkCheckpoint(
            DurableToolCallCheckpoint.Write(
            [
                new(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ModelToolCall("a", ToolCatalog.HttpRequest, argsA),
                        new ModelToolCall("b", ToolCatalog.HttpRequest, argsB)
                    ]),
                new(ModelRole.Tool, """{"ok":true}""", ToolCallId: "a", Name: ToolCatalog.HttpRequest)
            ]),
            2,
            0,
            1000);

        var resolved = DurableToolCallCheckpoint.TryResolveLegacyToolCallId(
            checkpoint,
            WorkSideEffectDisposition.Succeeded,
            hashB,
            null);

        Assert.Equal("b", resolved);
    }

    [Fact]
    public void TryResolveLegacyToolCallId_succeeded_is_null_when_action_hash_is_ambiguous()
    {
        const string sameArgs =
            """{"method":"POST","url":"https://example.com/items","body":"SAME"}""";
        var hash = ToolActionHash.Compute(
            ToolCatalog.HttpRequest,
            JsonSerializer.Deserialize<JsonElement>(sameArgs));
        var checkpoint = new WorkCheckpoint(
            DurableToolCallCheckpoint.Write(
            [
                new(
                    ModelRole.Assistant,
                    string.Empty,
                    ToolCalls:
                    [
                        new ModelToolCall("a", ToolCatalog.HttpRequest, sameArgs),
                        new ModelToolCall("b", ToolCatalog.HttpRequest, sameArgs)
                    ])
            ]),
            0,
            0,
            1000);

        Assert.Null(DurableToolCallCheckpoint.TryResolveLegacyToolCallId(
            checkpoint,
            WorkSideEffectDisposition.Succeeded,
            hash,
            null));
    }

    private static ModelToolCall Call(string id) => new(id, "http.request", """{"method":"GET","url":"https://example.com"}""");
}
