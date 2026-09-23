using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public enum ScheduleAdmissionKind
{
    Admit,
    NotDue,
    Expire,
    Complete
}

public sealed record ScheduleAdmission(
    ScheduleAdmissionKind Kind,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? NextAtUtc,
    int SkippedCount,
    DateTimeOffset? SkippedFromUtc,
    DateTimeOffset? SkippedToUtc,
    TriggerRegistrationStatus Status,
    int OccurrenceCount);

public sealed class TriggerTimeZoneUnavailableException : Exception
{
    public TriggerTimeZoneUnavailableException()
        : base("Timezone is unavailable.")
    {
    }
}

public static class TriggerScheduleCalculator
{
    public const int MaxCoalescedSlots = 4000;

    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    public static DateTimeOffset ResolveWallClock(string timeZoneId, DateOnly date, TimeOnly time)
    {
        var zone = RequireZone(timeZoneId);
        return Resolve(zone, date, time);
    }

    public static DateTimeOffset? InitialNext(TriggerSchedule schedule, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        createdAt = Truncate(createdAt);
        return schedule switch
        {
            OneShotSchedule oneShot => Truncate(oneShot.AtUtc),
            DailySchedule daily => InitialDaily(daily, createdAt),
            WeeklySchedule weekly => InitialWeekly(weekly, createdAt),
            _ => throw new ArgumentException("Schedule kind is not supported.")
        };
    }

    public static TimeZoneInfo RequireZone(string timeZoneId)
    {
        var id = TriggerTimeZone.Require(timeZoneId);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new TriggerTimeZoneUnavailableException();
        }
    }

    private static DateTimeOffset? InitialDaily(DailySchedule schedule, DateTimeOffset createdAt)
    {
        var zone = RequireZone(schedule.TimeZoneId);
        var date = StartDate(zone, createdAt, schedule.StartDate);
        for (var step = 0; step <= TriggerLimits.MaxMaxOccurrences; step++)
        {
            if (schedule.EndDate is DateOnly end && date > end)
            {
                return null;
            }

            var instant = Resolve(zone, date, schedule.LocalTime);
            if (instant > createdAt)
            {
                return instant;
            }

            date = date.AddDays(schedule.IntervalDays);
        }

        return null;
    }

    private static DateTimeOffset? InitialWeekly(WeeklySchedule schedule, DateTimeOffset createdAt)
    {
        var zone = RequireZone(schedule.TimeZoneId);
        var date = StartDate(zone, createdAt, schedule.StartDate);
        var selected = schedule.Weekdays.ToHashSet();
        var limit = (TriggerLimits.MaxMaxOccurrences * 7) + 7;
        for (var step = 0; step <= limit; step++)
        {
            if (schedule.EndDate is DateOnly end && date > end)
            {
                return null;
            }

            if (selected.Contains(date.DayOfWeek))
            {
                var instant = Resolve(zone, date, schedule.LocalTime);
                if (instant > createdAt)
                {
                    return instant;
                }
            }

            date = date.AddDays(1);
        }

        return null;
    }

    private static DateOnly StartDate(TimeZoneInfo zone, DateTimeOffset createdAt, DateOnly? startDate)
    {
        var createdLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(createdAt, zone).DateTime);
        return startDate is DateOnly start && start > createdLocal ? start : createdLocal;
    }

    private static DateTimeOffset Resolve(TimeZoneInfo zone, DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var earlier = offsets[0] >= offsets[1] ? offsets[0] : offsets[1];
            return Truncate(new DateTimeOffset(local, earlier).ToUniversalTime());
        }

        if (zone.IsInvalidTime(local))
        {
            var shifted = ShiftForward(zone, local);
            var offset = zone.GetUtcOffset(shifted);
            return Truncate(new DateTimeOffset(DateTime.SpecifyKind(shifted, DateTimeKind.Unspecified), offset).ToUniversalTime());
        }

        return Truncate(new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime());
    }

    private static DateTime ShiftForward(TimeZoneInfo zone, DateTime invalidLocal)
    {
        var before = invalidLocal;
        for (var step = 0; zone.IsInvalidTime(before) && step < 180; step++)
        {
            before = before.AddMinutes(-1);
        }

        var after = invalidLocal;
        for (var step = 0; zone.IsInvalidTime(after) && step < 180; step++)
        {
            after = after.AddMinutes(1);
        }

        var gap = zone.GetUtcOffset(after) - zone.GetUtcOffset(before);
        if (gap < TimeSpan.Zero)
        {
            gap = -gap;
        }

        var shifted = invalidLocal.Add(gap);
        return zone.IsInvalidTime(shifted) ? after : shifted;
    }
}

