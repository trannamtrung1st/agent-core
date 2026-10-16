using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;

namespace AgentCore.Application.Tests;

public sealed class WorkCompletionRequestTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    [InlineData(4442, false)]
    public void Summary_limit_is_enforced_with_actionable_bounded_feedback(int length, bool accepted)
    {
        using var schema = JsonDocument.Parse(WorkCompletionRequest.Contract.ParametersJson);
        Assert.Equal(WorkCompletionRequest.MaxSummaryCharacters,
            schema.RootElement.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32());
        var args = JsonSerializer.SerializeToElement(new { summary = new string('x', length), outcome = "Response", attentionRequired = false });
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args, [], out _, out _, out var rejection));
        if (!accepted)
        {
            Assert.Contains("summary", rejection);
            Assert.Contains("2000", rejection);
            Assert.DoesNotContain(new string('x', 30), rejection);
        }
    }

    [Theory]
    [InlineData("Wrong", false, "outcome")]
    [InlineData("Response", true, "attentionRequired")]
    [InlineData("NeedsAttention", false, "attentionRequired")]
    public void Invalid_outcome_and_attention_have_field_specific_feedback(string outcome, bool attentionRequired, string field)
    {
        Assert.False(WorkCompletionRequest.TryParse(JsonSerializer.SerializeToElement(new { summary = "Result", outcome, attentionRequired }),
            [], out _, out _, out var rejection));
        Assert.Contains(field, rejection);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"data\":{\"error\":\"saved field\"}}", false)]
    [InlineData("{\"error\":null,\"ok\":true}", false)]
    [InlineData("{\"error\":\"\",\"ok\":true}", false)]
    [InlineData("Action completed", false)]
    [InlineData("{\"error\":\"forbidden\",\"message\":\"Denied by policy\"}", true)]
    public void NoAction_distinguishes_rejected_actions_from_successful_action_data(string payload, bool accepted)
    {
        using var args = JsonDocument.Parse("""{"outcome":"NoAction","summary":"Nothing to do","attentionRequired":false}""");
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args.RootElement,
            [new(ModelRole.Tool, payload, ToolCallId: "write", Name: ToolCatalog.WorkspaceWrite)], out _, out _, out _));
    }
}
