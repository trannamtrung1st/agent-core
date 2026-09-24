namespace AgentCore.Domain.Triggers;

public static class TriggerLimits
{
    public const int MaxIntentCharacters = 500;
    public const int MaxTimeZoneCharacters = 64;
    public const int MaxSuspensionReasonCharacters = 200;
    public const int MaxDedupeKeyCharacters = 200;
    public const int MaxEvidenceBytes = 4096;
    public const int MinDailyInterval = 1;
    public const int MaxDailyInterval = 365;
    public const int MinWeeklyInterval = 1;
    public const int MaxWeeklyInterval = 52;
    public const int MaxWeekdays = 7;
    public const int MinMaxOccurrences = 1;
    public const int MaxMaxOccurrences = 366;
    public const int MinFixedIntervalSeconds = 60;
    public const int MaxFixedIntervalSeconds = 604_800;
}

public enum TriggerRegistrationStatus
{
    Active = 0,
    Completed = 1,
    Cancelled = 2,
    Expired = 3,
    SuspendedPolicy = 4
}

public enum TriggerScheduleKind
{
    OneShot = 0,
    Daily = 1,
    Weekly = 2,
    FixedInterval = 3
}

public enum TriggerSourceKind
{
    Schedule = 0,
    ApplicationEvent = 1
}

public enum TriggerAuthorizationOrigin
{
    CurrentUserTurn = 0
}

public enum OccurrenceRoutingDisposition
{
    Pending = 0,
    Claimed = 1,
    AcceptedLive = 2,
    AwaitingDurableWork = 3,
    Rejected = 4,
    LivePrepared = 5,
    AcceptedDurable = 6
}

public readonly record struct TriggerOwner
{
    public TriggerOwner(Guid agentInstanceId, Guid profileId)
    {
        if (agentInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Trigger owner requires an Agent Instance.", nameof(agentInstanceId));
        }

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Trigger owner requires a trusted profile.", nameof(profileId));
        }

        AgentInstanceId = agentInstanceId;
        ProfileId = profileId;
    }

    public Guid AgentInstanceId { get; }

    public Guid ProfileId { get; }
}

public static class TriggerTimeZone
{
    public static string Require(string? timeZoneId)
    {
        if (!IsIanaTimeZoneId(timeZoneId))
        {
            throw new ArgumentException("Timezone must be UTC or an IANA identifier.");
        }

        return timeZoneId!;
    }

    public static bool IsIanaTimeZoneId(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > TriggerLimits.MaxTimeZoneCharacters)
        {
            return false;
        }

        if (timeZoneId is "UTC" or "GMT")
        {
            return true;
        }

        var segments = timeZoneId.Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        if (!char.IsAsciiLetter(segments[0][0]))
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > TriggerLimits.MaxTimeZoneCharacters)
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '+' or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

