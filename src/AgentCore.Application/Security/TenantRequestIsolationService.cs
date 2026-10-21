using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

// Invoked only while the canonical host holds its quiescent Session reservation.
public sealed class TenantRequestIsolationService(ITenantRequestIsolationStore store, IBrowser browser, TimeProvider time)
{
    public async ValueTask<bool> SettleQuiescentAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        var state = await store.ReadTenantRequestIsolationAsync(owner, ct).ConfigureAwait(false);
        if (state?.Ready != true) return false;
        if (browser is not ITenantBrowserRetirement retirement) return false;
        var revision = state.Session.Snapshot.Revision;
        if (state.Lease is { State: not SandboxLeaseState.Terminated })
        { await store.RequestTenantIsolationRetirementAsync(owner, revision, ct).ConfigureAwait(false); return false; }
        async ValueTask<bool> StillReady(CancellationToken token)
        {
            var current = await store.ReadTenantRequestIsolationAsync(owner, token).ConfigureAwait(false);
            return current?.Ready == true && current.Session.Snapshot.Revision == revision
                && current.Lease is null or { State: SandboxLeaseState.Terminated };
        }
        var retired = await retirement.RetireTenantIfBoundAsync(new(owner.TenantId, owner.AgentInstanceId, owner.ExecutorPrincipalId, owner.SessionId), StillReady, ct).ConfigureAwait(false);
        if (retired is not ("closed" or "already_closed")) return false;
        return await store.CompleteTenantIsolatedRequestAsync(owner, revision, time.GetUtcNow(), ct).ConfigureAwait(false);
    }
}
