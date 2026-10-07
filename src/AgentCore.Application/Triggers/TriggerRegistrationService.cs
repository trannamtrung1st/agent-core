using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed class AutomationService(
    ITriggerStore store,
    IIdGenerator ids,
    TimeProvider time) : IAutomationService
{
    public ValueTask<Automation> CreateAsync(
        AutomationDraft draft,
        CancellationToken cancellationToken = default) =>
        ObserveAsync("create", () => CreateCoreAsync(draft, cancellationToken));

    public ValueTask<Automation?> GetAsync(
        TriggerOwner owner,
        Guid automationId,
        CancellationToken cancellationToken = default) =>
        store.GetAsync(owner, automationId, cancellationToken);

    public ValueTask<IReadOnlyList<Automation>> ListAsync(
        TriggerOwner owner,
        AutomationStatus? status,
        CancellationToken cancellationToken = default) =>
        store.ListAsync(owner, status, cancellationToken);

    public ValueTask<IReadOnlyList<Automation>> ListAutomationsPageAsync(
        TriggerOwner owner, int limit, Guid? before, CancellationToken cancellationToken = default) =>
        store.ListAutomationsPageAsync(owner, limit, before, cancellationToken);

    public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default) =>
        store.CountActiveAsync(owner, cancellationToken);

    public ValueTask<Automation> UpdateAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        AutomationChange change,
        CancellationToken cancellationToken = default) =>
        ObserveAsync("update", () => UpdateCoreAsync(owner, automationId, expectedRevision, change, cancellationToken));

    public ValueTask<Automation> SetModelOverrideAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        CancellationToken cancellationToken = default) =>
        store.SetModelOverrideAsync(
            owner,
            automationId,
            expectedRevision,
            catalogKey,
            reasoningEffort,
            UtcNow(),
            cancellationToken);

    public ValueTask<Automation> CancelAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(
            "cancel",
            () => store.CancelAsync(owner, automationId, expectedRevision, UtcNow(), cancellationToken));

    private async ValueTask<Automation> CreateCoreAsync(
        AutomationDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = UtcNow();
        var schedule = draft.Schedule ?? throw AgentCoreErrors.Validation("Schedule is required.");
        var next = draft.NextOccurrenceAtUtc ?? (schedule is OneShotSchedule oneShot ? oneShot.AtUtc : null);
        var registration = Guard(() => new Automation(
            ids.NewId(),
            draft.Owner,
            AutomationStatus.Active,
            draft.Instructions,
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
            suspensionReason: null, name: draft.Name));
        return await store.CreateAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Automation> UpdateCoreAsync(
        TriggerOwner owner,
        Guid automationId,
        long expectedRevision,
        AutomationChange change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = await store.GetAsync(owner, automationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Registration revision is stale.");
        }

        if (current.Status != AutomationStatus.Active)
        {
            throw AgentCoreErrors.Validation("Only an active registration can be updated.");
        }

        var intent = change.HasInstructions ? change.Instructions ?? "" : current.Instructions;
        var schedule = change.HasSchedule
            ? change.Schedule ?? throw AgentCoreErrors.Validation("Schedule is required.")
            : current.Schedule;
        var next = change.HasNextOccurrence ? change.NextOccurrenceAtUtc : current.NextOccurrenceAtUtc;
        var expires = change.HasExpiresAt ? change.ExpiresAtUtc : current.ExpiresAtUtc;
        if (!change.HasInstructions && !change.HasSchedule && !change.HasNextOccurrence && !change.HasExpiresAt)
        {
            return current;
        }

        return await store.UpdateAsync(
            owner,
            automationId,
            expectedRevision,
            intent,
            schedule,
            AsUtc(next, "Next occurrence"),
            AsUtc(expires, "Expiry"),
            UtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

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
            draft.AutomationId,
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

    private static async ValueTask<T> ObserveAsync<T>(string operation, Func<ValueTask<T>> action)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            RuntimeTelemetry.RecordAutomation(operation, "succeeded");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentCoreException exception)
        {
            RuntimeTelemetry.RecordAutomation(operation, exception.Code);
            throw;
        }
        catch (Exception)
        {
            RuntimeTelemetry.RecordAutomation(operation, "failed");
            throw;
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
