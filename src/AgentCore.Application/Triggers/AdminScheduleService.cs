using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

/// <summary>Owner configuration of the same registrations used by conversational scheduling.</summary>
public sealed class AdminScheduleService(ITriggerStore store, ExperienceService experience,
    IAgentDefinitionStore definitions, IModelCatalog catalog, ITriggerAdmissionGuard guard,
    IIdGenerator ids, TimeProvider time, ILocalUserProfileService profiles)
{
    public async ValueTask<TriggerRegistration> SaveAsync(Guid instanceId, Guid? registrationId, long expectedRevision,
        bool enabled, string intent, TriggerSchedule schedule, string? modelKey, string? effort, CancellationToken ct = default)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.Schedule, ct);
        if (enabled && decision.Kind != TriggerAdmissionDecisionKind.Allow) throw AgentCoreErrors.Forbidden(decision.Reason ?? "Scheduling is disabled.");
        var current = registrationId is Guid id ? await store.GetAsync(owner, id, ct) : null;
        if (registrationId is not null && current is null) throw AgentCoreErrors.NotFound("Schedule was not found.");
        var retainingDisabledModel = !enabled && current is not null && current.ModelOverrideCatalogKey == modelKey && current.ModelOverrideReasoningEffort == effort;
        if (!retainingDisabledModel) ExecutionModelPolicy.RequireSelectable(catalog, modelKey, effort);
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var next = enabled ? TriggerScheduleCalculator.InitialNext(schedule, now) : null;
        if (enabled && next is null) throw AgentCoreErrors.Validation("Schedule has no future occurrence.");
        var changed = current is null || !current.Schedule.SemanticEquals(schedule) || current.Status != (enabled ? TriggerRegistrationStatus.Active : TriggerRegistrationStatus.Disabled);
        var proposed = new TriggerRegistration(current?.RegistrationId ?? ids.NewId(), owner,
            enabled ? TriggerRegistrationStatus.Active : TriggerRegistrationStatus.Disabled, intent, schedule,
            changed ? next : current!.NextOccurrenceAtUtc, current?.ExpiresAtUtc, current?.OccurrenceCount ?? 0, expectedRevision + 1,
            (current?.ScheduleRevision ?? 0) + 1,
            current?.Provenance.WithUpdated(now) ?? new(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null, modelKey, effort,
            current?.RequiresVision ?? false);
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
        if (enabled && !ExecutionModelPolicy.Resolve(catalog, definition, instance, proposed).Accepted) throw AgentCoreErrors.Validation("Unattended model is unavailable.");
        return await store.SaveScheduleAsync(proposed, expectedRevision, History(proposed, current is null ? "create" : "update"), ct);
    }
    public async ValueTask DeleteAsync(Guid instanceId, Guid registrationId, long revision, CancellationToken ct = default)
    {
        await experience.RequireInstanceAsync(instanceId, ct);
        var current = await store.GetAsync(new(instanceId, LocalUserProfile.Id), registrationId, ct) ?? throw AgentCoreErrors.NotFound("Schedule was not found.");
        var proposed = current.WithCancellation(revision + 1, time.GetUtcNow());
        await store.SaveScheduleAsync(proposed, revision, History(proposed, "cancel"), ct);
    }
    public async ValueTask<TriggerOccurrence> RunNowAsync(Guid instanceId, Guid registrationId, long revision, CancellationToken ct = default)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var r = await store.GetAsync(owner, registrationId, ct) ?? throw AgentCoreErrors.NotFound("Schedule was not found.");
        if (r.Revision != revision) throw AgentCoreErrors.Conflict("Schedule revision is stale.");
        if (r.EventSourceId is not null || r.Provenance.AuthorizationOrigin == TriggerAuthorizationOrigin.AdminThought || r.Status != TriggerRegistrationStatus.Active)
            throw AgentCoreErrors.Validation("Enable an eligible schedule before running it.");
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.Schedule, ct);
        if (decision.Kind != TriggerAdmissionDecisionKind.Allow) throw AgentCoreErrors.Forbidden(decision.Reason ?? "Scheduling is disabled.");
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
        var model = ExecutionModelPolicy.Resolve(catalog, definition, instance, r);
        if (!model.Accepted || model.Pin is null) throw AgentCoreErrors.Validation("Unattended model is unavailable.");
        var result = await store.AdmitScheduleNowAsync(r, model.Pin, TriggerScheduleCalculator.Truncate(time.GetUtcNow()), ct);
        if (result.Occurrence is null || result.Outcome != ScheduledAdmitOutcome.Admitted)
            throw AgentCoreErrors.Conflict("This schedule already has an admitted run. Wait for it to finish.");
        return result.Occurrence;
    }
    private AdminEventAppend History(TriggerRegistration r, string operation) => new(ids.NewId(), time.GetUtcNow(),
        AdminEventActorKind.LocalOwner, AdminEventOperationKind.ScheduleRegistrationChanged, "agentInstance", r.Owner.AgentInstanceId.ToString("D"), r.Revision, null,
        JsonSerializer.Serialize(new { instanceId = r.Owner.AgentInstanceId, recordId = r.RegistrationId, operation, revision = r.Revision,
            enabled = r.Status == TriggerRegistrationStatus.Active, promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Intent))), modelKey = r.ModelOverrideCatalogKey }));
}
