using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record TenantWorkspaceOwnership(TenantWorkspaceReference Owner, Space? Space, TenantAgent? Agent, TenantSession? Session)
{
    public long Revision => Space?.Revision ?? Session!.Snapshot.Revision;
    public bool Writable => Space is { Kind: SpaceKind.Shared, DeletedAt: null }
        || Agent is { Instance.Lifecycle: AgentInstanceLifecycle.Active } && (Session is null || TenantWorkspaceMutationRules.Writable(Agent, Session));
    public string Name => Space?.Name ?? Session!.Snapshot.Title;
}

// The shared canonical owner resolution used by human file services and the
// filesystem adapter. IAM and operation consent remain at their existing owners.
public sealed class TenantWorkspaceOwnerResolver(ITenantSpaceStore spaces, ITenantAgentStore agents, ITenantSessionStore sessions)
{
    public async ValueTask<TenantWorkspaceOwnership> ResolveAsync(TenantWorkspaceReference owner, CancellationToken ct)
    {
        if (owner is TenantWorkspaceReference.SpaceHome home)
        {
            var space = await spaces.FindSpaceAsync(home.TenantId, home.SpaceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
            if (home.PrivateAlias && space.Kind != SpaceKind.Private) throw new AccessDeniedException("space_unavailable");
            TenantAgent? agent = null;
            if (space.Kind == SpaceKind.Private)
            {
                agent = await agents.FindTenantAgentAsync(home.TenantId, space.OwnerInstanceId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
                if (!(await spaces.ReadMembershipsAsync(home.TenantId, home.SpaceId, ct).ConfigureAwait(false)).Any(m => m.InstanceId == agent.Instance.InstanceId))
                    throw new AccessDeniedException("space_membership_unavailable");
            }
            return new(owner, space, agent, null);
        }
        if (owner is TenantWorkspaceReference.SessionWorking working)
        {
            var session = await sessions.FindTenantSessionAsync(working.TenantId, working.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            var agent = await agents.FindTenantAgentAsync(working.TenantId, session.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            if (!TenantWorkspaceMutationRules.Matches(owner, agent, session)) throw new AccessDeniedException("resource_unavailable");
            return new(owner, null, agent, session);
        }
        throw new AccessDeniedException("resource_unavailable");
    }
}
