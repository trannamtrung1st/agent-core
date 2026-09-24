using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class ScheduleDraftAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Unrelated_turn_after_rejection_clears_draft_before_prompt()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var eligible = true;
        var retained = ScheduleDraftAdmission.PrepareDraftForUserTurn(
            draft,
            ref eligible,
            "what is 2 + 2?",
            "en",
            null);
        Assert.Null(retained);
        Assert.False(eligible);
    }

    [Fact]
    public void Interval_correction_keeps_draft_for_one_follow_up_turn()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var eligible = true;
        var retained = ScheduleDraftAdmission.PrepareDraftForUserTurn(
            draft,
            ref eligible,
            "every minute",
            "en",
            null);
        Assert.NotNull(retained);
        Assert.Equal("Say hello to me", retained!.Intent);
        Assert.False(eligible);
    }

    [Fact]
    public void Second_schedule_turn_after_correction_window_does_not_inherit_draft()
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection(
            "Say hello to me",
            30,
            "recurrence_below_minimum",
            Now);
        var eligible = true;
        _ = ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible, "what is 2 + 2?", "en", null);
        var second = ScheduleDraftAdmission.PrepareDraftForUserTurn(
            draft,
            ref eligible,
            "every minute",
            "en",
            null);
        Assert.Null(second);
    }
}