public static class TriggerText
{
    public static string RequireIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            throw new ArgumentException("Intent is required.");
        }

        var trimmed = intent.Trim();
        if (trimmed.Length > TriggerLimits.MaxIntentCharacters || HasControlCharacter(trimmed))
        {
            throw new ArgumentException("Intent must be 1-500 characters without control characters.");
        }

        return trimmed;
    }

    public static string? OptionalReason(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length == 0 || trimmed.Length > TriggerLimits.MaxSuspensionReasonCharacters || HasControlCharacter(trimmed))
        {
            throw new ArgumentException("Suspension reason must be 1-200 characters without control characters.");
        }

        return trimmed;
    }

    public static string RequireDedupeKey(string? dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            throw new ArgumentException("Occurrence dedupe key is required.");
        }

        var trimmed = dedupeKey.Trim();
        if (trimmed.Length > TriggerLimits.MaxDedupeKeyCharacters)
        {
            throw new ArgumentException("Occurrence dedupe key must be 1-200 characters.");
        }

        foreach (var character in trimmed)
        {
            if (character is < '!' or > '~')
            {
                throw new ArgumentException("Occurrence dedupe key must be printable ASCII.");
            }
        }

        return trimmed;
    }

    public static string RequireEvidence(string? evidence)
    {
        if (evidence is null)
        {
            throw new ArgumentException("Occurrence evidence is required.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(evidence) > TriggerLimits.MaxEvidenceBytes)
        {
            throw new ArgumentException("Occurrence evidence must be at most 4 KiB.");
        }

        return evidence;
    }

    private static bool HasControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}

public abstract class TriggerSchedule
{
    public abstract TriggerScheduleKind Kind { get; }

    public abstract bool SemanticEquals(TriggerSchedule? other);

    public static void RequireOccurrenceCap(int? maxOccurrences)
    {
        if (maxOccurrences is null)
        {
            return;
        }

        if (maxOccurrences is < TriggerLimits.MinMaxOccurrences or > TriggerLimits.MaxMaxOccurrences)
        {
            throw new ArgumentException("maxOccurrences must be between 1 and 366.");
        }
    }

    public static void RequireDateOrder(DateOnly? startDate, DateOnly? endDate)
    {
        if (startDate is not null && endDate is not null && endDate < startDate)
        {
            throw new ArgumentException("Schedule end date cannot precede the start date.");
        }
    }
}

public sealed class OneShotSchedule : TriggerSchedule
{
    public OneShotSchedule(
        DateTimeOffset atUtc,
        string timeZoneId,
        DateOnly? localDate = null,
        TimeOnly? localTime = null)
    {
        if (atUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("One-shot instant must be a UTC timestamp.");
        }

        if (localDate.HasValue != localTime.HasValue)
        {
            throw new ArgumentException("One-shot local date and local time must be provided together.");
        }

        AtUtc = atUtc;
        TimeZoneId = TriggerTimeZone.Require(timeZoneId);
        LocalDate = localDate;
        LocalTime = localTime;
    }

    public override TriggerScheduleKind Kind => TriggerScheduleKind.OneShot;

    public DateTimeOffset AtUtc { get; }

    public string TimeZoneId { get; }

    public DateOnly? LocalDate { get; }

    public TimeOnly? LocalTime { get; }

    public override bool SemanticEquals(TriggerSchedule? other) =>
        other is OneShotSchedule schedule
        && AtUtc == schedule.AtUtc
        && string.Equals(TimeZoneId, schedule.TimeZoneId, StringComparison.Ordinal)
        && LocalDate == schedule.LocalDate
        && LocalTime == schedule.LocalTime;
}

public sealed class DailySchedule : TriggerSchedule
{
    public DailySchedule(
        int intervalDays,
        TimeOnly localTime,
        string timeZoneId,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        int? maxOccurrences = null)
    {
        if (intervalDays is < TriggerLimits.MinDailyInterval or > TriggerLimits.MaxDailyInterval)
        {
            throw new ArgumentException("Daily interval must be between 1 and 365 days.");
        }

        RequireDateOrder(startDate, endDate);
        RequireOccurrenceCap(maxOccurrences);
        IntervalDays = intervalDays;
        LocalTime = localTime;
        TimeZoneId = TriggerTimeZone.Require(timeZoneId);
        StartDate = startDate;
        EndDate = endDate;
        MaxOccurrences = maxOccurrences;
    }

    public override TriggerScheduleKind Kind => TriggerScheduleKind.Daily;

    public int IntervalDays { get; }

    public TimeOnly LocalTime { get; }

    public string TimeZoneId { get; }

    public DateOnly? StartDate { get; }

    public DateOnly? EndDate { get; }

    public int? MaxOccurrences { get; }

    public override bool SemanticEquals(TriggerSchedule? other) =>
        other is DailySchedule schedule
        && IntervalDays == schedule.IntervalDays
        && LocalTime == schedule.LocalTime
        && string.Equals(TimeZoneId, schedule.TimeZoneId, StringComparison.Ordinal)
        && StartDate == schedule.StartDate
        && EndDate == schedule.EndDate
        && MaxOccurrences == schedule.MaxOccurrences;
}

public sealed class FixedIntervalSchedule : TriggerSchedule
{
    public FixedIntervalSchedule(
        int intervalSeconds,
        DateTimeOffset anchorAtUtc,
        DateTimeOffset? endAtUtc = null,
        int? maxOccurrences = null)
    {
        if (intervalSeconds is < TriggerLimits.MinFixedIntervalSeconds or > TriggerLimits.MaxFixedIntervalSeconds)
        {
            throw new ArgumentException("Fixed interval must be between 60 seconds and 7 days.");
        }

        if (anchorAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fixed-interval anchor must be a UTC timestamp.");
        }

        if (endAtUtc is DateTimeOffset end && end.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Fixed-interval end must be a UTC timestamp.");
        }

        if (endAtUtc is DateTimeOffset bounded && bounded <= anchorAtUtc)
        {
            throw new ArgumentException("Fixed-interval end must be after the anchor.");
        }

        RequireOccurrenceCap(maxOccurrences);
        IntervalSeconds = intervalSeconds;
        AnchorAtUtc = anchorAtUtc;
        EndAtUtc = endAtUtc;
        MaxOccurrences = maxOccurrences;
    }

    public override TriggerScheduleKind Kind => TriggerScheduleKind.FixedInterval;

    public int IntervalSeconds { get; }

    public DateTimeOffset AnchorAtUtc { get; }

    public DateTimeOffset? EndAtUtc { get; }

    public int? MaxOccurrences { get; }

    public override bool SemanticEquals(TriggerSchedule? other) =>
        other is FixedIntervalSchedule schedule
        && IntervalSeconds == schedule.IntervalSeconds
        && AnchorAtUtc == schedule.AnchorAtUtc
        && EndAtUtc == schedule.EndAtUtc
        && MaxOccurrences == schedule.MaxOccurrences;
}

public sealed class WeeklySchedule : TriggerSchedule
{
    public WeeklySchedule(
        int intervalWeeks,
        IReadOnlyList<DayOfWeek> weekdays,
        TimeOnly localTime,
        string timeZoneId,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        int? maxOccurrences = null)
    {
        if (intervalWeeks is < TriggerLimits.MinWeeklyInterval or > TriggerLimits.MaxWeeklyInterval)
        {
            throw new ArgumentException("Weekly interval must be between 1 and 52 weeks.");
        }

        if (weekdays is null || weekdays.Count == 0)
        {
            throw new ArgumentException("Weekly schedule requires at least one weekday.");
        }

        var ordered = weekdays.Distinct().OrderBy(day => day).ToArray();
        if (ordered.Length != weekdays.Count || ordered.Length > TriggerLimits.MaxWeekdays)
        {
            throw new ArgumentException("Weekly weekdays must be distinct and at most seven.");
        }

        foreach (var weekday in ordered)
        {
            if (!Enum.IsDefined(weekday))
            {
                throw new ArgumentException("Weekly weekday is not valid.");
            }
        }

        RequireDateOrder(startDate, endDate);
        RequireOccurrenceCap(maxOccurrences);
        IntervalWeeks = intervalWeeks;
        Weekdays = ordered;
        LocalTime = localTime;
        TimeZoneId = TriggerTimeZone.Require(timeZoneId);
        StartDate = startDate;
        EndDate = endDate;
        MaxOccurrences = maxOccurrences;
    }

    public override TriggerScheduleKind Kind => TriggerScheduleKind.Weekly;

    public int IntervalWeeks { get; }

    public IReadOnlyList<DayOfWeek> Weekdays { get; }

    public TimeOnly LocalTime { get; }

    public string TimeZoneId { get; }

    public DateOnly? StartDate { get; }

    public DateOnly? EndDate { get; }

    public int? MaxOccurrences { get; }

    public override bool SemanticEquals(TriggerSchedule? other) =>
        other is WeeklySchedule schedule
        && IntervalWeeks == schedule.IntervalWeeks
        && LocalTime == schedule.LocalTime
        && string.Equals(TimeZoneId, schedule.TimeZoneId, StringComparison.Ordinal)
        && StartDate == schedule.StartDate
        && EndDate == schedule.EndDate
        && MaxOccurrences == schedule.MaxOccurrences
        && Weekdays.SequenceEqual(schedule.Weekdays);
}

public sealed class TriggerProvenance
{
    public TriggerProvenance(
        TriggerAuthorizationOrigin authorizationOrigin,
        Guid? sourceSessionId,
        Guid? sourceEventId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (!Enum.IsDefined(authorizationOrigin))
        {
            throw new ArgumentException("Authorization origin is not valid.");
        }

        RequireUtc(createdAt, "Created");
        RequireUtc(updatedAt, "Updated");
        if (updatedAt < createdAt)
        {
            throw new ArgumentException("Updated time cannot precede creation.");
        }

        RequireOptionalId(sourceSessionId, "Source session");
        RequireOptionalId(sourceEventId, "Source event");
        AuthorizationOrigin = authorizationOrigin;
        SourceSessionId = sourceSessionId;
        SourceEventId = sourceEventId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public TriggerAuthorizationOrigin AuthorizationOrigin { get; }

    public Guid? SourceSessionId { get; }

    public Guid? SourceEventId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public TriggerProvenance WithUpdated(DateTimeOffset updatedAt) =>
        new(AuthorizationOrigin, SourceSessionId, SourceEventId, CreatedAt, updatedAt);

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} timestamp must be UTC.");
        }
    }

    private static void RequireOptionalId(Guid? id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException($"{name} identifier is not valid.");
        }
    }
}

