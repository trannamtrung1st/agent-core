using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed class AdminAgentInstanceService(
    IAgentDefinitionStore definitions,
    IAgentInstanceStore instances,
    IAdminEventStore events,
    IIdGenerator ids,
    TimeProvider time)
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
}
