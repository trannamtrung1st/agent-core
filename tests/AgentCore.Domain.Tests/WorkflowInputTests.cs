using System.Text.Json;
using AgentCore.Domain.Workflows;

namespace AgentCore.Domain.Tests;

public sealed class WorkflowInputTests
{
    private static WorkflowGraph Graph() => WorkflowGraphTests.Valid() with { Inputs = [
        new("title", "Title", WorkflowInputKind.Text, true, 20),
        new("accepted", "Accepted", WorkflowInputKind.Boolean, true),
        new("amount", "Amount", WorkflowInputKind.Number, false),
        new("due", "Due", WorkflowInputKind.Date, false),
        new("choice", "Choice", WorkflowInputKind.Choice, false, Choices: ["one", "two"])
    ] };
    [Fact]
    public void Typed_inputs_have_deterministic_key_order_and_preserve_data()
    {
        using var a = JsonDocument.Parse("""{"title":"Chấp nhận 🎉","accepted":true,"amount":12.5,"due":"2026-10-28","choice":"two"}""");
        using var b = JsonDocument.Parse("""{"choice":"two","due":"2026-10-28","amount":12.5,"accepted":true,"title":"Chấp nhận 🎉"}""");
        Assert.Equal(WorkflowExecutionRules.Inputs(Graph(), a.RootElement), WorkflowExecutionRules.Inputs(Graph(), b.RootElement));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"title\":\"ok\",\"accepted\":\"true\"}")]
    [InlineData("{\"title\":\"ok\",\"accepted\":true,\"amount\":\"12\"}")]
    [InlineData("{\"title\":\"ok\",\"accepted\":true,\"amount\":1000000000001}")]
    [InlineData("{\"title\":\"ok\",\"accepted\":true,\"choice\":\"unknown\"}")]
    [InlineData("{\"title\":\"ok\",\"accepted\":true,\"due\":\"2026-02-30\"}")]
    [InlineData("{\"title\":\"ok\",\"accepted\":true,\"injected\":\"value\"}")]
    [InlineData("{\"title\":\"ok\",\"title\":\"changed\",\"accepted\":true}")]
    [InlineData("{\"title\":\"\",\"accepted\":true}")]
    public void Invalid_or_injected_inputs_are_refused(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => WorkflowExecutionRules.Inputs(Graph(), document.RootElement));
    }
}
