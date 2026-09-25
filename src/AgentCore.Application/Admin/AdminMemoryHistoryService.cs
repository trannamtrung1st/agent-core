using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed class AdminMemoryHistoryService(
    IAdminP7eHistoryMutator mutator,
    IIdGenerator ids,
    TimeProvider time)
{
    public ValueTask DeleteAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        CancellationToken cancellationToken = default) =>
        DeleteWithHistoryAsync(instanceId, scope, memoryId, sessionId, ids.NewId(), time.GetUtcNow(), cancellationToken);

    public ValueTask DeleteWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        Guid operationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var append = AdminEventFactory.MemoryItemDeleted(
            operationId,
            occurredAt,
            instanceId,
            scope,
            memoryId,
            sessionId);
        return mutator.DeleteLearnedMemoryWithHistoryAsync(
            instanceId,
            scope,
            memoryId,
            sessionId,
            append,
            (existing, incoming) => AdminEventReplayPolicy.EnsureMemoryItemDeletedReplayMatches(
                existing,
                incoming,
                instanceId,
                scope,
                memoryId,
                sessionId),
            cancellationToken);
    }

    public ValueTask<AdminLearnedMemoryResetResult> ResetScopeAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        CancellationToken cancellationToken = default) =>
        ResetScopeWithHistoryAsync(instanceId, scope, sessionId, ids.NewId(), time.GetUtcNow(), cancellationToken);

    public async ValueTask<AdminLearnedMemoryResetResult> ResetScopeWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        Guid operationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var placeholder = AdminEventFactory.MemoryScopeReset(
            operationId,
            occurredAt,
            instanceId,
            scope,
            itemsRemoved: 0,
            sessionId);
        var itemsRemoved = await mutator.ResetLearnedMemoryScopeWithHistoryAsync(
                instanceId,
                scope,
                sessionId,
                placeholder,
                (existing, incoming, removed) => AdminEventReplayPolicy.EnsureMemoryScopeResetReplayMatches(
                    existing,
                    incoming,
                    instanceId,
                    scope,
                    removed,
                    sessionId),
                cancellationToken)
            .ConfigureAwait(false);
        return new AdminLearnedMemoryResetResult(scope, itemsRemoved);
    }
}
