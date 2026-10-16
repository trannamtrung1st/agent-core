using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Admin;

/// <summary>Retires existing generations after saved restrictions deny dispatch. No second policy store.</summary>
public sealed class SandboxPolicyEnforcement(ISandboxStore store, ISandboxService sandbox)
{
    public async ValueTask<string> ObserveAsync(Guid instanceId, CancellationToken ct = default)
    {
        foreach (var lease in await store.ListLeasesAsync(ct))
            if (lease.AgentInstanceId == instanceId && lease.State != SandboxLeaseState.Terminated
                && (await sandbox.InspectAsync(lease.SessionId, ct))?.State == "EnforcementPending") return "EnforcementPending";
        return "Enforced";
    }

    public async ValueTask<bool> EnforceAsync(Guid instanceId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            foreach (var lease in await store.ListLeasesAsync(timeout.Token))
                if (lease.AgentInstanceId == instanceId && lease.State != SandboxLeaseState.Terminated
                    && (await sandbox.InspectAsync(lease.SessionId, timeout.Token))?.State == "EnforcementPending")
                    await sandbox.StopSessionAsync(lease.SessionId, timeout.Token, lease.Generation, SandboxStopIntent.PolicyChanged);
            return await ObserveAsync(instanceId, timeout.Token) == "Enforced";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; } // Saved restriction remains; the lease/reconciler owns pending cleanup.
    }
}
