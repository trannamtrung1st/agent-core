using AgentCore.Application.Execution;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class ChatActionAdmissionTests
{
    private static readonly Guid SessionId = Guid.Parse("019944af-0000-7000-8000-0000000000aa");

    [Fact]
    public void Same_session_chat_is_an_explicit_allow_without_approval()
    {
        var decision = ChatActionAdmission.Evaluate(Request());
        Assert.Equal(ChatAdmissionOutcome.Allow, decision.Outcome);
        Assert.Equal(SessionId, decision.BoundSessionId);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void Detached_execution_cannot_gain_session_chat()
    {
        var decision = ChatActionAdmission.Evaluate(Request(detached: true));
        Assert.Equal(ChatAdmissionOutcome.DenyDetached, decision.Outcome);
        Assert.Equal(SessionId, decision.BoundSessionId);
        Assert.False(decision.RequiresApproval);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Stale_epoch_or_response_is_rejected_before_effects(bool epochMatches, bool responseMatches)
    {
        var decision = ChatActionAdmission.Evaluate(Request(epochMatches: epochMatches, responseMatches: responseMatches));
        Assert.Equal(ChatAdmissionOutcome.DenyStale, decision.Outcome);
    }

    [Fact]
    public void A_second_observation_of_the_accepted_action_is_a_no_op()
    {
        var decision = ChatActionAdmission.Evaluate(Request(alreadyAccepted: true));
        Assert.Equal(ChatAdmissionOutcome.AlreadyAccepted, decision.Outcome);
        Assert.False(decision.RequiresApproval);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("destination")]
    [InlineData("profile")]
    [InlineData("tenant")]
    [InlineData("recipient")]
    public void Model_supplied_target_is_denied(string field)
    {
        var decision = ChatActionAdmission.Evaluate(field switch
        {
            "session" => Request(modelSessionId: "other"),
            "destination" => Request(modelDestination: "other"),
            "profile" => Request(modelProfileId: "other"),
            "tenant" => Request(modelTenant: "other"),
            _ => Request(modelRecipient: "other")
        });
        Assert.Equal(ChatAdmissionOutcome.DenyDestination, decision.Outcome);
        Assert.Equal(SessionId, decision.BoundSessionId);
    }

    [Fact]
    public void Unknown_action_kind_is_denied()
    {
        var decision = ChatActionAdmission.Evaluate(Request(actionKind: "teams.reply"));
        Assert.Equal(ChatAdmissionOutcome.DenyUnknownKind, decision.Outcome);
    }

    [Fact]
    public void Tool_limits_stay_at_the_current_bounds()
    {
        Assert.Equal(24, ToolLimits.MaxSteps);
        Assert.Equal(TimeSpan.FromSeconds(30), ToolLimits.PerTool);
        Assert.Equal(TimeSpan.FromSeconds(180), ToolLimits.Overall);
        Assert.Equal(8 * 1024 * 1024, ToolLimits.MaxOutputBytes);
    }

    private static ChatAdmissionRequest Request(
        string actionKind = AgentStepNormalizer.ChatRespondKind,
        string? modelSessionId = null,
        string? modelDestination = null,
        string? modelProfileId = null,
        string? modelTenant = null,
        string? modelRecipient = null,
        bool detached = false,
        bool epochMatches = true,
        bool responseMatches = true,
        bool alreadyAccepted = false) =>
        new(
            actionKind,
            SessionId,
            modelSessionId,
            modelDestination,
            modelProfileId,
            modelTenant,
            modelRecipient,
            detached,
            epochMatches,
            responseMatches,
            alreadyAccepted);
}
