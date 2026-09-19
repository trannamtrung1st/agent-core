using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class SessionLifecycleTests
{
    [Theory]
    [InlineData(SessionStatus.Created, SessionLifecycleStatus.Active)]
    [InlineData(SessionStatus.Attached, SessionLifecycleStatus.Active)]
    [InlineData(SessionStatus.Ending, SessionLifecycleStatus.Active)]
    [InlineData(SessionStatus.Paused, SessionLifecycleStatus.Paused)]
    [InlineData(SessionStatus.Ended, SessionLifecycleStatus.Ended)]
    public void Protocol_status_maps_to_additive_lifecycle(SessionStatus status, SessionLifecycleStatus expected)
    {
        Assert.Equal(expected, SessionLifecycle.FromProtocolStatus(status));
    }

    [Fact]
    public void Align_keeps_completed_expired_cancelled_independent_of_protocol_status()
    {
        Assert.Equal(
            SessionLifecycleStatus.Completed,
            SessionLifecycle.Align(SessionStatus.Attached, SessionLifecycleStatus.Completed));
        Assert.Equal(
            SessionLifecycleStatus.Expired,
            SessionLifecycle.Align(SessionStatus.Paused, SessionLifecycleStatus.Expired));
        Assert.Equal(
            SessionLifecycleStatus.Cancelled,
            SessionLifecycle.Align(SessionStatus.Created, SessionLifecycleStatus.Cancelled));
        Assert.Equal(
            SessionLifecycleStatus.Ended,
            SessionLifecycle.Align(SessionStatus.Ended, SessionLifecycleStatus.Active));
    }

    [Fact]
    public void MaxDuration_resolves_once_to_absolute_deadline()
    {
        var now = new DateTimeOffset(2026, 9, 19, 3, 0, 0, TimeSpan.Zero);
        var purpose = SessionLifecycle.ResolvePurpose(
            new SessionPurpose(SessionPurposeKind.Goal, "Finish the task"),
            now,
            TimeSpan.FromMinutes(60));
        Assert.Equal(SessionPurposeKind.Goal, purpose.Kind);
        Assert.Equal("Finish the task", purpose.Description);
        Assert.Equal(now.AddMinutes(60), purpose.DeadlineAt);

        var reconnect = SessionLifecycle.ResolvePurpose(purpose, now.AddHours(2), TimeSpan.FromMinutes(60));
        Assert.Equal(now.AddMinutes(60), reconnect.DeadlineAt);
    }

    [Fact]
    public void Explicit_deadline_is_not_replaced_by_maxDuration()
    {
        var now = new DateTimeOffset(2026, 9, 19, 3, 0, 0, TimeSpan.Zero);
        var deadline = now.AddHours(3);
        var purpose = SessionLifecycle.ResolvePurpose(
            new SessionPurpose(SessionPurposeKind.Goal, DeadlineAt: deadline),
            now,
            TimeSpan.FromMinutes(5));
        Assert.Equal(deadline, purpose.DeadlineAt);
    }

    [Fact]
    public void Purpose_metadata_and_description_are_bounded()
    {
        SessionLifecycle.Validate(new SessionPurpose(SessionPurposeKind.Ongoing, "ok"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionLifecycle.Validate(new SessionPurpose(SessionPurposeKind.Ongoing, new string('a', 2001))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionLifecycle.Validate(new SessionPurpose(
                SessionPurposeKind.Goal,
                Metadata: Enumerable.Range(0, 17).ToDictionary(i => $"k{i}", i => "v"))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionLifecycle.Validate(new SessionPurpose(
                SessionPurposeKind.Goal,
                Metadata: new Dictionary<string, string> { ["k"] = new string('x', 4001) })));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionLifecycle.ResolvePurpose(null, DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SessionLifecycle.ResolvePurpose(
                null,
                DateTimeOffset.UtcNow,
                SessionLifecycle.MaxMaxDuration + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Host_and_system_remain_outside_the_agent_and_user_policy_bits()
    {
        var policy = SessionCompletionPolicy.Default;
        Assert.Equal(AgentCompletionAuthority.Disabled, policy.AgentCompletion);
        Assert.True(policy.UserCompletionAllowed);
        Assert.True(policy.UserCancellationAllowed);
    }

    [Theory]
    [InlineData(SessionLifecycleStatus.Active, SessionLifecycleStatus.Paused)]
    [InlineData(SessionLifecycleStatus.Paused, SessionLifecycleStatus.Active)]
    [InlineData(SessionLifecycleStatus.Active, SessionLifecycleStatus.Completed)]
    [InlineData(SessionLifecycleStatus.Paused, SessionLifecycleStatus.Expired)]
    [InlineData(SessionLifecycleStatus.Active, SessionLifecycleStatus.Cancelled)]
    [InlineData(SessionLifecycleStatus.Active, SessionLifecycleStatus.Ended)]
    public void Graph_allows_pause_resume_and_terminalization(
        SessionLifecycleStatus from,
        SessionLifecycleStatus to)
    {
        Assert.True(SessionLifecycle.Allows(from, to));
    }

    [Fact]
    public void Terminal_outcomes_cannot_change_to_a_different_outcome()
    {
        Assert.False(SessionLifecycle.Allows(SessionLifecycleStatus.Completed, SessionLifecycleStatus.Cancelled));
        Assert.False(SessionLifecycle.Allows(SessionLifecycleStatus.Expired, SessionLifecycleStatus.Active));
        Assert.True(SessionLifecycle.Allows(SessionLifecycleStatus.Completed, SessionLifecycleStatus.Completed));
    }
}
