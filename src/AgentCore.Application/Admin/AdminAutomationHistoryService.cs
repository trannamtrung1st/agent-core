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
        Guid registrationId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        CancelRegistrationWithHistoryAsync(
            instanceId,
            registrationId,
            expectedRevision,
            ids.NewId(),
            time.GetUtcNow(),
            cancellationToken);

    public async ValueTask<AdminAutomationRegistration> CancelRegistrationWithHistoryAsync(
        Guid instanceId,
        Guid registrationId,
        long expectedRevision,
        Guid operationId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var append = AdminEventFactory.TriggerRegistrationRevoked(
            operationId,
            occurredAt,
            instanceId,
            registrationId,
            expectedRevision);
        await mutator.CancelTriggerRegistrationWithHistoryAsync(
                instanceId,
                registrationId,
                expectedRevision,
                append,
                (existing, incoming) => AdminEventReplayPolicy.EnsureTriggerRegistrationRevokedReplayMatches(
                    existing,
                    incoming,
                    instanceId,
                    registrationId,
                    expectedRevision),
                cancellationToken)
            .ConfigureAwait(false);
        return await automation.GetRegistrationAsync(instanceId, registrationId, cancellationToken)
            .ConfigureAwait(false);
    }
}
