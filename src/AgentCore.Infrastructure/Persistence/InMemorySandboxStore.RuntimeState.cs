using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemorySandboxStore
{
    // The IAM owner holds both the Session and Sandbox gates while consuming this metadata projection.
    internal IEnumerable<SandboxLease> ObserveTenantLeases(TenantRuntimeStateQuery query) => _leases.Values.Where(l =>
        l.TenantId == query.Owner.TenantId && l.AgentInstanceId == query.Owner.AgentInstanceId && l.ExecutorPrincipalId == query.Owner.ExecutorPrincipalId
        && OwnsTenantProfile(query.Owner.TenantId!.Value, l.Profile.Id) && l.State != SandboxLeaseState.Terminated && (query.Sessions is null || query.Sessions.Contains(l.SessionId))
        && (query.SandboxProfiles is null || query.SandboxProfiles.Contains(l.Profile.Id)));
}
