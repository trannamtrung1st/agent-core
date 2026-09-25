using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record TriggerInstancePolicyReconciliationResult(int Suspended, int Reactivated);

public interface ITriggerInstancePolicyReconciliationService
{
    ValueTask<TriggerInstancePolicyReconciliationResult> ReconcileAgentInstanceAsync(
        Guid agentInstanceId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed class TriggerInstancePolicyReconciliationService(
    ITriggerStore store,
    ITriggerAdmissionGuard guard) : ITriggerInstancePolicyReconciliationService
{
    public async ValueTask<TriggerInstancePolicyReconciliationResult> ReconcileAgentInstanceAsync(
        Guid agentInstanceId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var registrations = await store
            .ListFutureRegistrationsForAgentInstanceAsync(agentInstanceId, 256, cancellationToken)
            .ConfigureAwait(false);
        var suspended = 0;
        var reactivated = 0;
        foreach (var registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await guard
                .EvaluateAsync(registration.Owner, TriggerSourceKind.Schedule, cancellationToken)
                .ConfigureAwait(false);
            if (registration.Status == TriggerRegistrationStatus.Active
                && decision.Kind == TriggerAdmissionDecisionKind.Suspend)
            {
                var updated = await store.SuspendPolicyAsync(
                        registration.Owner,
                        registration.RegistrationId,
                        registration.Revision,
                        decision.Reason ?? "Scheduling is disabled for this agent.",
                        asOfUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated is not null)
                {
                    suspended++;
                }
            }
            else if (registration.Status == TriggerRegistrationStatus.SuspendedPolicy
                     && decision.Kind == TriggerAdmissionDecisionKind.Allow)
            {
                var updated = await store.TryReactivatePolicySuspensionAsync(
                        registration.Owner,
                        registration.RegistrationId,
                        registration.Revision,
                        asOfUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated is not null)
                {
                    reactivated++;
                }
            }
        }

        return new TriggerInstancePolicyReconciliationResult(suspended, reactivated);
    }
}
