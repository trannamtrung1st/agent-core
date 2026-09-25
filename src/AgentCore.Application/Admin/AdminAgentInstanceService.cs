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
    public async ValueTask<AgentInstance> CreateManagedAsync(
        string definitionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var operationId = ids.NewId();
        var existingEvent = await events.TryGetByOperationIdAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingEvent is not null)
        {
            return await ResolveManagedInstanceFromEventAsync(existingEvent, cancellationToken).ConfigureAwait(false);
        }

        var definition = await definitions.GetAsync(definitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{definitionId}' was not found.");
        var now = time.GetUtcNow();
        var instance = new AgentInstance(
            ids.NewId(),
            definition.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: false);
        var append = AdminEventFactory.ManagedInstanceCreated(
            operationId,
            now,
            definition.Id,
            instance.InstanceId,
            definition.Version);
        return await instances.InsertManagedWithHistoryAsync(instance, append, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AgentInstance> ReassociateActiveVersionAsync(
        Guid instanceId,
        int version,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        var definition = await definitions.GetAsync(instance.DefinitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{instance.DefinitionId}' version {version} was not found.");
        if (definition.Version == instance.ActiveVersion)
        {
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
