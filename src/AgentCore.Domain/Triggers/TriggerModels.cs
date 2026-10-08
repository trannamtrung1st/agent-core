using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Triggers;

public static class TriggerLimits
{
    public const int MaxInstructionsCharacters = 2000;
    public const int MaxTimeZoneCharacters = 64;
    public const int MaxSuspensionReasonCharacters = 200;
    public const int MaxDedupeKeyCharacters = 200;
    public const int MaxEvidenceBytes = 8192;
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

public enum AutomationStatus
{
    Active = 0,
    Completed = 1,
    Cancelled = 2,
    Expired = 3,
    SuspendedPolicy = 4,
    Disabled = 5
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
    ApplicationEvent = 1,
    ManualInvocation = 2
}

public enum TriggerAuthorizationOrigin
{
    CurrentUserTurn = 0,
    AdminOwner = 2,
    ApplicationEvent = 3
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
    public static string RequireInstructions(string value) => AutomationText.RequireInstructions(value);

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
            throw new ArgumentException($"Occurrence evidence must be at most {TriggerLimits.MaxEvidenceBytes} bytes.");
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
            throw new ArgumentException($"Fixed interval must be between {TriggerLimits.MinFixedIntervalSeconds} seconds and {TimeSpan.FromSeconds(TriggerLimits.MaxFixedIntervalSeconds).TotalDays} days.");
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

public sealed class Automation
{
    public Automation(
        Guid automationId,
        TriggerOwner owner,
        AutomationStatus status,
        string instructions,
        AutomationTrigger trigger,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        int occurrenceCount,
        long revision,
        long triggerRevision,
        TriggerProvenance provenance,
        string? suspensionReason,
        string? modelOverrideCatalogKey = null,
        string? modelOverrideReasoningEffort = null,
        bool requiresVision = false,
        string? name = null,
        AutomationExecutionTarget? executionTarget = null,
        AutomationCompletionDelivery? completionDelivery = null, bool requiresTools = false)
    {
        RequiresTools = requiresTools;
        if (automationId == Guid.Empty)
        {
            throw new ArgumentException("Registration identifier is required.", nameof(automationId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Registration status is not valid.", nameof(status));
        }

        ArgumentNullException.ThrowIfNull(trigger);

        if (occurrenceCount < 0)
        {
            throw new ArgumentException("Occurrence count cannot be negative.", nameof(occurrenceCount));
        }

        if (revision < 1 || triggerRevision < 1)
        {
            throw new ArgumentException("Registration revisions start at 1.");
        }

        RequireUtc(nextOccurrenceAtUtc, "Next occurrence");
        RequireUtc(expiresAtUtc, "Expiry");
        AutomationId = automationId;
        Owner = new TriggerOwner(owner.AgentInstanceId, owner.ProfileId);
        Status = status;
        Instructions = AutomationText.RequireInstructions(instructions);
        Name = AutomationText.RequireName(name ?? Instructions[..Math.Min(Instructions.Length, 80)]);
        Trigger = trigger;
        if (trigger is EventTrigger && nextOccurrenceAtUtc is not null)
            throw new ArgumentException("Event Automations cannot have a scheduled occurrence.");
        NextOccurrenceAtUtc = nextOccurrenceAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        OccurrenceCount = occurrenceCount;
        Revision = revision;
        TriggerRevision = triggerRevision;
        Provenance = provenance ?? throw new ArgumentException("Provenance is required.", nameof(provenance));
        SuspensionReason = TriggerText.OptionalReason(suspensionReason);
        ModelOverrideCatalogKey = OptionalModelToken(modelOverrideCatalogKey, AgentRunLimits.MaxModelFieldCharacters, "Model override");
        ModelOverrideReasoningEffort = OptionalModelToken(
            modelOverrideReasoningEffort,
            AgentRunLimits.MaxReasoningEffortCharacters,
            "Model override reasoning effort");
        RequiresVision = requiresVision;
        ExecutionTarget = executionTarget ?? AutomationExecutionTarget.Background;
        CompletionDelivery = completionDelivery ?? AutomationCompletionDelivery.None;
        CompletionDelivery.ValidateFor(ExecutionTarget);

    }

    public Guid AutomationId { get; }

    public TriggerOwner Owner { get; }

    public AutomationStatus Status { get; }

    public string Name { get; }

    public string Instructions { get; }

    public AutomationTrigger Trigger { get; }

    // Timing is accessed only by the internal schedule machinery.
    public TriggerSchedule Schedule => Trigger is ScheduleTrigger scheduled
        ? scheduled.Schedule : throw new InvalidOperationException("Event triggers have no schedule.");

    public DateTimeOffset? NextOccurrenceAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public int OccurrenceCount { get; }

    public long Revision { get; }

    public long TriggerRevision { get; }

    public TriggerProvenance Provenance { get; }

    public string? SuspensionReason { get; }

    public string? ModelOverrideCatalogKey { get; }

    public string? ModelOverrideReasoningEffort { get; }

    public AutomationExecutionTarget ExecutionTarget { get; }
    public AutomationCompletionDelivery CompletionDelivery { get; }

    public bool RequiresVision { get; }
    public bool RequiresTools { get; }

    public Guid? EventId => (Trigger as EventTrigger)?.EventId;


    public Automation WithUpdate(
        string instructions,
        AutomationTrigger trigger,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        long revision,
        long triggerRevision,
        DateTimeOffset updatedAt) =>
        new(
            AutomationId,
            Owner,
            Status,
            instructions,
            trigger,
            nextOccurrenceAtUtc,
            expiresAtUtc,
            OccurrenceCount,
            revision,
            triggerRevision,
            Provenance.WithUpdated(updatedAt),
            SuspensionReason,
            ModelOverrideCatalogKey,
            ModelOverrideReasoningEffort,
            RequiresVision,
            Name, ExecutionTarget, CompletionDelivery, RequiresTools);

    public Automation WithScheduleAdvance(
        AutomationStatus status,
        DateTimeOffset? nextOccurrenceAtUtc,
        int occurrenceCount,
        long revision,
        DateTimeOffset updatedAt,
        string? suspensionReason) =>
        new(
            AutomationId,
            Owner,
            status,
            Instructions,
            Trigger,
            nextOccurrenceAtUtc,
            ExpiresAtUtc,
            occurrenceCount,
            revision,
            TriggerRevision,
            Provenance.WithUpdated(updatedAt),
            suspensionReason,
            ModelOverrideCatalogKey,
            ModelOverrideReasoningEffort,
            RequiresVision,
            Name, ExecutionTarget, CompletionDelivery, RequiresTools);

    public Automation WithCancellation(long revision, DateTimeOffset cancelledAt) =>
        new(
            AutomationId,
            Owner,
            AutomationStatus.Cancelled,
            Instructions,
            Trigger,
            NextOccurrenceAtUtc,
            ExpiresAtUtc,
            OccurrenceCount,
            revision,
            TriggerRevision,
            Provenance.WithUpdated(cancelledAt),
            SuspensionReason,
            ModelOverrideCatalogKey,
            ModelOverrideReasoningEffort,
            RequiresVision,
            Name, ExecutionTarget, CompletionDelivery, RequiresTools);

    public Automation WithModelOverride(
        string? catalogKey,
        string? reasoningEffort,
        long revision,
        DateTimeOffset updatedAt) =>
        new(
            AutomationId,
            Owner,
            Status,
            Instructions,
            Trigger,
            NextOccurrenceAtUtc,
            ExpiresAtUtc,
            OccurrenceCount,
            revision,
            TriggerRevision,
            Provenance.WithUpdated(updatedAt),
            SuspensionReason,
            catalogKey,
            reasoningEffort,
            RequiresVision,
            Name, ExecutionTarget, CompletionDelivery, RequiresTools);

    private static string? OptionalModelToken(string? value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return AgentRunText.RequireToken(value, max, name);
    }

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
        Guid? automationId,
        TriggerOwner owner,
        TriggerSourceKind sourceKind,
        DateTimeOffset? scheduledAtUtc,
        DateTimeOffset observedAtUtc,
        DateTimeOffset admittedAtUtc,
        string evidenceJson,
        Guid? sourceEventId,
        long? triggerRevision,
        OccurrenceRoutingDisposition disposition,
        string? dispositionReason,
        long routingRevision,
        DateTimeOffset? routingUpdatedAtUtc,
        Guid? claimId,
        DateTimeOffset? claimLeaseExpiresAtUtc,
        ExecutionModelPin? modelPin = null,
        Guid? executionSessionId = null,
        Guid? acceptedAgentRunId = null,
        Guid? liveSessionId = null,
        DateTimeOffset? liveEvaluationCompletedAtUtc = null,
        AutomationExecutionTarget? executionTarget = null, AutomationCompletionDelivery? completionDelivery = null)
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

        if (triggerRevision is < 1)
        {
            throw new ArgumentException("Trigger revision must be positive.", nameof(triggerRevision));
        }

        if (routingRevision < 0)
        {
            throw new ArgumentException("Routing revision cannot be negative.", nameof(routingRevision));
        }

        RequireOptionalId(automationId, "Registration");
        RequireOptionalId(sourceEventId, "Source event");
        RequireOptionalId(claimId, "Claim");
        RequireOptionalId(executionSessionId, "Execution Session");
        RequireOptionalId(acceptedAgentRunId, "Accepted AgentRun");
        RequireOptionalId(liveSessionId, "Live Session");
        RequireUtc(liveEvaluationCompletedAtUtc, "Live evaluation completion");
        if (executionSessionId is not null && (acceptedAgentRunId is null || liveSessionId is not null)
            || acceptedAgentRunId is not null && executionSessionId is null && liveSessionId is null)
            throw new ArgumentException("An occurrence must have exactly one complete execution admission link.");
        if (disposition == OccurrenceRoutingDisposition.AcceptedDurable)
        {
            if (executionSessionId is null || acceptedAgentRunId is null)
            {
                throw new ArgumentException("Accepted background work requires its Session and AgentRun.", nameof(acceptedAgentRunId));
            }

            if (claimId is not null || claimLeaseExpiresAtUtc is not null)
            {
                throw new ArgumentException("Accepted durable work cannot hold a routing claim.", nameof(disposition));
            }
        }
        else if (executionSessionId is not null
            || acceptedAgentRunId is not null && disposition != OccurrenceRoutingDisposition.AcceptedLive)
        {
            throw new ArgumentException("Only accepted work can link a background Session or AgentRun.", nameof(acceptedAgentRunId));
        }

        if (liveSessionId is not null && (automationId is not null
            || disposition is not (OccurrenceRoutingDisposition.LivePrepared or OccurrenceRoutingDisposition.AcceptedLive))
            || liveEvaluationCompletedAtUtc is not null && (liveSessionId is null || disposition != OccurrenceRoutingDisposition.AcceptedLive)
            || liveSessionId is not null && acceptedAgentRunId is not null && liveEvaluationCompletedAtUtc is null)
            throw new ArgumentException("Live acceptance requires a native event, its Session and a completed evaluation before Run linkage.");

        RequireUtc(scheduledAtUtc, "Scheduled");
        RequireUtc(observedAtUtc, "Observed");
        RequireUtc(admittedAtUtc, "Admitted");
        RequireUtc(routingUpdatedAtUtc, "Routing");
        RequireUtc(claimLeaseExpiresAtUtc, "Claim lease");
        ExecutionTarget = executionTarget ?? AutomationExecutionTarget.Background;
        CompletionDelivery = completionDelivery ?? AutomationCompletionDelivery.None;
        CompletionDelivery.ValidateFor(ExecutionTarget);
        OccurrenceId = occurrenceId;
        DedupeKey = TriggerText.RequireDedupeKey(dedupeKey);
        AutomationId = automationId;
        Owner = new TriggerOwner(owner.AgentInstanceId, owner.ProfileId);
        SourceKind = sourceKind;
        ScheduledAtUtc = scheduledAtUtc;
        ObservedAtUtc = observedAtUtc;
        AdmittedAtUtc = admittedAtUtc;
        EvidenceJson = TriggerText.RequireEvidence(evidenceJson);
        SourceEventId = sourceEventId;
        TriggerRevision = triggerRevision;
        Disposition = disposition;
        DispositionReason = TriggerText.OptionalReason(dispositionReason);
        RoutingRevision = routingRevision;
        RoutingUpdatedAtUtc = routingUpdatedAtUtc;
        ClaimId = claimId;
        ClaimLeaseExpiresAtUtc = claimLeaseExpiresAtUtc;
        ModelPin = modelPin;
        ExecutionSessionId = executionSessionId;
        AcceptedAgentRunId = acceptedAgentRunId;
        LiveSessionId = liveSessionId;
        LiveEvaluationCompletedAtUtc = liveEvaluationCompletedAtUtc;
    }

