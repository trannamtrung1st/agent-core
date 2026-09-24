using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryTriggerStore : ITriggerStore
{
    private readonly object _gate = new();
    private readonly record struct DedupeIdentity(Guid AgentInstanceId, Guid ProfileId, string DedupeKey);

    private readonly Dictionary<Guid, TriggerRegistration> _registrations = [];
    private readonly Dictionary<Guid, TriggerOccurrence> _occurrences = [];
    private readonly Dictionary<DedupeIdentity, Guid> _dedupeKeys = [];

    public ValueTask<TriggerRegistration> CreateAsync(
        TriggerRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_registrations.ContainsKey(registration.RegistrationId))
            {
                throw AgentCoreErrors.Conflict("Trigger registration already exists.");
            }

            _registrations[registration.RegistrationId] = registration;
            return ValueTask.FromResult(registration);
        }
    }

    public ValueTask<TriggerRegistration?> GetAsync(
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(Find(owner, registrationId));
        }
    }

    public ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(
        TriggerOwner owner,
        TriggerRegistrationStatus? status,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = _registrations.Values
                .Where(item => item.Owner.Equals(owner) && (status is null || item.Status == status))
                .OrderByDescending(item => item.Provenance.CreatedAt)
                .ThenByDescending(item => item.RegistrationId)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<TriggerRegistration>>(items);
        }
    }

    public ValueTask<IReadOnlyList<TriggerRegistration>> ListSuspendedPolicyForAgentInstanceAsync(
        Guid agentInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = _registrations.Values
                .Where(item =>
                    item.Owner.AgentInstanceId == agentInstanceId
                    && item.Status == TriggerRegistrationStatus.SuspendedPolicy)
                .OrderByDescending(item => item.Provenance.CreatedAt)
                .ThenByDescending(item => item.RegistrationId)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<TriggerRegistration>>(items);
        }
    }

    public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var count = _registrations.Values.Count(item =>
                item.Owner.Equals(owner) && item.Status == TriggerRegistrationStatus.Active);
            return ValueTask.FromResult(count);
        }
    }

    public ValueTask<TriggerRegistration> UpdateAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = Find(owner, registrationId) ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
            var updated = TriggerRegistrationMutations.Update(
                current,
                expectedRevision,
                intent,
                schedule,
                nextOccurrenceAtUtc,
                expiresAtUtc,
                updatedAt);
            _registrations[registrationId] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<TriggerRegistration> CancelAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = Find(owner, registrationId) ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
            var cancelled = TriggerRegistrationMutations.Cancel(current, expectedRevision, cancelledAt);
            _registrations[registrationId] = cancelled;
            return ValueTask.FromResult(cancelled);
        }
    }

    public ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        RequireFreshOccurrence(occurrence);
        lock (_gate)
        {
            if (_dedupeKeys.TryGetValue(Dedupe(occurrence.Owner, occurrence.DedupeKey), out var existingId))
            {
                return ValueTask.FromResult(new TriggerOccurrenceAdmitResult(
                    TriggerOccurrenceAdmitKind.Duplicate,
                    _occurrences[existingId]));
            }

            if (_occurrences.ContainsKey(occurrence.OccurrenceId))
            {
                throw AgentCoreErrors.Conflict("Occurrence identifier is already in use.");
            }

            _occurrences[occurrence.OccurrenceId] = occurrence;
            _dedupeKeys[Dedupe(occurrence.Owner, occurrence.DedupeKey)] = occurrence.OccurrenceId;
            return ValueTask.FromResult(new TriggerOccurrenceAdmitResult(
                TriggerOccurrenceAdmitKind.Admitted,
                occurrence));
        }
    }

    public ValueTask<TriggerOccurrence?> GetOccurrenceAsync(
        TriggerOwner owner,
        Guid occurrenceId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence) || !occurrence.Owner.Equals(owner))
            {
                return ValueTask.FromResult<TriggerOccurrence?>(null);
            }

            return ValueTask.FromResult<TriggerOccurrence?>(occurrence);
        }
    }

    public ValueTask<IReadOnlyList<TriggerRegistration>> ListDueAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var take = Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize);
        lock (_gate)
        {
            var due = _registrations.Values
                .Where(item => item.Status == TriggerRegistrationStatus.Active
                    && item.NextOccurrenceAtUtc is DateTimeOffset next
                    && next <= asOf)
                .OrderBy(item => item.NextOccurrenceAtUtc)
                .ThenBy(item => item.RegistrationId)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<TriggerRegistration>>(due);
        }
    }

    public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedScheduleRevision,
        DateTimeOffset expectedNextOccurrenceAtUtc,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var expectedNext = TriggerScheduleCalculator.Truncate(expectedNextOccurrenceAtUtc);
        lock (_gate)
        {
            var current = Find(owner, registrationId);
            if (!IsCurrent(current, expectedScheduleRevision, expectedNext))
            {
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.Stale, current, null, 0));
            }

            ScheduleAdmission decision;
            try
            {
                decision = TriggerScheduleAdmission.Decide(current!, asOf);
            }
            catch (TriggerTimeZoneUnavailableException)
            {
                var suspended = current!.WithScheduleAdvance(
                    TriggerRegistrationStatus.SuspendedPolicy,
                    null,
                    current.OccurrenceCount,
                    current.Revision + 1,
                    asOf,
                    "Timezone is unavailable.");
                _registrations[registrationId] = suspended;
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.Rejected, suspended, null, 0));
            }

            if (decision.Kind is ScheduleAdmissionKind.NotDue)
            {
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, current, null, 0));
            }

            if (decision.Kind is not ScheduleAdmissionKind.Admit)
            {
                var closed = TriggerScheduleAdmission.Advance(current!, decision, asOf);
                _registrations[registrationId] = closed;
                var outcome = decision.Kind == ScheduleAdmissionKind.Expire
                    ? ScheduledAdmitOutcome.Expired
                    : ScheduledAdmitOutcome.Completed;
                return ValueTask.FromResult(new ScheduledAdmitResult(outcome, closed, null, 0));
            }

            var occurrence = TriggerScheduleAdmission.CreateOccurrence(current!, decision, asOf);
            if (_dedupeKeys.TryGetValue(Dedupe(occurrence.Owner, occurrence.DedupeKey), out var existingId))
            {
                var existing = _occurrences[existingId];
                var advanced = TriggerScheduleAdmission.Advance(current!, decision, asOf);
                _registrations[registrationId] = advanced;
                return ValueTask.FromResult(new ScheduledAdmitResult(
                    ScheduledAdmitOutcome.Duplicate,
                    advanced,
                    existing,
                    decision.SkippedCount));
            }

            var updated = TriggerScheduleAdmission.Advance(current!, decision, asOf);
            _occurrences[occurrence.OccurrenceId] = occurrence;
            _dedupeKeys[Dedupe(occurrence.Owner, occurrence.DedupeKey)] = occurrence.OccurrenceId;
            _registrations[registrationId] = updated;
            return ValueTask.FromResult(new ScheduledAdmitResult(
                ScheduledAdmitOutcome.Admitted,
                updated,
                occurrence,
                decision.SkippedCount));
        }
    }

    public ValueTask<TriggerRegistration?> SuspendPolicyAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        string reason,
        DateTimeOffset suspendedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = Find(owner, registrationId);
            if (current is null
                || current.Status != TriggerRegistrationStatus.Active
                || current.Revision != expectedRevision)
            {
                return ValueTask.FromResult<TriggerRegistration?>(null);
            }

            var suspended = current.WithScheduleAdvance(
                TriggerRegistrationStatus.SuspendedPolicy,
                current.NextOccurrenceAtUtc,
                current.OccurrenceCount,
                current.Revision + 1,
                suspendedAt,
                reason);
            _registrations[registrationId] = suspended;
            return ValueTask.FromResult<TriggerRegistration?>(suspended);
        }
    }

    public ValueTask<TriggerRegistration?> TryReactivatePolicySuspensionAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset reactivatedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = Find(owner, registrationId);
            if (current is null
                || current.Status != TriggerRegistrationStatus.SuspendedPolicy
                || current.Revision != expectedRevision)
            {
                return ValueTask.FromResult<TriggerRegistration?>(null);
            }

            DateTimeOffset? next = current.NextOccurrenceAtUtc;
            if (next is null && current.Schedule is OneShotSchedule oneShot)
            {
                next = oneShot.AtUtc;
            }

            next ??= TriggerScheduleCalculator.InitialNext(current.Schedule, reactivatedAt);
            var reactivated = current.WithScheduleAdvance(
                TriggerRegistrationStatus.Active,
                next,
                current.OccurrenceCount,
                current.Revision + 1,
                reactivatedAt,
                null);
            _registrations[registrationId] = reactivated;
            return ValueTask.FromResult<TriggerRegistration?>(reactivated);
        }
    }

    public ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
        {
            var expired = current.Disposition == OccurrenceRoutingDisposition.Claimed
                && current.ClaimLeaseExpiresAtUtc is DateTimeOffset lease
                && lease <= claimedAt;
            if (current.Disposition != OccurrenceRoutingDisposition.Pending && !expired)
            {
                return null;
            }

            return current.WithRouting(
                OccurrenceRoutingDisposition.Claimed,
                null,
                current.RoutingRevision + 1,
                claimedAt,
                claimId,
                leaseExpiresAtUtc);
        }));

    public ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.LivePrepared,
                    null,
                    current.RoutingRevision + 1,
                    acceptedAt,
                    null,
                    acceptedAt.Add(TriggerOccurrenceRouter.LivePreparedLease))
                : null));

    public ValueTask<TriggerOccurrence?> ConfirmLiveBeginAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.LivePrepared
            && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.AcceptedLive,
                    null,
                    current.RoutingRevision + 1,
                    confirmedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> RevertLivePreparedAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.LivePrepared
            && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.Pending,
                    null,
                    current.RoutingRevision + 1,
                    revertedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> PromoteLivePreparedAwaitingDurableWorkAsync(
        Guid occurrenceId,
        long expectedRoutingRevision,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.LivePrepared
            && current.RoutingRevision == expectedRoutingRevision
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.AwaitingDurableWork,
                    reason,
                    current.RoutingRevision + 1,
                    markedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> RevertAcceptedLiveAsync(
        Guid occurrenceId,
        DateTimeOffset revertedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.LivePrepared
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.Pending,
                    null,
                    current.RoutingRevision + 1,
                    revertedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> ReleaseClaimAsync(
        Guid occurrenceId,
        Guid claimId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.Pending,
                    null,
                    current.RoutingRevision + 1,
                    releasedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> MarkAwaitingDurableWorkAsync(
        Guid occurrenceId,
        Guid claimId,
        string reason,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.Claimed && current.ClaimId == claimId
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.AwaitingDurableWork,
                    reason,
                    current.RoutingRevision + 1,
                    markedAt,
                    null,
                    null)
                : null));

    public ValueTask<TriggerOccurrence?> TryRejectPendingAsync(
        Guid occurrenceId,
        string reason,
        DateTimeOffset rejectedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Mutate(occurrenceId, current =>
            current.Disposition == OccurrenceRoutingDisposition.Pending
                ? current.WithRouting(
                    OccurrenceRoutingDisposition.Rejected,
                    reason,
                    current.RoutingRevision + 1,
                    rejectedAt,
                    null,
                    null)
                : null));

    public ValueTask<int> RecoverExpiredClaimsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var recovered = 0;
            foreach (var current in _occurrences.Values.ToArray())
            {
                if (current.Disposition != OccurrenceRoutingDisposition.Claimed
                    || current.ClaimLeaseExpiresAtUtc is not DateTimeOffset lease
                    || lease > asOfUtc)
                {
                    continue;
                }

                _occurrences[current.OccurrenceId] = current.WithRouting(
                    OccurrenceRoutingDisposition.Pending,
                    null,
                    current.RoutingRevision + 1,
                    asOfUtc,
                    null,
                    null);
                recovered++;
            }

            return ValueTask.FromResult(recovered);
        }
    }

    public ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(
        OccurrenceRoutingDisposition disposition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var items = _occurrences.Values
                .Where(item => item.Disposition == disposition)
                .OrderBy(item => item.AdmittedAtUtc)
                .ThenBy(item => item.OccurrenceId)
                .Take(Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<TriggerOccurrence>>(items);
        }
    }

    private TriggerOccurrence? Mutate(Guid occurrenceId, Func<TriggerOccurrence, TriggerOccurrence?> change)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var current))
            {
                return null;
            }

            var next = change(current);
            if (next is null)
            {
                return null;
            }

            _occurrences[occurrenceId] = next;
            return next;
        }
    }

    private static DedupeIdentity Dedupe(TriggerOwner owner, string dedupeKey) =>
        new(owner.AgentInstanceId, owner.ProfileId, dedupeKey);

    private static bool IsCurrent(
        TriggerRegistration? current,
        long expectedScheduleRevision,
        DateTimeOffset expectedNext) =>
        current is not null
        && current.Status == TriggerRegistrationStatus.Active
        && current.ScheduleRevision == expectedScheduleRevision
        && current.NextOccurrenceAtUtc == expectedNext;

    private TriggerRegistration? Find(TriggerOwner owner, Guid registrationId) =>
        _registrations.TryGetValue(registrationId, out var registration) && registration.Owner.Equals(owner)
            ? registration
            : null;

    private static void RequireFreshOccurrence(TriggerOccurrence occurrence)
    {
        if (occurrence.Disposition != OccurrenceRoutingDisposition.Pending
            || occurrence.RoutingRevision != 0
            || occurrence.ClaimId is not null
            || occurrence.ClaimLeaseExpiresAtUtc is not null)
        {
            throw AgentCoreErrors.Validation("A new occurrence must start pending and unclaimed.");
        }
    }
}
