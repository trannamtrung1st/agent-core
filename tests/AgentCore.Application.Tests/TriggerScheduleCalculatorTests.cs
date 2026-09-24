using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tests;

public sealed class TriggerScheduleCalculatorTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-0008-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-0008-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wall_clock_resolution_keeps_zone_meaning_across_dst()
    {
        var saigon = TriggerScheduleCalculator.ResolveWallClock(
            "Asia/Ho_Chi_Minh",
            new DateOnly(2026, 9, 24),
            new TimeOnly(9, 0));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 2, 0, 0, TimeSpan.Zero), saigon);

        var utc = TriggerScheduleCalculator.ResolveWallClock("UTC", new DateOnly(2026, 9, 24), new TimeOnly(9, 0));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero), utc);

        var (before, after) = MondayPairWithOffsetChange("Europe/London");
        var first = TriggerScheduleCalculator.ResolveWallClock("Europe/London", before, new TimeOnly(9, 0));
        var second = TriggerScheduleCalculator.ResolveWallClock("Europe/London", after, new TimeOnly(9, 0));
        Assert.NotEqual(TimeSpan.FromDays(7), second - first);
        Assert.Equal(new TimeOnly(9, 0), LocalTime("Europe/London", first));
        Assert.Equal(new TimeOnly(9, 0), LocalTime("Europe/London", second));
    }

    [Fact]
    public void Ambiguous_local_time_uses_the_earlier_utc_instant_once()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        DateOnly? ambiguous = null;
        for (var day = new DateOnly(2026, 10, 1); day < new DateOnly(2026, 11, 15); day = day.AddDays(1))
        {
            var local = DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(1, 30)), DateTimeKind.Unspecified);
            if (zone.IsAmbiguousTime(local))
            {
                ambiguous = day;
                break;
            }
        }

        Assert.NotNull(ambiguous);
        var localTime = DateTime.SpecifyKind(ambiguous.Value.ToDateTime(new TimeOnly(1, 30)), DateTimeKind.Unspecified);
        var offsets = zone.GetAmbiguousTimeOffsets(localTime);
        var earlier = offsets[0] >= offsets[1] ? offsets[0] : offsets[1];
        var resolved = TriggerScheduleCalculator.ResolveWallClock("Europe/London", ambiguous.Value, new TimeOnly(1, 30));
        var again = TriggerScheduleCalculator.ResolveWallClock("Europe/London", ambiguous.Value, new TimeOnly(1, 30));
        Assert.Equal(new DateTimeOffset(localTime, earlier).ToUniversalTime(), resolved);
        Assert.Equal(resolved, again);
    }

    [Fact]
    public void Nonexistent_local_time_shifts_forward_by_the_gap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateOnly? missing = null;
        for (var day = new DateOnly(2026, 3, 1); day < new DateOnly(2026, 4, 1); day = day.AddDays(1))
        {
            var local = DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(2, 30)), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                missing = day;
                break;
            }
        }

        Assert.NotNull(missing);
        var resolved = TriggerScheduleCalculator.ResolveWallClock("America/New_York", missing.Value, new TimeOnly(2, 30));
        var localResolved = TimeZoneInfo.ConvertTime(resolved, zone);
        Assert.Equal(missing.Value, DateOnly.FromDateTime(localResolved.DateTime));
        Assert.Equal(new TimeOnly(3, 30), TimeOnly.FromDateTime(localResolved.DateTime));
    }

    [Fact]
    public void Initial_next_is_the_first_wall_clock_strictly_after_creation()
    {
        var daily = new DailySchedule(1, new TimeOnly(9, 0), "UTC");
        Assert.Equal(Created.AddHours(1), TriggerScheduleCalculator.InitialNext(daily, Created));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero),
            TriggerScheduleCalculator.InitialNext(daily, new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero)));

        var weekly = new WeeklySchedule(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC");
        var wednesday = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Wednesday, wednesday.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero), TriggerScheduleCalculator.InitialNext(weekly, wednesday));

        var everyOther = new WeeklySchedule(2, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC");
        var mondayMorning = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        var firstMonday = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(firstMonday, TriggerScheduleCalculator.InitialNext(everyOther, mondayMorning));
        var admitted = TriggerScheduleAdmission.Decide(Sample(everyOther, firstMonday, null, mondayMorning), firstMonday);
        Assert.Equal(firstMonday.AddDays(14), admitted.NextAtUtc);
    }

    [Fact]
    public void Start_date_keeps_the_interval_phase_when_the_anchor_is_already_past()
    {
        var created = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var everyOtherDay = new DailySchedule(2, new TimeOnly(9, 0), "UTC", startDate: new DateOnly(2026, 9, 2));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero),
            TriggerScheduleCalculator.InitialNext(everyOtherDay, created));

        var futureStart = new DailySchedule(2, new TimeOnly(9, 0), "UTC", startDate: new DateOnly(2026, 9, 10));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
            TriggerScheduleCalculator.InitialNext(futureStart, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));

        var everyOtherWeek = new WeeklySchedule(
            2,
            [DayOfWeek.Monday],
            new TimeOnly(9, 0),
            "UTC",
            startDate: new DateOnly(2026, 9, 7));
        Assert.Equal(DayOfWeek.Monday, new DateOnly(2026, 9, 7).DayOfWeek);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero),
            TriggerScheduleCalculator.InitialNext(everyOtherWeek, created));
    }

    [Fact]
    public void Weekly_interval_advances_from_the_admitted_slot_week()
    {
        var everyOther = new WeeklySchedule(2, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC");
        var created = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
        var offPhase = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(DayOfWeek.Monday, offPhase.DayOfWeek);
        var decision = TriggerScheduleAdmission.Decide(Sample(everyOther, offPhase, null, created), offPhase);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero), decision.NextAtUtc);

        var aligned = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        var alignedDecision = TriggerScheduleAdmission.Decide(Sample(everyOther, aligned, null, created), aligned);
        Assert.Equal(aligned.AddDays(14), alignedDecision.NextAtUtc);

        var bothDays = new WeeklySchedule(2, [DayOfWeek.Monday, DayOfWeek.Wednesday], new TimeOnly(9, 0), "UTC");
        var sameWeek = TriggerScheduleAdmission.Decide(Sample(bothDays, offPhase, null, created), offPhase);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero), sameWeek.NextAtUtc);
    }

    [Fact]
    public void Recurring_recovery_coalesces_to_the_latest_due_slot()
    {
        var schedule = new DailySchedule(1, new TimeOnly(9, 0), "UTC");
        var first = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var registration = Sample(schedule, first, null);
        var before = TriggerScheduleAdmission.Decide(registration, first.AddMilliseconds(-1));
        Assert.Equal(ScheduleAdmissionKind.NotDue, before.Kind);

        var exact = TriggerScheduleAdmission.Decide(registration, first);
        Assert.Equal(ScheduleAdmissionKind.Admit, exact.Kind);
        Assert.Equal(0, exact.SkippedCount);
        Assert.Equal(first.AddDays(1), exact.NextAtUtc);

        var late = TriggerScheduleAdmission.Decide(registration, new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero), late.ScheduledAtUtc);
        Assert.Equal(4, late.SkippedCount);
        Assert.Equal(first, late.SkippedFromUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero), late.SkippedToUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero), late.NextAtUtc);
        Assert.Equal(1, late.OccurrenceCount);

        var capped = Sample(new DailySchedule(1, new TimeOnly(9, 0), "UTC", maxOccurrences: 1), first, null);
        var once = TriggerScheduleAdmission.Decide(capped, first);
        Assert.Equal(TriggerRegistrationStatus.Completed, once.Status);
        Assert.Null(once.NextAtUtc);

        var ending = Sample(new DailySchedule(1, new TimeOnly(9, 0), "UTC", endDate: new DateOnly(2026, 9, 1)), first, null);
        var lastDay = TriggerScheduleAdmission.Decide(ending, first);
        Assert.Equal(TriggerRegistrationStatus.Completed, lastDay.Status);
        Assert.Null(lastDay.NextAtUtc);
    }

    [Fact]
    public void One_shot_expiry_rejects_and_a_missed_valid_shot_is_admitted_once()
    {
        var at = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var shot = new OneShotSchedule(at, "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 1), new TimeOnly(16, 0));
        var missed = TriggerScheduleAdmission.Decide(Sample(shot, at, null), at.AddDays(1));
        Assert.Equal(ScheduleAdmissionKind.Admit, missed.Kind);
        Assert.Equal(TriggerRegistrationStatus.Completed, missed.Status);
        Assert.Equal(at, missed.ScheduledAtUtc);

        var expired = TriggerScheduleAdmission.Decide(
            Sample(shot, at, at.AddHours(1)),
            at.AddDays(1));
        Assert.Equal(ScheduleAdmissionKind.Expire, expired.Kind);
        Assert.Equal(0, expired.OccurrenceCount);

        var first = TriggerScheduleAdmission.CreateOccurrence(Sample(shot, at, null), missed, at.AddDays(1));
        var second = TriggerScheduleAdmission.CreateOccurrence(Sample(shot, at, null), missed, at.AddDays(2));
        Assert.Equal(first.OccurrenceId, second.OccurrenceId);
        Assert.Equal(first.DedupeKey, second.DedupeKey);
    }

    [Fact]
    public void Unknown_timezone_is_rejected_without_inventing_an_instant()
    {
        var schedule = new DailySchedule(1, new TimeOnly(9, 0), "Fake/Nowhere");
        var registration = Sample(schedule, Created.AddHours(1), null);
        Assert.Throws<TriggerTimeZoneUnavailableException>(() => TriggerScheduleAdmission.Decide(registration, Created.AddHours(2)));
    }

    [Fact]
    public void Fixed_interval_coalesces_more_than_max_iterative_slots_in_one_admission()
    {
        const int intervalSeconds = 60;
        const int missedSlots = 5001;
        var schedule = new FixedIntervalSchedule(intervalSeconds, Created, null, null);
        var storedNext = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var asOf = storedNext.AddSeconds(missedSlots * intervalSeconds);
        var registration = Sample(schedule, storedNext, null);

        var admission = TriggerScheduleAdmission.Decide(registration, asOf);
        Assert.Equal(ScheduleAdmissionKind.Admit, admission.Kind);
        Assert.Equal(storedNext.AddSeconds(missedSlots * intervalSeconds), admission.ScheduledAtUtc);
        Assert.Equal(missedSlots - 1, admission.SkippedCount);
        Assert.NotNull(admission.NextAtUtc);
        Assert.True(admission.NextAtUtc > asOf);
    }

    private static (DateOnly Before, DateOnly After) MondayPairWithOffsetChange(string timeZoneId)
    {
        for (var day = new DateOnly(2026, 1, 5); day < new DateOnly(2026, 12, 1); day = day.AddDays(1))
        {
            if (day.DayOfWeek != DayOfWeek.Monday)
            {
                continue;
            }

            var next = day.AddDays(7);
            var first = TriggerScheduleCalculator.ResolveWallClock(timeZoneId, day, new TimeOnly(9, 0));
            var second = TriggerScheduleCalculator.ResolveWallClock(timeZoneId, next, new TimeOnly(9, 0));
            if (second - first != TimeSpan.FromDays(7))
            {
                return (day, next);
            }
        }

        throw new InvalidOperationException("Expected a DST boundary.");
    }

    private static TimeOnly LocalTime(string timeZoneId, DateTimeOffset instant)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    private static TriggerRegistration Sample(
        TriggerSchedule schedule,
        DateTimeOffset next,
        DateTimeOffset? expires,
        DateTimeOffset? created = null)
    {
        var stamped = created ?? Created;
        return new(
            Guid.Parse("019944af-0008-7000-8000-0000000000c1"),
            new TriggerOwner(InstanceId, ProfileId),
            TriggerRegistrationStatus.Active,
            "Call John",
            schedule,
            next,
            expires,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, stamped, stamped),
            null);
    }
}
