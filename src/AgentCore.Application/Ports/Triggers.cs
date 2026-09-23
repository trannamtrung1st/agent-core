using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Ports;

public sealed record TriggerRegistrationDraft(
    TriggerOwner Owner,
    string Intent,
    TriggerSchedule Schedule,
    DateTimeOffset? NextOccurrenceAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    TriggerAuthorizationOrigin AuthorizationOrigin,
    Guid? SourceSessionId,
    Guid? SourceEventId);

public sealed record TriggerRegistrationChange
{
    private TriggerRegistrationChange(
        string? intent,
        TriggerSchedule? schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        bool hasIntent,
        bool hasSchedule,
        bool hasNextOccurrence,
        bool hasExpiresAt)
    {
        Intent = intent;
        Schedule = schedule;
        NextOccurrenceAtUtc = nextOccurrenceAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        HasIntent = hasIntent;
        HasSchedule = hasSchedule;
        HasNextOccurrence = hasNextOccurrence;
        HasExpiresAt = hasExpiresAt;
    }

    public string? Intent { get; }

    public TriggerSchedule? Schedule { get; }

    public DateTimeOffset? NextOccurrenceAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool HasIntent { get; }

    public bool HasSchedule { get; }

    public bool HasNextOccurrence { get; }

    public bool HasExpiresAt { get; }

    public static TriggerRegistrationChange IntentOnly(string intent) =>
        new(intent, null, null, null, hasIntent: true, hasSchedule: false, hasNextOccurrence: false, hasExpiresAt: false);

    public static TriggerRegistrationChange ScheduleOnly(
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc) =>
        new(null, schedule, nextOccurrenceAtUtc, expiresAtUtc, hasIntent: false, hasSchedule: true, hasNextOccurrence: true, hasExpiresAt: true);

    public static TriggerRegistrationChange Full(
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc) =>
        new(intent, schedule, nextOccurrenceAtUtc, expiresAtUtc, hasIntent: true, hasSchedule: true, hasNextOccurrence: true, hasExpiresAt: true);
}

public enum TriggerOccurrenceAdmitKind
{
    Admitted = 0,
    Duplicate = 1
}

public sealed record TriggerOccurrenceAdmitResult(
    TriggerOccurrenceAdmitKind Kind,
    TriggerOccurrence Occurrence);

public enum ScheduledAdmitOutcome
{
    Admitted,
    Duplicate,
    Stale,
    NotDue,
    Expired,
    Completed,
    Rejected
}

public sealed record ScheduledAdmitResult(
    ScheduledAdmitOutcome Outcome,
    TriggerRegistration? Registration,
    TriggerOccurrence? Occurrence,
    int SkippedCount);

public sealed record TriggerOccurrenceDraft(
    TriggerOwner Owner,
    string DedupeKey,
    Guid? RegistrationId,
    TriggerSourceKind SourceKind,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset ObservedAtUtc,
    string EvidenceJson,
    Guid? SourceEventId,
    long? ScheduleRevision);

public interface ITriggerStore
{
    ValueTask<TriggerRegistration> CreateAsync(
        TriggerRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration?> GetAsync(
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(
        TriggerOwner owner,
        TriggerRegistrationStatus? status,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountActiveAsync(
        TriggerOwner owner,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration> UpdateAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration> CancelAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> GetOccurrenceAsync(
        TriggerOwner owner,
        Guid occurrenceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TriggerRegistration>> ListDueAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedScheduleRevision,
        DateTimeOffset expectedNextOccurrenceAtUtc,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration?> SuspendPolicyAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string reason,
        DateTimeOffset suspendedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> ConfirmLiveBeginAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> RevertLivePreparedAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> PromoteLivePreparedAwaitingDurableWorkAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> RevertAcceptedLiveAsync(
        Guid occurrenceId,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> ReleaseClaimAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> MarkAwaitingDurableWorkAsync(
        Guid occurrenceId,
        Guid claimId,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> TryRejectPendingAsync(
        Guid occurrenceId,
        string reason,
        DateTimeOffset rejectedAt,
        CancellationToken cancellationToken = default);

    ValueTask<int> RecoverExpiredClaimsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(
        OccurrenceRoutingDisposition disposition,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface ITriggerRegistrationService
{
    ValueTask<TriggerRegistration> CreateAsync(
        TriggerRegistrationDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration?> GetAsync(
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(
        TriggerOwner owner,
        TriggerRegistrationStatus? status,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountActiveAsync(
        TriggerOwner owner,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration> UpdateAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        TriggerRegistrationChange change,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerRegistration> CancelAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrenceDraft draft,
        CancellationToken cancellationToken = default);

    ValueTask<TriggerOccurrence?> GetOccurrenceAsync(
        TriggerOwner owner,
        Guid occurrenceId,
        CancellationToken cancellationToken = default);
}
