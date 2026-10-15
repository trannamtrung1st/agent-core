using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class SandboxStateProjection
{
    internal static ResourceStateObservation Observe(IReadOnlyList<SandboxLease> leases, DateTimeOffset observedAt, bool truncated = false)
    {
        var uncertain = leases.Any(l => l.State is SandboxLeaseState.Provisioning or SandboxLeaseState.NeedsReconciliation
            or SandboxLeaseState.NeedsResync or SandboxLeaseState.Failed);
        return new("sandbox", uncertain || truncated ? "partial" : "available", "isolated", observedAt,
            ActiveGenerations: uncertain || truncated ? null : leases.Count(l => l.State is SandboxLeaseState.Ready or SandboxLeaseState.Executing),
            ActiveCommands: uncertain || truncated ? null : leases.Count(l => l.ActiveExecutionId is not null),
            CleanupPending: truncated ? null : leases.Count(l => l.State == SandboxLeaseState.Terminating),
            Code: truncated ? "observation_limit" : uncertain ? "reconciliation_required" : null);
    }
}
