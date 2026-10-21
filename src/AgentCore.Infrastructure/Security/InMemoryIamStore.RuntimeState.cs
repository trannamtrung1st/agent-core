using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantRuntimeStateStore
{
    public ValueTask<DurableSessionState> ReadTenantSessionStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default)
    { query.Validate(); ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ObserveTenantSessions(query)); }
    public ValueTask<DurableRunState> ReadTenantRunStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default)
    { query.Validate(); ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ObserveTenantRuns(query)); }
    public ValueTask<ResourceStateObservation> ReadTenantSandboxStateAsync(TenantRuntimeStateQuery query, DateTimeOffset at, CancellationToken ct = default)
    {
        query.Validate(); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var leases = sandboxBacking.ObserveTenantLeases(query).Where(l => sessionBacking.ReadTenantSession(query.Owner.TenantId!.Value, l.SessionId) is { } s
                && s.Owner.AgentInstanceId == query.Owner.AgentInstanceId && s.Owner.ExecutorPrincipalId == query.Owner.ExecutorPrincipalId
                && s.Snapshot.ArchivedAt is null).Take(101).ToArray();
            return ValueTask.FromResult(AgentCore.Infrastructure.Persistence.SandboxStateProjection.Observe(leases.Take(100).ToArray(), at, leases.Length > 100));
        }
    }
}
