using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public interface ITriggerPolicyRecoveryService
{
    ValueTask<int> ReactivateSuspendedForOwnerAsync(
        TriggerOwner owner,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed class TriggerPolicyRecoveryService(
    ITriggerStore store,
    ITriggerAdmissionGuard guard) : ITriggerPolicyRecoveryService
{
    public async ValueTask<int> ReactivateSuspendedForOwnerAsync(
        TriggerOwner owner,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var decision = await guard.EvaluateAsync(owner, TriggerSourceKind.Schedule, cancellationToken).ConfigureAwait(false);
        if (decision.Kind != TriggerAdmissionDecisionKind.Allow)
        {
            return 0;
        }

        var suspended = await store.ListAsync(owner, TriggerRegistrationStatus.SuspendedPolicy, cancellationToken)
            .ConfigureAwait(false);
        var reactivated = 0;
        foreach (var registration in suspended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = await store.TryReactivatePolicySuspensionAsync(
                owner,
                registration.RegistrationId,
                registration.Revision,
                asOfUtc,
                cancellationToken).ConfigureAwait(false);
            if (updated is not null)
            {
                reactivated++;
            }
        }

        return reactivated;
    }
}
