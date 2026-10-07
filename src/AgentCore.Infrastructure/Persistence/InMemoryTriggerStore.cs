using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryTriggerStore : ITriggerStore
{
    private readonly InMemoryDurableState _state;
    private readonly AgentCore.Infrastructure.Admin.InMemoryAdminEventStore? _admin;

    public ValueTask<Automation> SaveAutomationAsync(Automation proposed, long expectedRevision,
        AgentCore.Application.Admin.AdminEventAppend history, CancellationToken ct = default, int maxActiveRegistrations = 32)
    {
        ct.ThrowIfCancellationRequested();
        lock (_state.Gate)
        {
            var current = Find(proposed.Owner, proposed.AutomationId);
            AutomationRules.ValidateSave(current, proposed, expectedRevision);
            if (proposed.Status == AutomationStatus.Active && current?.Status != AutomationStatus.Active && CountActiveSchedules(proposed.Owner) >= maxActiveRegistrations)
                throw AgentCoreErrors.Validation("Active schedule limit has been reached.");
            AgentCore.Application.Admin.AdminEventSummaryPolicy.ValidateAppend(history);
            _admin?.AppendWithinLock(history);
            _state.Registrations[proposed.AutomationId] = proposed;
            return ValueTask.FromResult(proposed);
        }
    }
    public ValueTask<ScheduledAdmitResult> AdmitAutomationNowAsync(Automation registration, ExecutionModelPin pin,
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        lock (_state.Gate)
        {
            var current = Find(registration.Owner, registration.AutomationId);
            if (current?.Revision != registration.Revision || current.Status != AutomationStatus.Active)
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.Stale, current, null, 0));
            if (RegistrationBusy(registration.AutomationId))
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, current, null, 0));
            var occurrence = AutomationRules.ManualOccurrence(current!, pin, asOf);

            var result = AdmitOccurrenceAsync(occurrence, ct).Result;
            return ValueTask.FromResult(new ScheduledAdmitResult(result.Kind == TriggerOccurrenceAdmitKind.Admitted
                ? ScheduledAdmitOutcome.Admitted : ScheduledAdmitOutcome.Duplicate, current, result.Occurrence, 0));
        }
    }
    private bool RegistrationBusy(Guid automationId) => _state.Occurrences.Values.Any(o => o.AutomationId == automationId
        && (o.Disposition is OccurrenceRoutingDisposition.Pending or OccurrenceRoutingDisposition.Claimed or OccurrenceRoutingDisposition.AwaitingDurableWork
            || o.DurableWorkItemId is Guid id && _state.WorkItems.TryGetValue(id, out var item) && !item.IsTerminal));

    public InMemoryTriggerStore()
        : this(new InMemoryDurableState())
    {
    }

    internal (int Registrations, int Occurrences) CountForInstance(Guid agentInstanceId)
    {
        lock (_state.Gate)
        {
            var deletedAutomations = DeletedAutomationIds(agentInstanceId);
            var registrations = _state.Registrations.Values.Count(item => item.Owner.AgentInstanceId == agentInstanceId && !deletedAutomations.Contains(item.AutomationId));
            var occurrences = _state.Occurrences.Values.Count(item => item.Owner.AgentInstanceId == agentInstanceId
                && !(item.AutomationId is Guid id && deletedAutomations.Contains(id)
                    && item.Disposition == OccurrenceRoutingDisposition.Rejected && item.DurableWorkItemId is null));
            return (registrations, occurrences);
        }
    }

    private HashSet<Guid> DeletedAutomationIds(Guid instanceId) => _state.Registrations.Values.Where(r => r.Owner.AgentInstanceId == instanceId
        && r.Status == AutomationStatus.Cancelled)
        .Select(r => r.AutomationId).ToHashSet();

    internal void PurgeDeletedAutomations(Guid instanceId)
    {
        lock (_state.Gate)
        {
            var ids = DeletedAutomationIds(instanceId);
            foreach (var occurrence in _state.Occurrences.Values.Where(o => o.Owner.AgentInstanceId == instanceId && o.AutomationId is Guid id && ids.Contains(id)
 && o.Disposition == OccurrenceRoutingDisposition.Rejected
                && o.DurableWorkItemId is null).ToArray())
                _state.Occurrences.Remove(occurrence.OccurrenceId);
            foreach (var id in ids) _state.Registrations.Remove(id);
        }
    }

    internal InMemoryTriggerStore(InMemoryDurableState state, AgentCore.Infrastructure.Admin.InMemoryAdminEventStore? admin = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _admin = admin;
    }

    public ValueTask<Automation> CreateAsync(
        Automation registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_state.Gate)
        {
            if (_state.Registrations.ContainsKey(registration.AutomationId))
            {
                throw AgentCoreErrors.Conflict("Trigger registration already exists.");
            }

            _state.Registrations[registration.AutomationId] = registration;
            return ValueTask.FromResult(registration);
        }
    }

    public ValueTask<Automation?> GetAsync(
        TriggerOwner owner,
        Guid automationId,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            return ValueTask.FromResult(Find(owner, automationId));
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListAsync(
        TriggerOwner owner,
        AutomationStatus? status,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.Registrations.Values
                .Where(item => item.Owner.Equals(owner) && (status is null || item.Status == status))
                .OrderByDescending(item => item.Provenance.CreatedAt)
                .ThenByDescending(item => item.AutomationId)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(items);
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListAutomationsPageAsync(
        TriggerOwner owner, int limit, Guid? before, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var owned = _state.Registrations.Values.Where(item => item.Owner.Equals(owner)).ToArray();
            var anchor = before is Guid id ? owned.FirstOrDefault(item => item.AutomationId == id)
                ?? throw AgentCoreErrors.NotFound("Page cursor was not found.") : null;
            var page = owned.Where(item => anchor is null || item.Provenance.CreatedAt < anchor.Provenance.CreatedAt ||
                item.Provenance.CreatedAt == anchor.Provenance.CreatedAt && item.AutomationId.CompareTo(anchor.AutomationId) < 0)
                .OrderByDescending(item => item.Provenance.CreatedAt).ThenByDescending(item => item.AutomationId)
                .Take(Math.Clamp(limit, 1, 100)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(page);
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListSuspendedPolicyForAgentInstanceAsync(
        Guid agentInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.Registrations.Values
                .Where(item =>
                    item.Owner.AgentInstanceId == agentInstanceId
                    && item.Status == AutomationStatus.SuspendedPolicy)
                .OrderByDescending(item => item.Provenance.CreatedAt)
                .ThenByDescending(item => item.AutomationId)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(items);
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListFutureRegistrationsForAgentInstanceAsync(
        Guid agentInstanceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.Registrations.Values
                .Where(item =>
                    item.Owner.AgentInstanceId == agentInstanceId
                    && item.Status is AutomationStatus.Active or AutomationStatus.SuspendedPolicy)
                .OrderByDescending(item => item.Provenance.CreatedAt)
                .ThenByDescending(item => item.AutomationId)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(items);
        }
    }

    private int CountActiveSchedules(TriggerOwner owner) => _state.Registrations.Values.Count(item =>
        item.Owner.Equals(owner) && item.Status == AutomationStatus.Active);

    public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var count = CountActiveSchedules(owner);
            return ValueTask.FromResult(count);
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListEventSubscriptionsAsync(
        Guid eventSourceId,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.Registrations.Values
                .Where(item =>
                    item.Status == AutomationStatus.Active
                    && item.EventSourceId == eventSourceId
                    && string.Equals(item.EventType, eventType, StringComparison.Ordinal))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(items);
        }
    }

    public ValueTask<Automation> UpdateAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string intent,
        TriggerSchedule schedule,
        DateTimeOffset? nextOccurrenceAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var current = Find(owner, automationId) ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
            var updated = AutomationMutations.Update(
                current,
                expectedRevision,
                intent,
                schedule,
                nextOccurrenceAtUtc,
                expiresAtUtc,
                updatedAt);
            _state.Registrations[automationId] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<Automation> SetModelOverrideAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var current = Find(owner, automationId) ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
            var updated = AutomationMutations.SetModelOverride(
                current,
                expectedRevision,
                catalogKey,
                reasoningEffort,
                updatedAt);
            _state.Registrations[automationId] = updated;
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<Automation> CancelAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var current = Find(owner, automationId) ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
            var cancelled = AutomationMutations.Cancel(current, expectedRevision, cancelledAt);
            _state.Registrations[automationId] = cancelled;
            return ValueTask.FromResult(cancelled);
        }
    }

    public ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrence occurrence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        RequireFreshOccurrence(occurrence);
        lock (_state.Gate)
        {
            if (_state.DedupeKeys.TryGetValue(Dedupe(occurrence.Owner, occurrence.DedupeKey), out var existingId))
            {
                return ValueTask.FromResult(new TriggerOccurrenceAdmitResult(
                    TriggerOccurrenceAdmitKind.Duplicate,
                    _state.Occurrences[existingId]));
            }

            if (_state.Occurrences.ContainsKey(occurrence.OccurrenceId))
            {
                throw AgentCoreErrors.Conflict("Occurrence identifier is already in use.");
            }

            _state.Occurrences[occurrence.OccurrenceId] = occurrence;
            _state.DedupeKeys[Dedupe(occurrence.Owner, occurrence.DedupeKey)] = occurrence.OccurrenceId;
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
        lock (_state.Gate)
        {
            if (!_state.Occurrences.TryGetValue(occurrenceId, out var occurrence) || !occurrence.Owner.Equals(owner))
            {
                return ValueTask.FromResult<TriggerOccurrence?>(null);
            }

            return ValueTask.FromResult<TriggerOccurrence?>(occurrence);
        }
    }

    public ValueTask<IReadOnlyList<Automation>> ListDueAsync(
        DateTimeOffset asOfUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var take = Math.Clamp(limit, 1, TriggerScheduler.DefaultBatchSize);
        lock (_state.Gate)
        {
            var due = _state.Registrations.Values
                .Where(item => item.Status == AutomationStatus.Active
                    && item.NextOccurrenceAtUtc is DateTimeOffset next
                    && next <= asOf)
                .OrderBy(item => item.NextOccurrenceAtUtc)
                .ThenBy(item => item.AutomationId)
                .Take(take)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Automation>>(due);
        }
    }

    public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(TriggerOwner owner, Guid automationId,
        long expectedScheduleRevision, DateTimeOffset expectedNextOccurrenceAtUtc, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default) => TryAdmitScheduledCore(owner, automationId,
            expectedScheduleRevision, expectedNextOccurrenceAtUtc, asOfUtc, cancellationToken);

    private ValueTask<ScheduledAdmitResult> TryAdmitScheduledCore(
        TriggerOwner owner,
        Guid automationId,
        long expectedScheduleRevision,
        DateTimeOffset expectedNextOccurrenceAtUtc,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default, ExecutionModelPin? pin = null)
    {
        var asOf = TriggerScheduleCalculator.Truncate(asOfUtc);
        var expectedNext = TriggerScheduleCalculator.Truncate(expectedNextOccurrenceAtUtc);
        lock (_state.Gate)
        {
            var current = Find(owner, automationId);
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
                    AutomationStatus.SuspendedPolicy,
                    null,
                    current.OccurrenceCount,
                    current.Revision + 1,
                    asOf,
                    "Timezone is unavailable.");
                _state.Registrations[automationId] = suspended;
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.Rejected, suspended, null, 0));
            }

            if (decision.Kind is ScheduleAdmissionKind.NotDue)
            {
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, current, null, 0));
            }

            if (decision.Kind is not ScheduleAdmissionKind.Admit)
            {
                var closed = TriggerScheduleAdmission.Advance(current!, decision, asOf);
                _state.Registrations[automationId] = closed;
                var outcome = decision.Kind == ScheduleAdmissionKind.Expire
                    ? ScheduledAdmitOutcome.Expired
                    : ScheduledAdmitOutcome.Completed;
                return ValueTask.FromResult(new ScheduledAdmitResult(outcome, closed, null, 0));
            }

            if (RegistrationBusy(automationId))
            {
                var coalesced = TriggerScheduleAdmission.Advance(current!, decision with { OccurrenceCount = current!.OccurrenceCount }, asOf);
                _state.Registrations[automationId] = coalesced;
                AgentCore.Application.Observability.RuntimeTelemetry.RecordWork("coalesced");
                return ValueTask.FromResult(new ScheduledAdmitResult(ScheduledAdmitOutcome.NotDue, coalesced, null, decision.SkippedCount + 1));
            }
            var occurrence = TriggerScheduleAdmission.CreateOccurrence(current!, decision, asOf);
            if (pin is not null) occurrence = occurrence.WithModelPin(pin);
            if (_state.DedupeKeys.TryGetValue(Dedupe(occurrence.Owner, occurrence.DedupeKey), out var existingId))
            {
                var existing = _state.Occurrences[existingId];
                var advanced = TriggerScheduleAdmission.Advance(current!, decision, asOf);
                _state.Registrations[automationId] = advanced;
                return ValueTask.FromResult(new ScheduledAdmitResult(
                    ScheduledAdmitOutcome.Duplicate,
                    advanced,
                    existing,
                    decision.SkippedCount));
            }

            var updated = TriggerScheduleAdmission.Advance(current!, decision, asOf);
            _state.Occurrences[occurrence.OccurrenceId] = occurrence;
            _state.DedupeKeys[Dedupe(occurrence.Owner, occurrence.DedupeKey)] = occurrence.OccurrenceId;
            _state.Registrations[automationId] = updated;
            return ValueTask.FromResult(new ScheduledAdmitResult(
                ScheduledAdmitOutcome.Admitted,
                updated,
                occurrence,
                decision.SkippedCount));
        }
    }

    public ValueTask<Automation?> SuspendPolicyAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string reason,
        DateTimeOffset suspendedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var current = Find(owner, automationId);
            if (current is null
                || current.Status != AutomationStatus.Active
                || current.Revision != expectedRevision)
            {
                return ValueTask.FromResult<Automation?>(null);
            }

            var suspended = current.WithScheduleAdvance(
                AutomationStatus.SuspendedPolicy,
                current.NextOccurrenceAtUtc,
                current.OccurrenceCount,
                current.Revision + 1,
                suspendedAt,
                reason);
            _state.Registrations[automationId] = suspended;
            return ValueTask.FromResult<Automation?>(suspended);
        }
    }

    public ValueTask<Automation?> TryReactivatePolicySuspensionAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        DateTimeOffset reactivatedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var current = Find(owner, automationId);
            if (current is null
                || current.Status != AutomationStatus.SuspendedPolicy
                || current.Revision != expectedRevision)
            {
                return ValueTask.FromResult<Automation?>(null);
            }

            DateTimeOffset? next = current.NextOccurrenceAtUtc;
            if (next is null && current.Schedule is OneShotSchedule oneShot)
            {
                next = oneShot.AtUtc;
            }

            next ??= TriggerScheduleCalculator.InitialNext(current.Schedule, reactivatedAt);
            var reactivated = current.WithScheduleAdvance(
                AutomationStatus.Active,
                next,
                current.OccurrenceCount,
                current.Revision + 1,
                reactivatedAt,
                null);
            _state.Registrations[automationId] = reactivated;
            return ValueTask.FromResult<Automation?>(reactivated);
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
        lock (_state.Gate)
        {
            var recovered = 0;
            foreach (var current in _state.Occurrences.Values.ToArray())
            {
                if (current.Disposition != OccurrenceRoutingDisposition.Claimed
                    || current.ClaimLeaseExpiresAtUtc is not DateTimeOffset lease
                    || lease > asOfUtc)
                {
                    continue;
                }

                _state.Occurrences[current.OccurrenceId] = current.WithRouting(
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

    public ValueTask<TriggerOccurrence?> TryAssignModelPinIfMissingAsync(
        Guid occurrenceId,
        ExecutionModelPin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        lock (_state.Gate)
        {
            if (!_state.Occurrences.TryGetValue(occurrenceId, out var current))
            {
                return ValueTask.FromResult<TriggerOccurrence?>(null);
            }

            if (current.ModelPin is not null)
            {
                return ValueTask.FromResult<TriggerOccurrence?>(current);
            }

            var next = current.WithModelPin(pin);
            _state.Occurrences[occurrenceId] = next;
            return ValueTask.FromResult<TriggerOccurrence?>(next);
        }
    }

    public ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(
        OccurrenceRoutingDisposition disposition,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_state.Gate)
        {
            var items = _state.Occurrences.Values
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
        lock (_state.Gate)
        {
            if (!_state.Occurrences.TryGetValue(occurrenceId, out var current))
            {
                return null;
            }

            var next = change(current);
            if (next is null)
            {
                return null;
            }

            _state.Occurrences[occurrenceId] = next;
            return next;
        }
    }

    private static InMemoryDurableState.DedupeIdentity Dedupe(TriggerOwner owner, string dedupeKey) =>
        new(owner.AgentInstanceId, owner.ProfileId, dedupeKey);

    private static bool IsCurrent(
        Automation? current,
        long expectedScheduleRevision,
        DateTimeOffset expectedNext) =>
        current is not null
        && current.Status == AutomationStatus.Active
        && current.ScheduleRevision == expectedScheduleRevision
        && current.NextOccurrenceAtUtc == expectedNext;

    private Automation? Find(TriggerOwner owner, Guid automationId) =>
        _state.Registrations.TryGetValue(automationId, out var registration) && registration.Owner.Equals(owner)
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