public static class TriggerScheduleAdmission
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ScheduleAdmission Decide(TriggerRegistration registration, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(registration);
        asOf = TriggerScheduleCalculator.Truncate(asOf);
        if (registration.NextOccurrenceAtUtc is not DateTimeOffset storedNext)
        {
            return Finish(ScheduleAdmissionKind.Complete, registration, TriggerRegistrationStatus.Completed);
        }

        storedNext = TriggerScheduleCalculator.Truncate(storedNext);
        if (registration.ExpiresAtUtc is DateTimeOffset expiry && TriggerScheduleCalculator.Truncate(expiry) <= asOf)
        {
            return Finish(ScheduleAdmissionKind.Expire, registration, TriggerRegistrationStatus.Expired);
        }

        if (storedNext > asOf)
        {
            return new ScheduleAdmission(
                ScheduleAdmissionKind.NotDue,
                null,
                storedNext,
                0,
                null,
                null,
                registration.Status,
                registration.OccurrenceCount);
        }

        if (registration.Schedule is OneShotSchedule)
        {
            return new ScheduleAdmission(
                ScheduleAdmissionKind.Admit,
                storedNext,
                null,
                0,
                null,
                null,
                TriggerRegistrationStatus.Completed,
                registration.OccurrenceCount + 1);
        }

        if (OccurrenceCap(registration.Schedule) is int cap && registration.OccurrenceCount >= cap)
        {
            return Finish(ScheduleAdmissionKind.Complete, registration, TriggerRegistrationStatus.Completed);
        }

        if (IsAfterEnd(registration.Schedule, storedNext))
        {
            return Finish(ScheduleAdmissionKind.Complete, registration, TriggerRegistrationStatus.Completed);
        }

        var anchorWeek = AnchorWeek(registration, storedNext);
        var latest = storedNext;
        DateTimeOffset? previous = null;
        var count = 1;
        var guard = 0;
        while (guard++ < TriggerScheduleCalculator.MaxCoalescedSlots)
        {
            var next = Following(registration.Schedule, latest, anchorWeek);
            if (next is null || next <= latest || next > asOf || IsAfterEnd(registration.Schedule, next.Value))
            {
                break;
            }

            previous = latest;
            latest = next.Value;
            count++;
        }

        var skipped = count - 1;
        var nextFuture = Following(registration.Schedule, latest, anchorWeek);
        if (nextFuture is not null && IsAfterEnd(registration.Schedule, nextFuture.Value))
        {
            nextFuture = null;
        }

        var occurrenceCount = registration.OccurrenceCount + 1;
        var status = TriggerRegistrationStatus.Active;
        if (nextFuture is null || (OccurrenceCap(registration.Schedule) is int limit && occurrenceCount >= limit))
        {
            nextFuture = null;
            status = TriggerRegistrationStatus.Completed;
        }

        return new ScheduleAdmission(
            ScheduleAdmissionKind.Admit,
            latest,
            nextFuture,
            skipped,
            skipped > 0 ? storedNext : null,
            skipped > 0 ? previous ?? storedNext : null,
            status,
            occurrenceCount);
    }

    public static TriggerOccurrence CreateOccurrence(
        TriggerRegistration registration,
        ScheduleAdmission admission,
        DateTimeOffset asOf)
    {
        if (admission.Kind != ScheduleAdmissionKind.Admit || admission.ScheduledAtUtc is not DateTimeOffset scheduled)
        {
            throw new ArgumentException("Only an admitted schedule produces an occurrence.");
        }

        asOf = TriggerScheduleCalculator.Truncate(asOf);
        scheduled = TriggerScheduleCalculator.Truncate(scheduled);
        var dedupeKey = DedupeKey(registration.RegistrationId, registration.ScheduleRevision, scheduled);
        return new TriggerOccurrence(
            OccurrenceId(dedupeKey),
            dedupeKey,
            registration.RegistrationId,
            registration.Owner,
            TriggerSourceKind.Schedule,
            scheduled,
            asOf,
            asOf,
            Evidence(registration, admission, scheduled),
            null,
            registration.ScheduleRevision,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null);
    }

    public static TriggerRegistration Advance(
        TriggerRegistration registration,
        ScheduleAdmission admission,
        DateTimeOffset asOf,
        string? suspensionReason = null)
    {
        asOf = TriggerScheduleCalculator.Truncate(asOf);
        return registration.WithScheduleAdvance(
            admission.Status,
            admission.NextAtUtc,
            admission.OccurrenceCount,
            registration.Revision + 1,
            asOf,
            suspensionReason ?? registration.SuspensionReason);
    }

    public static string DedupeKey(Guid registrationId, long scheduleRevision, DateTimeOffset scheduledAtUtc) =>
        $"schedule:{registrationId:D}:{scheduleRevision}:{TriggerScheduleCalculator.Truncate(scheduledAtUtc).ToUnixTimeMilliseconds()}";

    public static Guid OccurrenceId(string dedupeKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dedupeKey));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static ScheduleAdmission Finish(
        ScheduleAdmissionKind kind,
        TriggerRegistration registration,
        TriggerRegistrationStatus status) =>
        new(kind, null, null, 0, null, null, status, registration.OccurrenceCount);

    private static int? OccurrenceCap(TriggerSchedule schedule) => schedule switch
    {
        DailySchedule daily => daily.MaxOccurrences,
        WeeklySchedule weekly => weekly.MaxOccurrences,
        _ => null
    };

    private static int AnchorWeek(TriggerRegistration registration, DateTimeOffset storedNext)
    {
        if (registration.Schedule is not WeeklySchedule weekly)
        {
            return 0;
        }

        var zone = TriggerScheduleCalculator.RequireZone(weekly.TimeZoneId);
        var first = TriggerScheduleCalculator.InitialNext(weekly, registration.Provenance.CreatedAt) ?? storedNext;
        return WeekIndex(LocalDate(zone, first));
    }

    private static DateTimeOffset? Following(TriggerSchedule schedule, DateTimeOffset slotUtc, int anchorWeek) =>
        schedule switch
        {
            DailySchedule daily => FollowingDaily(daily, slotUtc),
            WeeklySchedule weekly => FollowingWeekly(weekly, slotUtc, anchorWeek),
            _ => null
        };

    private static DateTimeOffset? FollowingDaily(DailySchedule schedule, DateTimeOffset slotUtc)
    {
        var zone = TriggerScheduleCalculator.RequireZone(schedule.TimeZoneId);
        var date = LocalDate(zone, slotUtc).AddDays(schedule.IntervalDays);
        if (schedule.EndDate is DateOnly end && date > end)
        {
            return null;
        }

        var instant = ResolveWall(zone, date, schedule.LocalTime);
        return instant > slotUtc ? instant : null;
    }

    private static DateTimeOffset? FollowingWeekly(WeeklySchedule schedule, DateTimeOffset slotUtc, int anchorWeek)
    {
        var zone = TriggerScheduleCalculator.RequireZone(schedule.TimeZoneId);
        var date = LocalDate(zone, slotUtc).AddDays(1);
        var selected = schedule.Weekdays.ToHashSet();
        var window = (7 * schedule.IntervalWeeks) + 7;
        for (var step = 0; step < window; step++)
        {
            if (schedule.EndDate is DateOnly end && date > end)
            {
                return null;
            }

            if (selected.Contains(date.DayOfWeek))
            {
                var delta = WeekIndex(date) - anchorWeek;
                if (delta >= 0 && delta % schedule.IntervalWeeks == 0)
                {
                    var instant = ResolveWall(zone, date, schedule.LocalTime);
                    if (instant > slotUtc)
                    {
                        return instant;
                    }
                }
            }

            date = date.AddDays(1);
        }

        return null;
    }

    private static bool IsAfterEnd(TriggerSchedule schedule, DateTimeOffset instant)
    {
        DateOnly? end = schedule switch
        {
            DailySchedule daily => daily.EndDate,
            WeeklySchedule weekly => weekly.EndDate,
            _ => null
        };
        if (end is null)
        {
            return false;
        }

        var zone = TriggerScheduleCalculator.RequireZone(TimeZoneId(schedule));
        return LocalDate(zone, instant) > end.Value;
    }

    private static string TimeZoneId(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => oneShot.TimeZoneId,
        DailySchedule daily => daily.TimeZoneId,
        WeeklySchedule weekly => weekly.TimeZoneId,
        _ => "UTC"
    };

    private static DateTimeOffset ResolveWall(TimeZoneInfo zone, DateOnly date, TimeOnly time) =>
        TriggerScheduleCalculator.ResolveWallClock(zone.Id, date, time);

    private static DateOnly LocalDate(TimeZoneInfo zone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static int WeekIndex(DateOnly date)
    {
        var mondayOffset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return (date.DayNumber - mondayOffset) / 7;
    }

    private static string Evidence(TriggerRegistration registration, ScheduleAdmission admission, DateTimeOffset scheduled)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["intent"] = registration.Intent,
            ["scheduleKind"] = registration.Schedule.Kind.ToString(),
            ["scheduledAtUtc"] = scheduled.ToUnixTimeMilliseconds(),
            ["skippedCount"] = admission.SkippedCount,
            ["skippedFromUtc"] = admission.SkippedFromUtc?.ToUnixTimeMilliseconds(),
            ["skippedToUtc"] = admission.SkippedToUtc?.ToUnixTimeMilliseconds()
        }, Json);
        return TriggerText.RequireEvidence(payload);
    }
}
