using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Experience;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed class AdminAutomationAuthoringService(ITriggerStore store, ExperienceService experience,
    IAgentDefinitionStore definitions, IModelCatalog catalog, ITriggerAdmissionGuard guard,
    IExternalEventStore events, IIdGenerator ids, TimeProvider time, ILocalUserProfileService profiles, IMemoryStore? memory = null, IEventFilterEvaluator? filters = null)
{
    public async ValueTask<Automation> SaveAsync(Guid instanceId, Guid? automationId, long expectedRevision,
        bool enabled, string name, string instructions, AutomationTrigger trigger, string? modelKey, string? effort, CancellationToken ct = default, TriggerProvenance? provenance = null, AutomationExecutionTarget? executionTarget = null, AutomationCompletionDelivery? completionDelivery = null, bool? requiresTools = null, bool? requiresVision = null, string? presetId = null, int? presetVersion = null)
    {
        var current = automationId is { } id ? await store.GetAsync(new(instanceId, LocalUserProfile.Id), id, ct) : null;
        var prior = current?.Triggers.SingleOrDefault();
        var keepIdentity = prior is not null && prior.Source == new AutomationTriggerRecord(prior.TriggerId, trigger).Source;
        return await SaveAsync(instanceId, automationId, expectedRevision, enabled, name, instructions,
            new[] { new AutomationTriggerRecord(keepIdentity ? prior!.TriggerId : ids.NewId(), trigger, prior?.Enabled ?? true, keepIdentity ? prior!.Revision : 1) },
            modelKey, effort, ct, provenance, executionTarget, completionDelivery, requiresTools, requiresVision, presetId, presetVersion);
    }

    public async ValueTask<Automation> SaveAsync(Guid instanceId, Guid? automationId, long expectedRevision,
        bool enabled, string name, string instructions, IReadOnlyList<AutomationTriggerRecord> submitted, string? modelKey, string? effort, CancellationToken ct = default, TriggerProvenance? provenance = null, AutomationExecutionTarget? executionTarget = null, AutomationCompletionDelivery? completionDelivery = null, bool? requiresTools = null, bool? requiresVision = null, string? presetId = null, int? presetVersion = null)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var current = automationId is Guid id ? await store.GetAsync(owner, id, ct) : null;
        if (automationId is not null && current is null) throw AgentCoreErrors.NotFound("Automation was not found.");
        if ((current?.Revision ?? 0) != expectedRevision) throw AgentCoreErrors.Conflict("Automation revision is stale.");
        var target = executionTarget ?? current?.ExecutionTarget ?? AutomationExecutionTarget.Background;
        var delivery = completionDelivery ?? current?.CompletionDelivery ?? AutomationCompletionDelivery.None;
        delivery.ValidateFor(target);
        SessionSnapshot? targetSession = null;
        var validateDestination = enabled || current is null || current.ExecutionTarget != target || current.CompletionDelivery != delivery;
        if (validateDestination && target.SessionId is { } targetId) targetSession = await AutomationDestinationPolicy.RequireAsync(memory ?? throw AgentCoreErrors.Validation("Target inspection is unavailable."), owner, targetId, ct);
        if (validateDestination && delivery.SessionId is { } reportId) await AutomationDestinationPolicy.RequireAsync(memory ?? throw AgentCoreErrors.Validation("Target inspection is unavailable."), owner, reportId, ct);
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
        var policy = definition.TriggerPolicy;
        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var children = new List<AutomationTriggerRecord>();
        var eligible = 0;
        DateTimeOffset? next = null;
        foreach (var child in submitted)
        {
            var prior = current?.Triggers.SingleOrDefault(t => t.TriggerId == child.TriggerId);
            if (prior is null && current is not null && child.Revision != 1 || prior is not null && child.Revision != prior.Revision)
                throw AgentCoreErrors.Conflict("Trigger revision is stale.");
            var trigger = child.Configuration;
            if (trigger is FilteredEventTrigger filtered && filters?.Validate(filtered.FilterExpression) is { } filterError)
                throw AgentCoreErrors.Validation(filterError);
            var decision = await guard.EvaluateAsync(owner, AutomationRules.Source(trigger), ct);
            var canExecute = decision.Kind == TriggerAdmissionDecisionKind.Allow;
            if (trigger is ScheduleTrigger scheduled)
            {
                // Structural timing is validated by the Domain. Definition limits govern execution only.
                if (enabled && child.Enabled && canExecute)
                {
                    if (policy is null) canExecute = false;
                    else
                    {
                        try { ScheduleDefinitionPolicy.Validate(scheduled.Schedule, policy, now, instructions); }
                        catch (Exception ex) when (ex is ArgumentException or TriggerScheduleCommandException)
                        { throw AgentCoreErrors.Validation(ex.Message); }
                        next = TriggerScheduleCalculator.InitialNext(scheduled.Schedule, now);
                        if (next is null) throw AgentCoreErrors.Validation("Schedule has no future occurrence.");
                    }
                }
            }
            else if (trigger is EventTrigger reaction)
            {
                var source = await events.GetAsync(reaction.EventId, ct) ?? throw AgentCoreErrors.NotFound("Event was not found.");
                canExecute &= source.Status == WebhookEventStatus.Active;
            }
            if (child.Enabled && canExecute) eligible++;
            var changedChild = prior is not null && (prior.Enabled != child.Enabled || !prior.Configuration.SemanticEquals(trigger));
            children.Add(new(child.TriggerId, trigger, child.Enabled, prior is null ? 1 : prior.Revision + (changedChild ? 1 : 0)));
        }
        if (enabled && eligible == 0) throw AgentCoreErrors.Forbidden("No enabled trigger is eligible. Repair the policy/source or save as disabled.");
        var retainingDisabledModel = !enabled && current is not null && current.ModelOverrideCatalogKey == modelKey && current.ModelOverrideReasoningEffort == effort;
        if (!retainingDisabledModel) ExecutionModelPolicy.RequireSelectable(catalog, modelKey, effort);
        if (current is null && presetId is not null)
        {
            var template = AutomationPresetCatalog.Templates.SingleOrDefault(p => p.PresetId == presetId && p.PresetVersion == presetVersion)
                ?? throw AgentCoreErrors.Validation("Preset is unavailable.");
            provenance = new(provenance?.AuthorizationOrigin ?? TriggerAuthorizationOrigin.AdminOwner, provenance?.SourceSessionId,
                provenance?.SourceEventId, now, now, template.PresetId, template.PresetVersion);
        }
        var changed = current is null || !current.Triggers.SequenceEqual(children) || current.Status != (enabled ? AutomationStatus.Active : AutomationStatus.Disabled);
        var proposed = new Automation(current?.AutomationId ?? ids.NewId(), owner,
            enabled ? AutomationStatus.Active : AutomationStatus.Disabled, instructions, children,
            changed ? next : current!.NextOccurrenceAtUtc, current?.ExpiresAtUtc, current?.OccurrenceCount ?? 0, expectedRevision + 1,
            (current?.TriggerRevision ?? 0) + 1,
            current?.Provenance.WithUpdated(now) ?? provenance ?? new(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null, modelKey, effort,
            requiresVision ?? current?.RequiresVision ?? false, name, target, delivery, requiresTools ?? current?.RequiresTools ?? false);
        if (targetSession is not null) AutomationDestinationPolicy.Pin(targetSession, catalog, modelKey, effort, proposed.RequiresVision, proposed.RequiresTools);
        if (enabled && targetSession is null && !ExecutionModelPolicy.Resolve(catalog, definition, instance, proposed).Accepted)
            throw AgentCoreErrors.Validation("Unattended model is unavailable or does not support tools.");
        return await store.SaveAutomationAsync(proposed, expectedRevision, History(proposed, current is null ? "create" : "update"), ct, policy?.MaxActiveRegistrations ?? 0);
    }

    public async ValueTask DeleteAsync(Guid instanceId, Guid automationId, long revision, CancellationToken ct = default)
    {
        await experience.RequireInstanceAsync(instanceId, ct);
        var current = await store.GetAsync(new(instanceId, LocalUserProfile.Id), automationId, ct) ?? throw AgentCoreErrors.NotFound("Automation was not found.");
        var deleted = current.WithCancellation(revision + 1, time.GetUtcNow());
        await store.SaveAutomationAsync(deleted, revision, History(deleted, "delete"), ct);
    }

    public async ValueTask<TriggerOccurrence> RunNowAsync(Guid instanceId, Guid automationId, long revision, CancellationToken ct = default)
    {
        var instance = await experience.RequireInstanceAsync(instanceId, ct);
        await profiles.GetLocalProfileAsync(ct);
        var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var automation = await store.GetAsync(owner, automationId, ct) ?? throw AgentCoreErrors.NotFound("Automation was not found.");
        if (automation.Revision != revision) throw AgentCoreErrors.Conflict("Automation revision is stale.");
        if (automation.Status != AutomationStatus.Active) throw AgentCoreErrors.Validation("Enable Automation before running it.");
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.ManualInvocation, ct);
        if (decision.Kind != TriggerAdmissionDecisionKind.Allow) throw AgentCoreErrors.Forbidden(decision.Reason ?? "Manual Automation execution is disabled by policy.");
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
        var model = automation.ExecutionTarget.SessionId is { } targetId
            ? new ExecutionModelDecision(AutomationDestinationPolicy.Pin(await AutomationDestinationPolicy.RequireAsync(
                memory ?? throw AgentCoreErrors.Validation("Target inspection is unavailable."), owner, targetId, ct), catalog,
                automation.ModelOverrideCatalogKey, automation.ModelOverrideReasoningEffort, automation.RequiresVision, automation.RequiresTools), null)
            : ExecutionModelPolicy.Resolve(catalog, definition, instance, automation);
        if (!model.Accepted || model.Pin is null) throw AgentCoreErrors.Validation("Unattended model is unavailable.");
        var result = await store.AdmitAutomationNowAsync(automation, model.Pin, TriggerScheduleCalculator.Truncate(time.GetUtcNow()), ct);
        if (result.Occurrence is null || result.Outcome != ScheduledAdmitOutcome.Admitted)
            throw AgentCoreErrors.Conflict("Automation already has an admitted Run. Wait for it to finish.");
        return result.Occurrence;
    }

    private AdminEventAppend History(Automation a, string operation) => new(ids.NewId(), time.GetUtcNow(),
        AdminEventActorKind.LocalOwner, AdminEventOperationKind.AutomationChanged, "agentInstance", a.Owner.AgentInstanceId.ToString("D"), a.Revision, null,
        JsonSerializer.Serialize(new { instanceId = a.Owner.AgentInstanceId, recordId = a.AutomationId, operation, revision = a.Revision,
            enabled = a.Status == AutomationStatus.Active, instructionsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a.Instructions))), modelKey = a.ModelOverrideCatalogKey, executionTarget = a.ExecutionTarget, completionDelivery = a.CompletionDelivery }));
}
