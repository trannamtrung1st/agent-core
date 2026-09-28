using System.Diagnostics;
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
    ITriggerInstancePolicyReconciliationService? policyReconciliation = null)
{
    public ValueTask<AgentInstance> CreateManagedAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default) =>
        CreateManagedAsync(definitionId, version, persona: null, cancellationToken);

    public async ValueTask<AgentInstance> CreateManagedAsync(
        string definitionId,
        int version,
        AgentIdentity? persona,
        CancellationToken cancellationToken = default)
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
            now,
            Compatibility: false);
        var append = AdminEventFactory.ManagedInstanceCreated(
            operationId,
            now,
            definition.Id,
            instance.InstanceId,
            definition.Version,
            personaSource: personaSource,
            personaFingerprint: AdminPersonaHistoryFingerprint.Compute(storedPersona));
        var created = await instances.InsertManagedWithHistoryAsync(instance, append, cancellationToken)
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
        CancellationToken cancellationToken = default)
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
            definition.Version);
        var updated = await instances.UpdateActiveVersionWithHistoryAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, expectedRevision, ActiveVersion: definition.Version),
                now,
                append,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcileTriggerPolicyAsync(instance.InstanceId, now, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<AgentInstance> SetLifecycleAsync(
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

    private async ValueTask<AgentInstance> RequireManagedAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Compatibility)
        {
            throw AgentCoreErrors.Validation("Compatibility instances cannot be mutated through the managed API.");
        }

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