public sealed class TriggerRegistration
{
    public TriggerRegistration(
        Guid registrationId,
        TriggerOwner owner,
        TriggerRegistrationStatus status,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        int occurrenceCount,
        long revision,
        long scheduleRevision,
        TriggerProvenance provenance,
        string? suspensionReason)
    {
        if (registrationId == Guid.Empty)
        {
            throw new ArgumentException("Registration identifier is required.", nameof(registrationId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Registration status is not valid.", nameof(status));
        }

        if (schedule is null)
        {
            throw new ArgumentException("Schedule is required.", nameof(schedule));
        }

        if (occurrenceCount < 0)
        {
            throw new ArgumentException("Occurrence count cannot be negative.", nameof(occurrenceCount));
        }

        if (revision < 1 || scheduleRevision < 1)
        {
            throw new ArgumentException("Registration revisions start at 1.");
        }

        RequireUtc(nextOccurrenceAtUtc, "Next occurrence");
        RequireUtc(expiresAtUtc, "Expiry");
        RegistrationId = registrationId;
        Owner = new TriggerOwner(owner.AgentInstanceId, owner.ProfileId);
        Status = status;
        Intent = TriggerText.RequireIntent(intent);
        Schedule = schedule;
        NextOccurrenceAtUtc = nextOccurrenceAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        OccurrenceCount = occurrenceCount;
        Revision = revision;
        ScheduleRevision = scheduleRevision;
        Provenance = provenance ?? throw new ArgumentException("Provenance is required.", nameof(provenance));
        SuspensionReason = TriggerText.OptionalReason(suspensionReason);
    }

    public Guid RegistrationId { get; }

    public TriggerOwner Owner { get; }

    public TriggerRegistrationStatus Status { get; }

    public string Intent { get; }

    public TriggerSchedule Schedule { get; }

    public DateTimeOffset? NextOccurrenceAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public int OccurrenceCount { get; }

    public long Revision { get; }

    public long ScheduleRevision { get; }

    public TriggerProvenance Provenance { get; }

    public string? SuspensionReason { get; }

    public TriggerRegistration WithUpdate(
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        long revision,
        long scheduleRevision,
        DateTimeOffset updatedAt) =>
        new(
            RegistrationId,
            Owner,
            Status,
            intent,
            schedule,
            nextOccurrenceAtUtc,
            expiresAtUtc,
            OccurrenceCount,
            revision,
            scheduleRevision,
            Provenance.WithUpdated(updatedAt),
            SuspensionReason);

    public TriggerRegistration WithScheduleAdvance(
        TriggerRegistrationStatus status,
        DateTimeOffset? nextOccurrenceAtUtc,
        int occurrenceCount,
        long revision,
        DateTimeOffset updatedAt,
        string? suspensionReason) =>
        new(
            RegistrationId,
            Owner,
            status,
            Intent,
            Schedule,
            nextOccurrenceAtUtc,
            ExpiresAtUtc,
            occurrenceCount,
            revision,
            ScheduleRevision,
            Provenance.WithUpdated(updatedAt),
            suspensionReason);

    public TriggerRegistration WithCancellation(long revision, DateTimeOffset cancelledAt) =>
        new(
            RegistrationId,
            Owner,
            TriggerRegistrationStatus.Cancelled,
            Intent,
            Schedule,
            NextOccurrenceAtUtc,
            ExpiresAtUtc,
            OccurrenceCount,
            revision,
            ScheduleRevision,
            Provenance.WithUpdated(cancelledAt),
            SuspensionReason);

    private static void RequireUtc(DateTimeOffset? value, string name)
    {
        if (value is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} timestamp must be UTC.");
        }
    }
}

public sealed class TriggerOccurrence
{
    public TriggerOccurrence(
        Guid occurrenceId,
        string dedupeKey,
        Guid? registrationId,
        TriggerOwner owner,
        TriggerSourceKind sourceKind,
        DateTimeOffset? scheduledAtUtc,
        DateTimeOffset observedAtUtc,
        DateTimeOffset admittedAtUtc,
        string evidenceJson,
        Guid? sourceEventId,
        long? scheduleRevision,
        OccurrenceRoutingDisposition disposition,
        string? dispositionReason,
        long routingRevision,
        DateTimeOffset? routingUpdatedAtUtc,
        Guid? claimId,
        DateTimeOffset? claimLeaseExpiresAtUtc,
        Guid? durableWorkItemId = null)
    {
        if (occurrenceId == Guid.Empty)
        {
            throw new ArgumentException("Occurrence identifier is required.", nameof(occurrenceId));
        }

        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentException("Occurrence source kind is not valid.", nameof(sourceKind));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentException("Routing disposition is not valid.", nameof(disposition));
        }

