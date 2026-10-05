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

public sealed class ThoughtRegistrationService(ITriggerStore store, ExperienceService experience,
    IAgentDefinitionStore definitions, IModelCatalog catalog, ITriggerAdmissionGuard guard,
    IIdGenerator ids, TimeProvider time, ILocalUserProfileService profiles)
{
    public async ValueTask<TriggerRegistration> SaveAsync(Guid instanceId, Guid? registrationId, long expectedRevision,
        bool enabled, int intervalSeconds, string prompt, string? modelKey, string? effort, CancellationToken ct = default)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ThoughtActivation, ct);
        if (enabled && decision.Kind != TriggerAdmissionDecisionKind.Allow)
            throw AgentCoreErrors.Forbidden(decision.Reason ?? "Scheduling is disabled for this agent.");
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var current = registrationId is Guid id ? await store.GetAsync(owner, id, ct) : null;
        if (registrationId is not null && current is null) throw AgentCoreErrors.NotFound("Thought registration was not found.");
        // Owners must be able to stop future runs even after a catalog/default model disappears.
        var retainingDisabledModel = !enabled && current is not null
            && string.Equals(modelKey, current.ModelOverrideCatalogKey, StringComparison.Ordinal)
            && string.Equals(effort, current.ModelOverrideReasoningEffort, StringComparison.Ordinal);
        if (!retainingDisabledModel) ExecutionModelPolicy.RequireSelectable(catalog, modelKey, effort);
        var schedule = current?.Schedule is FixedIntervalSchedule s && s.IntervalSeconds == intervalSeconds
            ? s : new FixedIntervalSchedule(intervalSeconds, now.AddSeconds(intervalSeconds));
        var proposed = new TriggerRegistration(current?.RegistrationId ?? ids.NewId(), owner,
            enabled ? TriggerRegistrationStatus.Active : TriggerRegistrationStatus.Disabled, prompt, schedule,
            enabled ? TriggerScheduleCalculator.InitialNext(schedule, now) : null, null, current?.OccurrenceCount ?? 0,
            expectedRevision + 1, (current?.ScheduleRevision ?? 0) + 1,
            new(TriggerAuthorizationOrigin.AdminThought, null, null, current?.Provenance.CreatedAt ?? now, now),
            null, modelKey, effort);
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct)
            ?? throw AgentCoreErrors.NotFound("Current Definition was not found.");
        if (enabled)
        {
            var model = ExecutionModelPolicy.Resolve(catalog, definition, instance, proposed);
            if (!model.Accepted) throw AgentCoreErrors.Validation("Thought activation requires an available tool-capable unattended model.");
        }
        return await store.SaveThoughtAsync(proposed, expectedRevision, History(proposed, current is null ? "create" : "update"), ct);
    }
    public async ValueTask DeleteAsync(Guid instanceId, Guid registrationId, long revision, CancellationToken ct = default)
    {
        await experience.RequireInstanceAsync(instanceId, ct);
        var current = await store.GetAsync(new(instanceId, LocalUserProfile.Id), registrationId, ct)
            ?? throw AgentCoreErrors.NotFound("Thought registration was not found.");
        var proposed = current.WithCancellation(revision + 1, time.GetUtcNow());
        await store.SaveThoughtAsync(proposed, revision, History(proposed, "delete"), ct);
    }
    public async ValueTask<TriggerOccurrence> RunNowAsync(Guid instanceId, Guid registrationId, long revision, CancellationToken ct = default)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var current = await store.GetAsync(owner, registrationId, ct) ?? throw AgentCoreErrors.NotFound("Thought registration was not found.");
        if (current.Revision != revision) throw AgentCoreErrors.Conflict("Thought registration revision is stale.");
        if (current.Provenance.AuthorizationOrigin != TriggerAuthorizationOrigin.AdminThought || current.Status != TriggerRegistrationStatus.Active)
            throw AgentCoreErrors.Validation("Enable this thought before running it.");
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ThoughtActivation, ct);
        if (decision.Kind != TriggerAdmissionDecisionKind.Allow) throw AgentCoreErrors.Forbidden(decision.Reason ?? "Thought activation is unavailable.");
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct)
            ?? throw AgentCoreErrors.NotFound("Current Definition was not found.");
        var model = ExecutionModelPolicy.Resolve(catalog, definition, instance, current);
        if (!model.Accepted || model.Pin is null) throw AgentCoreErrors.Validation("Unattended model is unavailable.");
        // Manual admission uses the identical scheduled occurrence/overlap/snapshot path.
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var result = await store.AdmitThoughtNowAsync(current, model.Pin, now, ct);
        if (result.Occurrence is null) throw AgentCoreErrors.Conflict("This thought already has an active occurrence. Wait for it to finish.");
        return result.Occurrence;
    }
    private AdminEventAppend History(TriggerRegistration r, string operation) => new(ids.NewId(), time.GetUtcNow(),
        AdminEventActorKind.LocalOwner, AdminEventOperationKind.ThoughtRegistrationChanged, "agentInstance", r.Owner.AgentInstanceId.ToString("D"),
        r.Revision, null, JsonSerializer.Serialize(new { instanceId = r.Owner.AgentInstanceId, recordId = r.RegistrationId,
            operation, revision = r.Revision, enabled = r.Status == TriggerRegistrationStatus.Active,
            intervalSeconds = ((FixedIntervalSchedule)r.Schedule).IntervalSeconds,
            promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Intent))), modelKey = r.ModelOverrideCatalogKey }));
}
