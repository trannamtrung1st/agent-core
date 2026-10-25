using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceMutationRules
{
    public static bool Writable(TenantAgent agent, TenantSession? session) => agent.Instance.Lifecycle == AgentInstanceLifecycle.Active
        && (session is null || session.Snapshot.DurablyDeletedAt is null && session.Snapshot.ArchivedAt is null
            && session.Snapshot.Status is not (SessionStatus.Ending or SessionStatus.Ended) && !SessionLifecycle.IsTerminal(session.Snapshot.LifecycleStatus));
    public static bool Matches(TenantWorkspaceReference owner, TenantAgent agent, TenantSession? session) =>
        owner is TenantWorkspaceReference.SessionWorking working && working.TenantId == agent.TenantId
        && session?.Owner.TenantId == working.TenantId && session.Owner.SessionId == working.SessionId
        && session.Owner.AgentInstanceId == agent.Instance.InstanceId && session.Owner.ExecutorPrincipalId == agent.PrincipalId;
    public static async ValueTask<bool> Allowed(TenantSecurityState state, TenantWorkspaceReference owner, Guid human, string permission, IamAuthorizer iam, CancellationToken ct)
    {
        if (permission is not ("workspaces.write" or "workspaces.delete")) throw new ArgumentException("Invalid workspace mutation permission.");
        var (read, parent) = Parent(owner);
        return (await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(read), parent, ct).ConfigureAwait(false)).Allowed
            && (await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(permission), TenantWorkspaceRules.Scope(owner), ct).ConfigureAwait(false)).Allowed;
    }
    public static (string Permission, AccessScope Scope) Parent(TenantWorkspaceReference owner) => owner switch {
        TenantWorkspaceReference.SpaceHome home => ("spaces.read", TenantSpaceService.Scope(home.TenantId, home.SpaceId)),
        TenantWorkspaceReference.SessionWorking working => ("sessions.read", TenantSessionRules.Scope(working.TenantId, working.SessionId)),
        _ => throw new AccessDeniedException("resource_unavailable") };
    public static void Validate(TenantWorkspaceHumanAuthorization proof)
    {
        var a = proof.Audit;
        if (proof.Workspace.OwnerId == Guid.Empty || proof.SecurityRevision < 1 || proof.OwnerRevision < 1
            || proof.Permission is not ("workspaces.write" or "workspaces.delete") || a.AuditId == Guid.Empty
            || a.TenantId != proof.Workspace.TenantId || a.ActorPrincipalId != proof.Human.PrincipalId || a.Execution is not null
            || a.Action != proof.Permission || a.Target != TenantWorkspaceRules.Scope(proof.Workspace).ResourceId
            || a.Decision is not ("allowed" or "denied") || a.At.Offset != TimeSpan.Zero
            || a.ReasonCode != (a.Decision == "allowed" ? "human_workspace_authorized" : "human_workspace_denied"))
            throw new ArgumentException("Invalid human workspace authorization evidence.");
    }
}
