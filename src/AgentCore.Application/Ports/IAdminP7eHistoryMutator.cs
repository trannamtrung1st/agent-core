using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface IAdminP7eHistoryMutator
{
    ValueTask DeleteLearnedMemoryWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default);

    ValueTask<int> ResetLearnedMemoryScopeWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend, int> ensureReplay,
        CancellationToken cancellationToken = default);

    ValueTask CancelAutomationWithHistoryAsync(
        Guid instanceId,
        Guid automationId,
        long expectedRevision,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default);
}
