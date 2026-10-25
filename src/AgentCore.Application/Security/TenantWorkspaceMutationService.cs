using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed class TenantWorkspaceMutationService(IIamStore security, ITenantAgentStore agents, ITenantSessionStore sessions,
    ITenantWorkspaceMutationStore files, ITenantAuthorizationAuditStore audits, IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time)
{
    private sealed record View(TenantSecurityState State, TenantWorkspaceOwnership Ownership)
    {
        public TenantWorkspaceReference Workspace => Ownership.Owner;
    }
    private async ValueTask<View> Load(Guid tenant, Guid id, bool working, HumanSessionReference human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        TenantWorkspaceReference owner = working ? new TenantWorkspaceReference.SessionWorking(tenant, id) : new TenantWorkspaceReference.SpaceHome(tenant, id);
        var ownership = await new TenantWorkspaceOwnerResolver((ITenantSpaceStore)security, agents, sessions).ResolveAsync(owner, ct).ConfigureAwait(false);
        return new(state, ownership);
    }
    public async ValueTask<TenantWorkspaceAccess> InspectAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, CancellationToken ct = default)
    {
        var view = await Load(tenant, id, working, human, ct).ConfigureAwait(false);
        var (readPermission, parent) = TenantWorkspaceMutationRules.Parent(view.Workspace);
        if (!(await iam.CheckOwnedTenantAsync(view.State, human.PrincipalId, PermissionCatalog.Get(readPermission), parent, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
        var scope = TenantWorkspaceRules.Scope(view.Workspace);
        var read = (await iam.CheckOwnedTenantAsync(view.State, human.PrincipalId, PermissionCatalog.Get("workspaces.read"), scope, ct).ConfigureAwait(false)).Allowed;
        var writable = view.Ownership.Writable;
        var write = writable && await TenantWorkspaceMutationRules.Allowed(view.State, view.Workspace, human.PrincipalId, "workspaces.write", iam, ct).ConfigureAwait(false);
        var delete = writable && await TenantWorkspaceMutationRules.Allowed(view.State, view.Workspace, human.PrincipalId, "workspaces.delete", iam, ct).ConfigureAwait(false);
        var current = await Load(tenant, id, working, human, ct).ConfigureAwait(false);
        var authority = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (authority.Tenant.Revision != view.State.Tenant.Revision || current.State.Tenant.Revision != view.State.Tenant.Revision || current.Ownership.Revision != view.Ownership.Revision
            || current.Ownership.Agent?.Instance.Revision != view.Ownership.Agent?.Instance.Revision) throw new SecurityConflictException("stale_revision");
        return new(view.Workspace, view.State.Tenant.Revision, view.Ownership.Revision, read, write, delete, !writable,
            view.Ownership.Name, view.Ownership.Session?.Snapshot.Title);
    }
    private async ValueTask<View> Require(Guid tenant, Guid id, bool working, HumanSessionReference human, long expected, string permission, CancellationToken ct)
    {
        var view = await Load(tenant, id, working, human, ct).ConfigureAwait(false);
        var state = view.State; var owner = view.Workspace;
        if (!await TenantWorkspaceMutationRules.Allowed(state, owner, human.PrincipalId, permission, iam, ct).ConfigureAwait(false))
        {
            await Record(view, human, permission, false, ct).ConfigureAwait(false); throw new AccessDeniedException("resource_unavailable");
        }
        if (!view.Ownership.Writable)
        {
            await Record(view, human, permission, false, ct).ConfigureAwait(false); throw new SecurityConflictException("workspace_read_only");
        }
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return view;
    }
    private ValueTask<bool> Record(View view, HumanSessionReference human, string permission, bool allowed, CancellationToken ct) =>
        audits.TryRecordWorkspaceAuthorizationAsync(new(view.Workspace, human, view.State.Tenant.Revision, view.Ownership.Revision, permission,
            new(ids.NewId(), view.Workspace.TenantId, human.PrincipalId, permission, TenantWorkspaceRules.Scope(view.Workspace).ResourceId!,
                allowed ? "allowed" : "denied", allowed ? "human_workspace_authorized" : "human_workspace_denied",
                DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()))), ct);
    public async ValueTask<TenantWorkspaceReference> AuthorizeAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, long expected, bool delete, CancellationToken ct = default) =>
        (await Require(tenant, id, working, human, expected, delete ? "workspaces.delete" : "workspaces.write", ct).ConfigureAwait(false)).Workspace;
    public ValueTask WriteAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, long expected, string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        if (bytes.Length > AgentWorkspaceLimits.MaxFileBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        return Mutate(tenant, id, working, human, expected, path, false, bytes.ToArray(), ct);
    }
    public ValueTask DeleteAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, long expected, string path, CancellationToken ct = default) =>
        Mutate(tenant, id, working, human, expected, path, true, [], ct);
    private async ValueTask Mutate(Guid tenant, Guid id, bool working, HumanSessionReference human, long expected, string path, bool delete, byte[] bytes, CancellationToken ct)
    {
        var permission = delete ? "workspaces.delete" : "workspaces.write";
        var before = await Require(tenant, id, working, human, expected, permission, ct).ConfigureAwait(false);
        path = TenantWorkspaceRules.Path(before.Workspace, path, file: true);
        async ValueTask Commit(CancellationToken token)
        {
            var current = await Require(tenant, id, working, human, expected, permission, token).ConfigureAwait(false);
            if (current.Workspace != before.Workspace || current.Ownership.Revision != before.Ownership.Revision || current.Ownership.Agent?.Instance.Revision != before.Ownership.Agent?.Instance.Revision)
                throw new SecurityConflictException("stale_revision");
            if (!await Record(current, human, permission, true, token).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        }
        try
        {
            if (delete) await files.DeleteTenantAsync(before.Workspace, path, Commit, ct).ConfigureAwait(false);
            else await files.WriteTenantAsync(before.Workspace, path, bytes, Commit, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AgentCoreException("workspace_effect_unknown", "The file operation could not be confirmed. Reload current files before deciding whether to try again.", 409); }
        // The protected primitive has completed. Do not revoke a factual acknowledgement or project private metadata afterward.
    }
}
