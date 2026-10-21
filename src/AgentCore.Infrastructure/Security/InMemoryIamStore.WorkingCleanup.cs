using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workspaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkingCleanupStore
{
    public ValueTask<TenantWorkingCleanupPage> ListTenantWorkingCleanupAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return ValueTask.FromResult(sessionBacking.WorkingCleanupPage(after, limit));
    }
    public ValueTask<TenantWorkingCleanupTarget?> ReadTenantWorkingCleanupAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var graph = sessionBacking.SessionSettlement(owner); if (graph is null) return ValueTask.FromResult<TenantWorkingCleanupTarget?>(null);
            var lease = sandboxBacking.TenantLease(owner.TenantId, owner.SessionId);
            if (lease is null && sandboxBacking.HasLease(owner.SessionId)) return ValueTask.FromResult<TenantWorkingCleanupTarget?>(null);
            return ValueTask.FromResult(TenantWorkingCleanupRules.Target(graph, lease));
        }
    }
}