        if (scheduleRevision is < 1)
        {
            throw new ArgumentException("Schedule revision must be positive.", nameof(scheduleRevision));
        }

        if (routingRevision < 0)
        {
            throw new ArgumentException("Routing revision cannot be negative.", nameof(routingRevision));
        }

        RequireOptionalId(registrationId, "Registration");
        RequireOptionalId(sourceEventId, "Source event");
        RequireOptionalId(claimId, "Claim");
        RequireOptionalId(durableWorkItemId, "Durable work item");
        if (disposition == OccurrenceRoutingDisposition.AcceptedDurable)
        {
            if (durableWorkItemId is null)
            {
                throw new ArgumentException("Accepted durable work requires a work item.", nameof(durableWorkItemId));
            }

            if (claimId is not null || claimLeaseExpiresAtUtc is not null)
            {
                throw new ArgumentException("Accepted durable work cannot hold a routing claim.", nameof(disposition));
            }
        }
        else if (durableWorkItemId is not null)
        {
            throw new ArgumentException("Only accepted durable work can link a work item.", nameof(durableWorkItemId));
        }

        RequireUtc(scheduledAtUtc, "Scheduled");
        RequireUtc(observedAtUtc, "Observed");
        RequireUtc(admittedAtUtc, "Admitted");
        RequireUtc(routingUpdatedAtUtc, "Routing");
        RequireUtc(claimLeaseExpiresAtUtc, "Claim lease");
        OccurrenceId = occurrenceId;
        DedupeKey = TriggerText.RequireDedupeKey(dedupeKey);
        RegistrationId = registrationId;
        Owner = new TriggerOwner(owner.AgentInstanceId, owner.ProfileId);
        SourceKind = sourceKind;
        ScheduledAtUtc = scheduledAtUtc;
        ObservedAtUtc = observedAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        EvidenceJson = TriggerText.RequireEvidence(evidenceJson);
        SourceEventId = sourceEventId;
        ScheduleRevision = scheduleRevision;
        Disposition = disposition;
        DispositionReason = TriggerText.OptionalReason(dispositionReason);
        RoutingRevision = routingRevision;
        RoutingUpdatedAtUtc = routingUpdatedAtUtc;
        ClaimId = claimId;
        ClaimLeaseExpiresAtUtc = claimLeaseExpiresAtUtc;
        DurableWorkItemId = durableWorkItemId;
    }

