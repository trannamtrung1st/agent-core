using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantRequestIsolationStore
{
    public ValueTask<bool> HasTenantRequestBoundaryAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var session = sessionBacking.ReadTenantSession(owner.TenantId, owner.SessionId);
            var runs = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == owner.TenantId && r.SessionId == owner.SessionId).ToArray();
            return ValueTask.FromResult(session?.Owner == owner && runs.Length > 0 && TenantRequestIsolationRules.Strict(session.Snapshot, runs));
        }
    }
    public ValueTask<TenantRequestIsolationPage> ListTenantRequestIsolationAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return ValueTask.FromResult(sessionBacking.RequestIsolationPage(after, limit));
    }
    private TenantRequestIsolationState? RequestIsolation(TenantSessionRoot owner)
    {
        var graph = sessionBacking.SessionSettlement(owner); if (graph is null) return null;
        var lease = sandboxBacking.TenantLease(owner.TenantId, owner.SessionId);
        if (lease is null && sandboxBacking.HasLease(owner.SessionId)) throw new AccessDeniedException("sandbox_owner_mismatch");
        if (lease is not null && !TenantRequestIsolationRules.LeaseOwner(owner, lease)) throw new AccessDeniedException("sandbox_owner_mismatch");
        return new(graph.Session, TenantRequestIsolationRules.Ready(graph.Session, graph.Runs, graph.Children, graph.ChildRuns, graph.Receipts), lease);
    }
    public ValueTask<TenantRequestIsolationState?> ReadTenantRequestIsolationAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(RequestIsolation(owner));
    }
    public ValueTask<bool> RequestTenantIsolationRetirementAsync(TenantSessionRoot owner, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var state = RequestIsolation(owner);
            if (state?.Ready != true || state.Session.Snapshot.Revision != expectedRevision || state.Lease is not { } lease) return ValueTask.FromResult(false);
            if (lease.State is not (SandboxLeaseState.Terminated or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation))
                sandboxBacking.SaveTenantLease(lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Terminating, CleanupReason = "requestCompleted" });
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> CompleteTenantIsolatedRequestAsync(TenantSessionRoot owner, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var state = RequestIsolation(owner);
            if (state?.Ready != true || state.Session.Snapshot.Revision != expectedRevision || state.Lease is { State: not SandboxLeaseState.Terminated }) return ValueTask.FromResult(false);
            var next = sessionBacking.CompleteIsolatedRequest(state.Session, at); AppendTenantCoreSession(state.Session, next);
            return ValueTask.FromResult(true);
        }
    }
}
