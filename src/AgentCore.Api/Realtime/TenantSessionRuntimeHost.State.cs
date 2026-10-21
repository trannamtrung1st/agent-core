using AgentCore.Application.Ports;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionRuntimeHost : ITenantHostedRuntimeStateReader
{
    public HostedSessionState ObserveTenant(TenantRuntimeStateQuery query)
    {
        query.Validate();
        lock (_gate)
        {
            bool Visible(Guid id) => query.Sessions is null || query.Sessions.Contains(id);
            var live = _live.Where(p => p.Value.Run.Owner == query.Owner && Visible(p.Key)).Select(p => p.Key)
                .Concat(_voices.Where(p => p.Value.Lease.Owner.TenantId == query.Owner.TenantId
                    && p.Value.Lease.Owner.AgentInstanceId == query.Owner.AgentInstanceId && p.Value.Lease.Owner.ExecutorPrincipalId == query.Owner.ExecutorPrincipalId
                    && Visible(p.Key)).Select(p => p.Key)).Distinct().Count();
            return new(live, delivery.ObserveAttachedTenantSessions(query));
        }
    }
}