    public Guid OccurrenceId { get; }

    public string DedupeKey { get; }

    public Guid? RegistrationId { get; }

    public TriggerOwner Owner { get; }

    public TriggerSourceKind SourceKind { get; }

    public DateTimeOffset? ScheduledAtUtc { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset AdmittedAtUtc { get; }

    public string EvidenceJson { get; }

    public Guid? SourceEventId { get; }

    public long? ScheduleRevision { get; }

    public OccurrenceRoutingDisposition Disposition { get; }

    public string? DispositionReason { get; }

    public long RoutingRevision { get; }

    public DateTimeOffset? RoutingUpdatedAtUtc { get; }

    public Guid? ClaimId { get; }

    public DateTimeOffset? ClaimLeaseExpiresAtUtc { get; }

    public Guid? DurableWorkItemId { get; }

    public TriggerOccurrence WithDurableAcceptance(Guid workItemId, long routingRevision, DateTimeOffset acceptedAtUtc)
    {
        if (Disposition != OccurrenceRoutingDisposition.AwaitingDurableWork)
        {
            throw new ArgumentException("Only awaiting durable work can be accepted.", nameof(workItemId));
        }

        if (routingRevision != RoutingRevision + 1)
        {
            throw new ArgumentException("Durable acceptance must advance the routing revision.", nameof(routingRevision));
        }

        return new TriggerOccurrence(
            OccurrenceId,
            DedupeKey,
            RegistrationId,
            Owner,
            SourceKind,
            ScheduledAtUtc,
            ObservedAtUtc,
            AdmittedAtUtc,
            EvidenceJson,
            SourceEventId,
            ScheduleRevision,
            OccurrenceRoutingDisposition.AcceptedDurable,
            null,
            routingRevision,
            acceptedAtUtc,
            null,
            null,
            workItemId);
    }

    public TriggerOccurrence WithRouting(
        OccurrenceRoutingDisposition disposition,
        string? dispositionReason,
        long routingRevision,
        DateTimeOffset routingUpdatedAtUtc,
        Guid? claimId,
        DateTimeOffset? claimLeaseExpiresAtUtc) =>
        new(
            OccurrenceId,
            DedupeKey,
            RegistrationId,
            Owner,
            SourceKind,
            ScheduledAtUtc,
            ObservedAtUtc,
            AdmittedAtUtc,
            EvidenceJson,
            SourceEventId,
            ScheduleRevision,
            disposition,
            dispositionReason,
            routingRevision,
            routingUpdatedAtUtc,
            claimId,
            claimLeaseExpiresAtUtc,
            durableWorkItemId: null);

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} timestamp must be UTC.");
        }
    }

    private static void RequireUtc(DateTimeOffset? value, string name)
    {
        if (value is not null)
        {
            RequireUtc(value.Value, name);
        }
    }

    private static void RequireOptionalId(Guid? id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException($"{name} identifier is not valid.");
        }
    }
}
