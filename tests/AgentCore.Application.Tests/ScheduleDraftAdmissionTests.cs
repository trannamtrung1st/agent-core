using System.Text.Json;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class ScheduleDraftAdmissionTests
{
    [Fact]
    public void Serialized_draft_is_evidence_for_exactly_one_eligible_turn()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection("Say hello", 30,
            "recurrence_below_minimum", DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(new { scheduleDraft = draft.ToJsonObject() });
        Assert.Equal(draft, ScheduleDraftContext.TryFromToolError(json));
        var eligible = true;
        Assert.Equal(draft, ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible));
        Assert.False(eligible);
        Assert.Null(ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible));
    }

    [Fact]
    public void Ineligible_turn_never_reintroduces_draft_evidence()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection("Say hello", 30,
            "recurrence_below_minimum", DateTimeOffset.UnixEpoch);
        var eligible = false;
        Assert.Null(ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible));
        Assert.False(eligible);
    }
}
