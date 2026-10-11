using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class ScheduleDraftAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("okay so 60s", true)]
    [InlineData("60 seconds", true)]
    [InlineData("1 minute", true)]
    [InlineData("every minute", true)]
    [InlineData("don't use 60s", false)]
    [InlineData("60s then delete the report", false)]
    [InlineData("every minute what is the weather?", false)]
    [InlineData("2147483647 hours", false)]
    public void Serialized_draft_survives_only_one_interval_correction(string text, bool expected)
    {
        var draft = ScheduleDraftContext.ForFixedIntervalRejection("Wake me up", 30, "recurrence_below_minimum", Now);
        var json = System.Text.Json.JsonSerializer.Serialize(new { scheduleDraft = draft.ToJsonObject() });
        Assert.Equal(draft, ScheduleDraftContext.TryFromToolError(json));
        var eligible = true;
        var retained = ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible, text, "en", null);
        Assert.Equal(expected, retained is not null);
        Assert.False(eligible);
        Assert.Null(ScheduleDraftAdmission.PrepareDraftForUserTurn(draft, ref eligible, "60s", "en", null));
    }

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
        Assert.Equal("Say hello to me", retained!.Instructions);
        Assert.False(eligible);
    }

    [Fact]
    public void Complete_new_recurring_request_does_not_retain_failed_draft()
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
            "every minute remind me to check the oven",
            "en",
            null);
        Assert.Null(retained);
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