    public AutomationExecutionTarget ExecutionTarget { get; }
    public AutomationCompletionDelivery CompletionDelivery { get; }
    public Guid OccurrenceId { get; }

    public string DedupeKey { get; }

    public Guid? AutomationId { get; }

    public TriggerOwner Owner { get; }

    public TriggerSourceKind SourceKind { get; }

    public DateTimeOffset? ScheduledAtUtc { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset AdmittedAtUtc { get; }

    public string EvidenceJson { get; }

    public Guid? SourceEventId { get; }

    public long? TriggerRevision { get; }

    public OccurrenceRoutingDisposition Disposition { get; }

    public string? DispositionReason { get; }

    public long RoutingRevision { get; }

    public DateTimeOffset? RoutingUpdatedAtUtc { get; }

    public Guid? ClaimId { get; }

    public DateTimeOffset? ClaimLeaseExpiresAtUtc { get; }

    public ExecutionModelPin? ModelPin { get; }

    public Guid? ExecutionSessionId { get; }
    public Guid? AcceptedAgentRunId { get; }
    public Guid? LiveSessionId { get; }
    public DateTimeOffset? LiveEvaluationCompletedAtUtc { get; }

    public TriggerOccurrence WithModelPin(ExecutionModelPin pin) =>
        ModelPin is null
            ? new TriggerOccurrence(
                OccurrenceId,
                DedupeKey,
                AutomationId,
                Owner,
                SourceKind,
                ScheduledAtUtc,
                ObservedAtUtc,
                AdmittedAtUtc,
                EvidenceJson,
                SourceEventId,
                TriggerRevision,
                Disposition,
                DispositionReason,
                RoutingRevision,
                RoutingUpdatedAtUtc,
                ClaimId,
                ClaimLeaseExpiresAtUtc,
                pin, ExecutionSessionId, AcceptedAgentRunId, LiveSessionId, LiveEvaluationCompletedAtUtc, ExecutionTarget, CompletionDelivery)
            : this;

    public TriggerOccurrence WithLiveSession(Guid sessionId, long expectedRevision, DateTimeOffset atUtc)
    {
        if (Disposition != OccurrenceRoutingDisposition.LivePrepared || RoutingRevision != expectedRevision
            || LiveSessionId is not null && LiveSessionId != sessionId)
            throw new ArgumentException("Live preparation requires its current revision and one target Session.");
        return CopyLive(sessionId, null, null, OccurrenceRoutingDisposition.LivePrepared, atUtc);
    }

    public TriggerOccurrence WithLiveEvaluation(Guid sessionId, Guid? agentRunId, DateTimeOffset atUtc)
    {
        if (Disposition is not (OccurrenceRoutingDisposition.LivePrepared or OccurrenceRoutingDisposition.AcceptedLive)
            || LiveSessionId != sessionId || LiveEvaluationCompletedAtUtc is not null)
            throw new ArgumentException("Live evaluation requires its prepared Session and an unfinished receipt.");
        return CopyLive(sessionId, agentRunId, atUtc, OccurrenceRoutingDisposition.AcceptedLive, atUtc);
    }

    private TriggerOccurrence CopyLive(Guid sessionId, Guid? runId, DateTimeOffset? completedAt,
        OccurrenceRoutingDisposition disposition, DateTimeOffset atUtc) =>
        new(OccurrenceId, DedupeKey, AutomationId, Owner, SourceKind, ScheduledAtUtc, ObservedAtUtc,
            AdmittedAtUtc, EvidenceJson, SourceEventId, TriggerRevision, disposition, null,
            RoutingRevision + 1, atUtc, disposition == OccurrenceRoutingDisposition.LivePrepared ? ClaimId : null,
            disposition == OccurrenceRoutingDisposition.LivePrepared ? ClaimLeaseExpiresAtUtc : null,
            modelPin: ModelPin, acceptedAgentRunId: runId, liveSessionId: sessionId,
            liveEvaluationCompletedAtUtc: completedAt, executionTarget: ExecutionTarget, completionDelivery: CompletionDelivery);

    public TriggerOccurrence WithExecutionAcceptance(Guid sessionId, Guid agentRunId, long expectedRevision,
        DateTimeOffset acceptedAtUtc)
    {
        if (Disposition != OccurrenceRoutingDisposition.AwaitingDurableWork || RoutingRevision != expectedRevision)
            throw new ArgumentException("Occurrence admission requires its current awaiting routing revision.");
        return new TriggerOccurrence(OccurrenceId, DedupeKey, AutomationId, Owner, SourceKind, ScheduledAtUtc,
            ObservedAtUtc, AdmittedAtUtc, EvidenceJson, SourceEventId, TriggerRevision,
            OccurrenceRoutingDisposition.AcceptedDurable, null, RoutingRevision + 1, acceptedAtUtc, null, null,
            ModelPin, sessionId, agentRunId, executionTarget: ExecutionTarget, completionDelivery: CompletionDelivery);
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
            AutomationId,
            Owner,
            SourceKind,
            ScheduledAtUtc,
            ObservedAtUtc,
            AdmittedAtUtc,
            EvidenceJson,
            SourceEventId,
            TriggerRevision,
            disposition,
            dispositionReason,
            routingRevision,
            routingUpdatedAtUtc,
            claimId,
            claimLeaseExpiresAtUtc,
            modelPin: ModelPin,
            acceptedAgentRunId: disposition == OccurrenceRoutingDisposition.AcceptedLive ? AcceptedAgentRunId : null,
            liveSessionId: disposition == OccurrenceRoutingDisposition.AcceptedLive ? LiveSessionId : null,
            liveEvaluationCompletedAtUtc: disposition == OccurrenceRoutingDisposition.AcceptedLive ? LiveEvaluationCompletedAtUtc : null,
            executionTarget: ExecutionTarget, completionDelivery: CompletionDelivery);

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
