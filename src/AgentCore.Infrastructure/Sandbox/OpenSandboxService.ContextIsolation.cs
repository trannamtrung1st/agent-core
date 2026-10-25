using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public async ValueTask RetireIdleGenerationAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        var gate = Gate(owner.SessionId);
        if (!await gate.WaitAsync(0, ct)) throw new SecurityConflictException("sandbox_execution_busy");
        try
        {
            var saved = await TenantRuntime.ReadTenantSandboxRuntimeAsync(owner.TenantId, owner.SessionId, ct: ct)
                ?? throw new AccessDeniedException("resource_unavailable");
            if (saved.Owner != owner) throw new AccessDeniedException("tenant_context_mismatch");
            var lease = saved.Lease;
            if (lease is null || lease.State == SandboxLeaseState.Terminated) return;
            if (lease.State == SandboxLeaseState.Executing || lease.ActiveExecutionId is not null)
                throw new SecurityConflictException("sandbox_execution_busy");
            if (saved.Execution is { TerminationConfirmed: false })
                throw new SecurityConflictException("sandbox_recovery_required");
            // Same exact generation retirement as the existing recovery worker, including remote
            // acknowledgement and factual receipt settlement. No reset/profile intent is invented.
            lease = await RetireTenantLeaseLockedAsync(lease, saved.Execution, "contextChanged", ct);
            TenantSandboxContextRules.RequireRetired(lease);
        }
        finally { gate.Release(); }
    }
}
