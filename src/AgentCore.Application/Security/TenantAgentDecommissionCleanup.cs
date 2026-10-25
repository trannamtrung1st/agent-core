using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

public sealed class TenantAgentDecommissionCleanup(ITenantAgentDecommissionStore store, IBrowser browser,
    ITenantAgentWorkspaceCleanup files, IIdGenerator ids, TimeProvider time, ILogger<TenantAgentDecommissionCleanup>? logger = null)
{
    private readonly SemaphoreSlim gate = new(1, 1); private Guid? cursor;
    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try {
            var page = await store.ListAgentDecommissionCleanupAsync(cursor, 100, ct); var completed = 0;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            foreach (var owner in page.Items) {
                ct.ThrowIfCancellationRequested(); cursor = owner.AgentInstanceId;
                if (await ReconcileAsync(owner, linked.Token)) completed++;
                if (budget.IsCancellationRequested) return completed;
            }
            cursor = page.NextCursor; return completed;
        } finally { gate.Release(); }
    }
    public async ValueTask<bool> ReconcileAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default)
    {
        try {
            if (!await store.IsAgentDecommissionedAsync(owner, ct) || browser is not ITenantBrowserProfileControl profiles) return false;
            ValueTask<bool> Still(CancellationToken token) => store.IsAgentDecommissionedAsync(owner, token);
            var observed = await profiles.ReadTenantProfileAsync(owner, ct);
            if (observed.Busy || observed.InFlightOperation is not null) return false;
            var result = await profiles.ResetTenantProfileAsync(owner, ids.NewId(), observed.Witness, (_, token) => Still(token), Still, ct);
            if (result.Status != "reset" || !await Still(ct)) return false;
            await files.PurgeDecommissionedAgentAsync(owner, token => store.CanPurgeDecommissionedAgentWorkspaceAsync(owner, token), ct); return true;
        }
        catch (Exception error) {
            HandledFailureLog.Warning(logger, error, "agent.cleanup", "agent_decommission_cleanup_pending", new(AgentInstanceId: owner.AgentInstanceId));
            return false;
        }
    }
}
