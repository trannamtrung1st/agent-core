using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed class AdminAutomationHistoryService(
    IAdminP7eHistoryMutator mutator,
    AdminAutomationService automation,
    IIdGenerator ids,
    TimeProvider time)
{
    public ValueTask<AdminAutomationRegistration> CancelRegistrationAsync(
        Guid instanceId,
        Guid automationId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        CancelRegistrationWithHistoryAsync(
            instanceId,
            automationId,
            expectedRevision,
            ids.NewId(),
            time.GetUtcNow(),
            cancellationToken);

    public async ValueTask<AdminAutomationRegistration> CancelRegistrationWithHistoryAsync(
        Guid instanceId,
        Guid automationId,
        long expectedRevision,
        Guid operationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var append = AdminEventFactory.AutomationRevoked(
            operationId,
            occurredAt,
            instanceId,
            automationId,
            expectedRevision);
        await mutator.CancelAutomationWithHistoryAsync(
                instanceId,
                automationId,
                expectedRevision,
                append,
                (existing, incoming) => AdminEventReplayPolicy.EnsureAutomationRevokedReplayMatches(
                    existing,
                    incoming,
                    instanceId,
                    automationId,
                    expectedRevision),
                cancellationToken)
            .ConfigureAwait(false);
        return await automation.GetRegistrationAsync(instanceId, automationId, cancellationToken)
            .ConfigureAwait(false);
    }
}
