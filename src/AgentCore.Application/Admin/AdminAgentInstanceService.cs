using System.Diagnostics;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AdminAgentInstanceService(
    IAgentDefinitionStore definitions,
    IAgentInstanceStore instances,
    IAdminEventStore events,
    IIdGenerator ids,
    TimeProvider time,
    ITriggerInstancePolicyReconciliationService? policyReconciliation = null,
    IAdminLifecycleDeletion? deletion = null,
    AdminLifecycleCoordinator? lifecycleGate = null,
    IModelCatalog? modelCatalog = null,
    IAgentDefinitionResourceAdminStore? resourcePublications = null,
    InstanceAutomationPolicy? automationPolicy = null)
{
    public async ValueTask<InstanceVersionPreview> PreviewVersionAsync(Guid instanceId, int version, CancellationToken ct = default)
    {
        var instance = await RequireManagedAsync(instanceId, ct);
        var baseline = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Selected Definition was not found.");
        var target = await definitions.GetAsync(instance.DefinitionId, version, ct) ?? throw AgentCoreErrors.NotFound("Target Definition was not found.");
        _ = await ValidateAdoptionAsync(instance, target, ct);
        var differences = Agents.InstanceSettingsResolver.Sections.SelectMany(section =>
            Agents.InstanceSettingsResolver.Values(baseline, section).Where(field =>
                System.Text.Json.JsonSerializer.Serialize(field.Value) != System.Text.Json.JsonSerializer.Serialize(Agents.InstanceSettingsResolver.Values(target, section)[field.Key]))
            .Select(field => section + "." + field.Key))
            .Concat(Enum.GetValues<ExecutionBudgetClass>().Where(kind => baseline.ExecutionBudgets?.Get(kind) != target.ExecutionBudgets?.Get(kind)).Select(kind => "executionBudgets." + kind)).ToArray();
        var oldResources = resourcePublications is null ? [] : await resourcePublications.ListPublicationResourcesAsync(baseline.Id, baseline.Version, ct);
        var newResources = resourcePublications is null ? [] : await resourcePublications.ListPublicationResourcesAsync(target.Id, target.Version, ct);
        return new(instance.Revision, baseline.Version, target.Version, differences,
            Agents.InstanceSettingsResolver.Sections.SelectMany(section => Agents.InstanceSettingsResolver.Overrides(instance.SettingsOverrides ?? new(), section).Keys.Select(field => section + "." + field))
                .Concat(Enum.GetValues<ExecutionBudgetClass>().Where(kind => instance.ExecutionBudgets?.Get(kind) is not null).Select(kind => "executionBudgets." + kind)).ToArray(),
            target.SkillList.Where(s => !baseline.SkillList.Any(old => old.Id == s.Id)).Select(s => "definition:" + s.Id)
                .Concat(newResources.Where(r => !oldResources.Any(old => old.ResourceId == r.ResourceId)).Select(r => "definition:" + r.ResourceId)).ToArray(),
            baseline.SkillList.Where(s => !target.SkillList.Any(next => next.Id == s.Id)).Select(s => "definition:" + s.Id)
                .Concat(oldResources.Where(r => !newResources.Any(next => next.ResourceId == r.ResourceId)).Select(r => "definition:" + r.ResourceId)).ToArray(),
            "Applies to the next new Run in existing conversations. Current Runs are unchanged.",
            target.SkillList.Where(s => baseline.SkillList.FirstOrDefault(old => old.Id == s.Id) is { } old && System.Text.Json.JsonSerializer.Serialize(old) != System.Text.Json.JsonSerializer.Serialize(s)).Select(s => "definition:" + s.Id)
                .Concat(newResources.Where(r => oldResources.FirstOrDefault(old => old.ResourceId == r.ResourceId) is { } old && old != (r with { Version = old.Version })).Select(r => "definition:" + r.ResourceId)).ToArray());
    }

    private async ValueTask<AgentDefinition> ValidateAdoptionAsync(AgentInstance instance, AgentDefinition target, CancellationToken ct)
    {
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances are read-only.");
        var effective = Agents.InstanceSettingsResolver.Resolve(target, instance.SettingsOverrides);
        if (modelCatalog is not null) _ = SessionModelBinder.PinDefault(modelCatalog, effective);
        var current = await instances.ReadSkillsAsync(instance.InstanceId, ct);
        var skills = new InstanceSkillSnapshot(current.DefinitionStates.Concat(target.SkillList.Where(s => !current.DefinitionStates.Any(state => state.DefinitionSkillId == s.Id))
            .Select(s => new AgentDefinitionSkillState(instance.InstanceId, s.Id, null, 1, time.GetUtcNow()))).ToArray(), current.InstanceSkills);
        _ = Agents.EffectiveSkillCatalogResolver.Resolve(effective, skills);
        if (resourcePublications is not null)
            _ = AgentInstanceResourceService.Resolve(effective, await resourcePublications.ListPublicationResourcesAsync(target.Id, target.Version, ct),
                await instances.ReadResourcesAsync(instance.InstanceId, ct), skills);
        return effective;
    }
    public ValueTask<AgentInstance> CreateManagedAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default) =>
        CreateManagedAsync(definitionId, version, persona: null, cancellationToken);

    public ValueTask<AgentInstance> CreateManagedAsync(
        string definitionId,
        int version,
        AgentIdentity? persona,
        CancellationToken cancellationToken = default) =>
        WithDefinitionGateAsync(
            definitionId,
            ct => CreateManagedCoreAsync(definitionId, version, persona, ct),
            cancellationToken);

    private async ValueTask<AgentInstance> CreateManagedCoreAsync(
        string definitionId,
        int version,
        AgentIdentity? persona,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var operationId = ids.NewId();
        var existingEvent = await events.TryGetByOperationIdAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingEvent is not null)
        {
            var replayed = await ResolveManagedInstanceFromEventAsync(existingEvent, cancellationToken)
                .ConfigureAwait(false);
            OperationalDiagnostics.RecordAdmin(
                "instanceCreate",
                "existing",
                "replay",
                started,
                replayed.DefinitionId,
                replayed.ActiveVersion,
                replayed.InstanceId,
                "active");
            return replayed;
        }

        var definition = await definitions.GetAsync(definitionId, version, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceCreate", "rejected", "notFound", started, definitionId, version, null, null);
            throw AgentCoreErrors.NotFound($"Agent '{definitionId}' was not found.");
        }
        var storedPersona = definition.Identity;
        var personaSource = "Default";
        if (persona is not null)
        {
            try
            {
                AgentDefinitionValidator.ValidateIdentity(persona);
            }
            catch (ArgumentException ex)
            {
                OperationalDiagnostics.RecordAdmin(
                    "instanceCreate", "rejected", "validation", started, definitionId, version, null, null);
                throw AgentCoreErrors.Validation(ex.Message);
            }

            storedPersona = persona;
            personaSource = "Custom";
        }

        var now = time.GetUtcNow();
        var instance = new AgentInstance(
            ids.NewId(),
            definition.Id,
            definition.Version,
            storedPersona,
            AgentInstanceLifecycle.Active,
            now,
            now);
        var append = AdminEventFactory.ManagedInstanceCreated(
            operationId,
            now,
            definition.Id,
            instance.InstanceId,
            definition.Version,
            personaSource: personaSource,
            personaFingerprint: AdminPersonaHistoryFingerprint.Compute(storedPersona));
        var created = await instances.InsertManagedWithHistoryAsync(instance, append, cancellationToken, definition.SkillList)
            .ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "instanceCreate",
            "completed",
            "completed",
            started,
            created.DefinitionId,
            created.ActiveVersion,
            created.InstanceId,
            "active");
        return created;
    }

    public async ValueTask<AgentInstance> ReassociateActiveVersionAsync(
        Guid instanceId,
        int version,
        long expectedRevision,
        CancellationToken cancellationToken = default,
        HarnessManagementState? harnessManagement = null,
        AdminEventActorKind actorKind = AdminEventActorKind.LocalOwner)
    {
        var started = Stopwatch.GetTimestamp();
        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceVersion", "rejected", "conflict", started, instance.DefinitionId, version, instanceId, null);
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        var definition = await definitions.GetAsync(instance.DefinitionId, version, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceVersion",
                "rejected",
                "notFound",
                started,
                instance.DefinitionId,
                version,
                instanceId,
                null);
            throw AgentCoreErrors.NotFound($"Agent '{instance.DefinitionId}' version {version} was not found.");
        }

        if (definition.Version == instance.ActiveVersion)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceVersion",
                "unchanged",
                "completed",
                started,
                instance.DefinitionId,
                instance.ActiveVersion,
                instance.InstanceId,
                "unchanged");
            return instance;
        }

        var operationId = ids.NewId();
        var now = time.GetUtcNow();
        var append = AdminEventFactory.InstanceDefinitionVersionChanged(
            operationId,
            now,
            instance.DefinitionId,
            instance.InstanceId,
            instance.ActiveVersion,
            definition.Version, actorKind);
        var effective = await ValidateAdoptionAsync(instance, definition, cancellationToken);
        var reconciliation = automationPolicy is null ? null : await automationPolicy.PlanAsync(instance, effective, now, cancellationToken);
        var updated = await instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, expectedRevision, ActiveVersion: definition.Version, HarnessManagement: harnessManagement, DefinitionSkills: definition.SkillList, AutomationPolicyChanges: reconciliation),
                now,
                append,
                cancellationToken)
            .ConfigureAwait(false);
        if (automationPolicy is null) await ReconcileTriggerPolicyAsync(instance.InstanceId, now, cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "instanceVersion",
            "completed",
            "completed",
            started,
            updated.DefinitionId,
            updated.ActiveVersion,
            updated.InstanceId,
            "active");
        return updated;
    }

    public async ValueTask<AgentInstance> UpdatePersonaAsync(
        Guid instanceId,
        AgentIdentity persona,
        long expectedRevision,
        long expectedPersonaRevision,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision || instance.PersonaRevision != expectedPersonaRevision)
        {
            OperationalDiagnostics.RecordAdmin(
                "instancePersona", "rejected", "conflict", started, instance.DefinitionId, instance.ActiveVersion, instanceId, null);
            throw AgentCoreErrors.Conflict(
                instance.Revision != expectedRevision
                    ? "Agent instance revision is stale."
                    : "Agent instance persona revision is stale.");
        }

        if (instance.Persona.Equals(persona))
        {
            OperationalDiagnostics.RecordAdmin(
                "instancePersona",
                "unchanged",
                "completed",
                started,
                instance.DefinitionId,
                instance.ActiveVersion,
                instance.InstanceId,
                "unchanged");
            return instance;
        }

        var operationId = ids.NewId();
        var now = time.GetUtcNow();
        var toPersonaRevision = instance.PersonaRevision + 1;
        var append = AdminEventFactory.PersonaChanged(
            operationId,
            now,
            instance.DefinitionId,
            instance.InstanceId,
            instance.ActiveVersion,
            instance.PersonaRevision,
            toPersonaRevision,
            persona);
        var updatedPersona = await instances.UpdatePersonaWithHistoryAsync(
                new AgentInstanceRevisionUpdate(
                    instance.InstanceId,
                    expectedRevision,
                    Persona: persona,
                    ExpectedPersonaRevision: expectedPersonaRevision),
                now,
                append,
                cancellationToken)
            .ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "instancePersona",
            "completed",
            "completed",
            started,
            updatedPersona.DefinitionId,
            updatedPersona.ActiveVersion,
            updatedPersona.InstanceId,
            "none");
        return updatedPersona;
    }

    public async ValueTask<AgentInstance> SetExecutionBudgetsAsync(Guid instanceId, long expectedRevision,
        ExecutionBudgetPolicy? policy, CancellationToken ct = default)
    {
        var instance = await RequireManagedAsync(instanceId, ct).ConfigureAwait(false);
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Only active Instances can change execution budgets.");
        try { policy?.Validate(); }
        catch (ArgumentException ex) { throw AgentCoreErrors.Validation(ex.Message); }
        return await instances.UpdateWithExpectedRevisionAsync(new(instanceId, expectedRevision,
            SetExecutionBudgets: true, ExecutionBudgets: policy), time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public async ValueTask<AgentInstance> SetUnattendedModelAsync(
        Guid instanceId,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        if (modelCatalog is null)
        {
            throw AgentCoreErrors.Validation("The model catalog is not available.");
        }

        ExecutionModelPolicy.RequireSelectable(modelCatalog, catalogKey, reasoningEffort);
        var key = string.IsNullOrWhiteSpace(catalogKey) ? null : catalogKey.Trim();
        var effort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        if (string.Equals(instance.UnattendedModelCatalogKey, key, StringComparison.Ordinal)
            && string.Equals(instance.UnattendedReasoningEffort, effort, StringComparison.Ordinal))
        {
            return instance;
        }

        return await instances.UpdateWithExpectedRevisionAsync(
            new AgentInstanceRevisionUpdate(
                instance.InstanceId,
                expectedRevision,
                SetUnattendedModel: true,
                UnattendedModelCatalogKey: key,
                UnattendedReasoningEffort: effort),
            time.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<AgentInstance> SetLifecycleAsync(
        Guid instanceId,
        AgentInstanceLifecycle lifecycle,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        WithInstanceGateAsync(
            instanceId,
            ct => SetLifecycleCoreAsync(instanceId, lifecycle, expectedRevision, ct),
            cancellationToken);

    private async ValueTask<AgentInstance> SetLifecycleCoreAsync(
        Guid instanceId,
        AgentInstanceLifecycle lifecycle,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (lifecycle is not (AgentInstanceLifecycle.Active or AgentInstanceLifecycle.Archived))
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceLifecycle", "rejected", "validation", started, null, null, instanceId, null);
            throw AgentCoreErrors.Validation("lifecycle must be Active or Archived.");
        }

        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceLifecycle", "rejected", "conflict", started, instance.DefinitionId, instance.ActiveVersion, instanceId, null);
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        if (instance.Lifecycle == lifecycle)
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceLifecycle",
                "unchanged",
                "completed",
                started,
                instance.DefinitionId,
                instance.ActiveVersion,
                instance.InstanceId,
                "unchanged");
            return instance;
        }

        var operationId = ids.NewId();
        var now = time.GetUtcNow();
        var nextRevision = instance.Revision + 1;
        var append = AdminEventFactory.InstanceLifecycleChanged(
            operationId,
            now,
            instance.DefinitionId,
            instance.InstanceId,
            instance.ActiveVersion,
            instance.Lifecycle,
            lifecycle,
            nextRevision);
        var updated = await instances.UpdateLifecycleWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, expectedRevision, Lifecycle: lifecycle),
                now,
                append,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcileTriggerPolicyAsync(instance.InstanceId, now, cancellationToken).ConfigureAwait(false);
        OperationalDiagnostics.RecordAdmin(
            "instanceLifecycle",
            "completed",
            "completed",
            started,
            updated.DefinitionId,
            updated.ActiveVersion,
            updated.InstanceId,
            lifecycle == AgentInstanceLifecycle.Archived ? "archived" : "active");
        return updated;
    }

    public async ValueTask DeleteAsync(
        AdminInstanceDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (deletion is null)
        {
            throw AgentCoreErrors.Validation("Instance deletion is not available.");
        }

        try
        {
            await WithInstanceGateAsync(
                command.InstanceId,
                ct => deletion.DeleteInstanceAsync(command, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "NotFound" or "ValidationError")
        {
            OperationalDiagnostics.RecordAdmin(
                "instanceDelete",
                "rejected",
                ex.Code == "NotFound" ? "notFound" : ex.Code == "Conflict" ? "conflict" : "validation",
                started,
                null,
                null,
                command.InstanceId,
                null);
            throw;
        }

        OperationalDiagnostics.RecordAdmin(
            "instanceDelete", "completed", "completed", started, null, null, command.InstanceId, "deleted");
    }

    private async ValueTask<AgentInstance> ResolveManagedInstanceFromEventAsync(
        AdminEvent existingEvent,
        CancellationToken cancellationToken)
    {
        if (existingEvent.Operation != AdminEventOperationKind.ManagedInstanceCreated)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        if (!Guid.TryParse(existingEvent.TargetId, out var instanceId))
        {
            throw AgentCoreErrors.Conflict("Managed instance history is missing an instance target id.");
        }

        return await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
    }

    private async ValueTask<T> WithInstanceGateAsync<T>(
        Guid instanceId,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        if (lifecycleGate is null)
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }

        return await lifecycleGate.WithInstanceAsync(instanceId, action, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WithInstanceGateAsync(
        Guid instanceId,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        _ = await WithInstanceGateAsync(
            instanceId,
            async ct =>
            {
                await action(ct).ConfigureAwait(false);
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> WithDefinitionGateAsync<T>(
        string definitionId,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        if (lifecycleGate is null)
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }

        return await lifecycleGate.WithDefinitionAsync(definitionId, action, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AgentInstance> RequireManagedAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        return instance;
    }

    private async ValueTask ReconcileTriggerPolicyAsync(
        Guid agentInstanceId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (policyReconciliation is null)
        {
            return;
        }

        await policyReconciliation.ReconcileAgentInstanceAsync(agentInstanceId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
    }
}
