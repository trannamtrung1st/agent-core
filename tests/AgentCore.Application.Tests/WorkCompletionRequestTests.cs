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

    [Theory]
    [InlineData(TenantExperienceToolContract.Consolidate, "requiresApproval", false, true)]
    [InlineData(TenantExperienceToolContract.Consolidate, "denied", false, true)]
    [InlineData(TenantExperienceToolContract.Record, "denied", false, true)]
    [InlineData(TenantExperienceToolContract.Consolidate, "allowed", true, false)]
    [InlineData(TenantExperienceToolContract.Consolidate, "allowed", false, false)]
    [InlineData(TenantExperienceToolContract.Consolidate, "requiresApproval", true, false)]
    [InlineData(ToolCatalog.WorkspaceWrite, "denied", false, false)]
    public void NoAction_requires_a_typed_unapplied_Experience_refusal_not_just_an_applied_flag(string tool, string decision, bool applied, bool accepted)
    {
        var args = JsonSerializer.SerializeToElement(new { outcome = "NoAction", summary = "No mutation was applied", attentionRequired = false });
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args,
            [new(ModelRole.Tool, JsonSerializer.Serialize(new { decision, applied }), ToolCallId: "effect", Name: tool)], out _, out _, out _));
    }

    [Theory]
    [InlineData("denied", false, true, true)]
    [InlineData("unknown", false, true, false)]
    [InlineData("completed", true, true, false)]
    [InlineData("completed", false, true, false)]
    [InlineData("denied", true, true, false)]
    [InlineData("denied", false, false, false)]
    public void NoAction_accepts_only_a_factual_denied_Experience_mutation_receipt(string outcome, bool applied, bool experience, bool accepted)
    {
        var args = JsonSerializer.SerializeToElement(new { outcome = "NoAction", summary = "No mutation was applied", attentionRequired = false });
        var payload = JsonSerializer.Serialize(new { outcome, applied, experienceId = experience ? "11111111-1111-1111-1111-111111111111" : null });
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args,
            [new(ModelRole.Tool, payload, ToolCallId: "consent", Name: ToolCatalog.RequestAuthorization)], out _, out _, out _));
    }

    [Theory]
    [InlineData("completed", true, false, true)]
    [InlineData("denied", false, false, true)]
    [InlineData("unknown", false, false, false)]
    [InlineData("denied", true, false, false)]
    [InlineData("completed", false, false, false)]
    [InlineData("completed", true, true, false)]
    public void NoAction_treats_typed_Experience_source_consent_as_a_read_only_result(string outcome, bool readAuthorized, bool applied, bool accepted)
    {
        var args = JsonSerializer.SerializeToElement(new { outcome = "NoAction", summary = "Nothing useful to record", attentionRequired = false });
        var payload = JsonSerializer.Serialize(new { outcome, applied, readAuthorized, experienceId = (string?)null, experienceSourceObservation = (object?)null });
        Assert.Equal(accepted, WorkCompletionRequest.TryParse(args,
            [new(ModelRole.Tool, payload, ToolCallId: "source-consent", Name: ToolCatalog.RequestAuthorization)], out _, out _, out _));
    }
}
