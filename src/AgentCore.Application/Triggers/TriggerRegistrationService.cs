using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed class TriggerRegistrationService(
    ITriggerStore store,
    IIdGenerator ids,
    TimeProvider time) : ITriggerRegistrationService
{
    public async ValueTask<TriggerRegistration> CreateAsync(
        TriggerRegistrationDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = UtcNow();
        var schedule = draft.Schedule ?? throw AgentCoreErrors.Validation("Schedule is required.");
        var next = draft.NextOccurrenceAtUtc ?? (schedule is OneShotSchedule oneShot ? oneShot.AtUtc : null);
        var registration = Guard(() => new TriggerRegistration(
            ids.NewId(),
            draft.Owner,
            TriggerRegistrationStatus.Active,
            draft.Intent,
            schedule,
            AsUtc(next, "Next occurrence"),
            AsUtc(draft.ExpiresAtUtc, "Expiry"),
            occurrenceCount: 0,
            revision: 1,
            scheduleRevision: 1,
            new TriggerProvenance(
                draft.AuthorizationOrigin,
                draft.SourceSessionId,
                draft.SourceEventId,
                now,
                now),
            suspensionReason: null));
        return await store.CreateAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<TriggerRegistration?> GetAsync(
        TriggerOwner owner,
        Guid registrationId,
        CancellationToken cancellationToken = default) =>
        store.GetAsync(owner, registrationId, cancellationToken);

    public ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(
        TriggerOwner owner,
        TriggerRegistrationStatus? status,
        CancellationToken cancellationToken = default) =>
        store.ListAsync(owner, status, cancellationToken);

    public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default) =>
        store.CountActiveAsync(owner, cancellationToken);

    public async ValueTask<TriggerRegistration> UpdateAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        TriggerRegistrationChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = await store.GetAsync(owner, registrationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status != TriggerRegistrationStatus.Active)
        {
            throw AgentCoreErrors.Validation("Only an active registration can be updated.");
        }

        var intent = change.HasIntent ? change.Intent ?? "" : current.Intent;
        var schedule = change.HasSchedule
            ? change.Schedule ?? throw AgentCoreErrors.Validation("Schedule is required.")
            : current.Schedule;
        var next = change.HasNextOccurrence ? change.NextOccurrenceAtUtc : current.NextOccurrenceAtUtc;
        var expires = change.HasExpiresAt ? change.ExpiresAtUtc : current.ExpiresAtUtc;
        if (!change.HasIntent && !change.HasSchedule && !change.HasNextOccurrence && !change.HasExpiresAt)
        {
            return current;
        }

        return await store.UpdateAsync(
            owner,
            registrationId,
            expectedRevision,
            intent,
            schedule,
            AsUtc(next, "Next occurrence"),
            AsUtc(expires, "Expiry"),
            UtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<TriggerRegistration> CancelAsync(
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        store.CancelAsync(owner, registrationId, expectedRevision, UtcNow(), cancellationToken);

    public async ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(
        TriggerOccurrenceDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var evidence = string.IsNullOrWhiteSpace(draft.EvidenceJson) ? "{}" : draft.EvidenceJson;
        ValidateEvidenceJson(evidence);
        var now = UtcNow();
        var occurrence = Guard(() => new TriggerOccurrence(
            ids.NewId(),
            draft.DedupeKey,
            draft.RegistrationId,
            draft.Owner,
            draft.SourceKind,
            AsUtc(draft.ScheduledAtUtc, "Scheduled"),
            AsUtc(draft.ObservedAtUtc, "Observed"),
            now,
            evidence,
            draft.SourceEventId,
            draft.ScheduleRevision,
            OccurrenceRoutingDisposition.Pending,
            dispositionReason: null,
            routingRevision: 0,
            routingUpdatedAtUtc: null,
            claimId: null,
            claimLeaseExpiresAtUtc: null));
        return await store.AdmitOccurrenceAsync(occurrence, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<TriggerOccurrence?> GetOccurrenceAsync(
        TriggerOwner owner,
        Guid occurrenceId,
        CancellationToken cancellationToken = default) =>
        store.GetOccurrenceAsync(owner, occurrenceId, cancellationToken);

    private DateTimeOffset UtcNow()
    {
        var now = time.GetUtcNow();
        return DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset AsUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw AgentCoreErrors.Validation($"{name} timestamp must be UTC.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset? AsUtc(DateTimeOffset? value, string name) =>
        value is null ? null : AsUtc(value.Value, name);

    private static void ValidateEvidenceJson(string evidence)
    {
        try
        {
            using var document = JsonDocument.Parse(evidence);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                throw AgentCoreErrors.Validation("Occurrence evidence must be a JSON object or array.");
            }
        }
        catch (JsonException)
        {
            throw AgentCoreErrors.Validation("Occurrence evidence must be JSON.");
        }
    }

    private static T Guard<T>(Func<T> factory)
    {
        try
        {
            return factory();
        }
        catch (ArgumentException exception)
        {
            throw AgentCoreErrors.Validation(exception.Message);
        }
    }
}
