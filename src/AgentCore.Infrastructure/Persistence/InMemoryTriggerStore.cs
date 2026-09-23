using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryTriggerStore : ITriggerStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TriggerRegistration> _registrations = [];
    private readonly Dictionary<Guid, TriggerOccurrence> _occurrences = [];
    private readonly Dictionary<string, Guid> _dedupeKeys = new(StringComparer.Ordinal);

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
                .OrderBy(item => item.Provenance.CreatedAt)
                .ThenBy(item => item.RegistrationId)
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
            if (_dedupeKeys.TryGetValue(occurrence.DedupeKey, out var existingId))
            {
                var existing = _occurrences[existingId];
                if (!existing.Owner.Equals(occurrence.Owner))
                {
                    throw AgentCoreErrors.Conflict("Occurrence identity is already in use.");
                }

                return ValueTask.FromResult(new TriggerOccurrenceAdmitResult(
                    TriggerOccurrenceAdmitKind.Duplicate,
                    existing));
            }

            if (_occurrences.ContainsKey(occurrence.OccurrenceId))
            {
                throw AgentCoreErrors.Conflict("Occurrence identifier is already in use.");
            }

            _occurrences[occurrence.OccurrenceId] = occurrence;
            _dedupeKeys[occurrence.DedupeKey] = occurrence.OccurrenceId;
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
