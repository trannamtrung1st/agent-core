using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Tests;

public sealed class TriggerContractTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-0005-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-0005-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_requires_instance_and_profile()
    {
        Assert.Throws<ArgumentException>(() => new TriggerOwner(Guid.Empty, ProfileId));
        Assert.Throws<ArgumentException>(() => new TriggerOwner(InstanceId, Guid.Empty));
        var owner = new TriggerOwner(InstanceId, ProfileId);
        Assert.Equal(InstanceId, owner.AgentInstanceId);
        Assert.Equal(ProfileId, owner.ProfileId);
    }

    [Fact]
    public void Intent_is_bounded_and_rejects_control_characters()
    {
        Assert.Equal("Call John", TriggerText.RequireIntent("  Call John  "));
        Assert.Equal(500, TriggerText.RequireIntent(new string('a', 500)).Length);
        Assert.Throws<ArgumentException>(() => TriggerText.RequireIntent(" "));
        Assert.Throws<ArgumentException>(() => TriggerText.RequireIntent(new string('a', 501)));
        Assert.Throws<ArgumentException>(() => TriggerText.RequireIntent("line\nbreak"));
    }

    [Fact]
    public void Timezone_accepts_utc_and_iana_ids_only()
    {
        Assert.Equal("UTC", TriggerTimeZone.Require("UTC"));
        Assert.Equal("Asia/Ho_Chi_Minh", TriggerTimeZone.Require("Asia/Ho_Chi_Minh"));
        Assert.Equal("Europe/London", TriggerTimeZone.Require("Europe/London"));
        Assert.Equal("America/Argentina/Buenos_Aires", TriggerTimeZone.Require("America/Argentina/Buenos_Aires"));
        Assert.Throws<ArgumentException>(() => TriggerTimeZone.Require(""));
        Assert.Throws<ArgumentException>(() => TriggerTimeZone.Require("not a zone"));
        Assert.Throws<ArgumentException>(() => TriggerTimeZone.Require("Asia"));
        Assert.Throws<ArgumentException>(() => TriggerTimeZone.Require("+07:00"));
    }

    [Fact]
    public void Schedules_reject_unbounded_or_inverted_recurrence()
    {
        Assert.Throws<ArgumentException>(() => new OneShotSchedule(Now.ToOffset(TimeSpan.FromHours(7)), "Asia/Ho_Chi_Minh"));
        Assert.Throws<ArgumentException>(() => new OneShotSchedule(Now, "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 24), null));
        var oneShot = new OneShotSchedule(Now, "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 24), new TimeOnly(9, 0));
        Assert.Equal(TriggerScheduleKind.OneShot, oneShot.Kind);

        Assert.Throws<ArgumentException>(() => new DailySchedule(0, new TimeOnly(9, 0), "UTC"));
        Assert.Throws<ArgumentException>(() => new DailySchedule(366, new TimeOnly(9, 0), "UTC"));
        Assert.Throws<ArgumentException>(() => new DailySchedule(1, new TimeOnly(9, 0), "UTC", new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 23)));
        Assert.Throws<ArgumentException>(() => new DailySchedule(1, new TimeOnly(9, 0), "UTC", maxOccurrences: 0));
        Assert.Throws<ArgumentException>(() => new DailySchedule(1, new TimeOnly(9, 0), "UTC", maxOccurrences: 367));
        var daily = new DailySchedule(365, new TimeOnly(9, 0), "UTC", maxOccurrences: 366);
        Assert.Equal(365, daily.IntervalDays);

        Assert.Throws<ArgumentException>(() => new WeeklySchedule(0, [DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London"));
        Assert.Throws<ArgumentException>(() => new WeeklySchedule(53, [DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London"));
        Assert.Throws<ArgumentException>(() => new WeeklySchedule(1, [], new TimeOnly(9, 0), "Europe/London"));
        Assert.Throws<ArgumentException>(() => new WeeklySchedule(1, [DayOfWeek.Monday, DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London"));
        var weekly = new WeeklySchedule(52, [DayOfWeek.Wednesday, DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London");
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday], weekly.Weekdays);
        Assert.True(weekly.SemanticEquals(new WeeklySchedule(52, [DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(9, 0), "Europe/London")));
        Assert.False(weekly.SemanticEquals(daily));
    }

    [Fact]
    public void Occurrence_evidence_and_identity_are_bounded()
    {
        var owner = new TriggerOwner(InstanceId, ProfileId);
        var admitted = Occurrence(owner, new string('a', TriggerLimits.MaxEvidenceBytes));
        Assert.Equal(OccurrenceRoutingDisposition.Pending, admitted.Disposition);
        Assert.Equal("registration|1|1758600000000", admitted.DedupeKey);
        Assert.Throws<ArgumentException>(() => Occurrence(owner, new string('a', TriggerLimits.MaxEvidenceBytes + 1)));
        Assert.Throws<ArgumentException>(() => new TriggerOccurrence(
            Guid.Empty,
            "dedupe-1",
            null,
            owner,
            TriggerSourceKind.Schedule,
            Now,
            Now,
            Now,
            "{}",
            null,
            1,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null));
        Assert.Throws<ArgumentException>(() => new TriggerRegistration(
            Guid.NewGuid(),
            owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new OneShotSchedule(Now, "UTC"),
            Now,
            null,
            0,
            0,
            1,
            Provenance(),
            null));
    }

    [Fact]
    public void Durable_schedule_kinds_do_not_include_runtime_timers()
    {
        Assert.Equal(["OneShot", "Daily", "Weekly"], Enum.GetNames<TriggerScheduleKind>());
        Assert.Equal(["Schedule", "ApplicationEvent"], Enum.GetNames<TriggerSourceKind>());
        Assert.Equal(
            ["Pending", "Claimed", "AcceptedLive", "AwaitingDurableWork", "Rejected"],
            Enum.GetNames<OccurrenceRoutingDisposition>());
    }

    private static TriggerOccurrence Occurrence(TriggerOwner owner, string evidence) =>
        new(
            Guid.Parse("019944af-0005-7000-8000-0000000000c1"),
            "registration|1|1758600000000",
            Guid.Parse("019944af-0005-7000-8000-0000000000d1"),
            owner,
            TriggerSourceKind.Schedule,
            Now,
            Now,
            Now,
            evidence,
            null,
            1,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null);

    private static TriggerProvenance Provenance() =>
        new(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now);
}
