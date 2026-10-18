using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantWorkspaceReadService(IIamStore security, ITenantAgentStore agents, ITenantSessionStore sessions,
    ITenantWorkspaceReadStore files, IdentityService identity, IamAuthorizer authorizer)
{
    private sealed record View(TenantSecurityState State, TenantAgent Agent, TenantSession? Session, TenantWorkspaceReference Workspace);
    private async ValueTask<View> Require(Guid tenant, Guid id, bool working, HumanSessionReference human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var session = working ? await sessions.FindTenantSessionAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable") : null;
        var instance = session?.Owner.AgentInstanceId ?? id;
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (session is not null && session.Owner.ExecutorPrincipalId != agent.PrincipalId) throw new AccessDeniedException("resource_unavailable");
        var workspace = new TenantWorkspaceReference(tenant, instance, session?.Owner.SessionId);
        var parent = working ? TenantSessionRules.Scope(tenant, id) : AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
        foreach (var (permission, scope) in new[] { (working ? "sessions.read" : "agents.read", parent), ("workspaces.read", TenantWorkspaceRules.Scope(workspace)) })
            if (!(await authorizer.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("resource_unavailable");
        return new(state, agent, session, workspace);
    }
    private async ValueTask<T> Fenced<T>(Guid tenant, Guid id, bool working, HumanSessionReference human, Func<View, CancellationToken, ValueTask<T>> read, CancellationToken ct)
    {
        var before = await Require(tenant, id, working, human, ct).ConfigureAwait(false);
        var result = await read(before, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var after = await Require(tenant, id, working, human, ct).ConfigureAwait(false);
        if (after.State.Tenant.Revision != before.State.Tenant.Revision || after.Workspace != before.Workspace
            || after.Agent.Instance.Revision != before.Agent.Instance.Revision || after.Session?.Owner != before.Session?.Owner
            || after.Session?.Snapshot.Revision != before.Session?.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        return result;
    }
    public ValueTask<AgentWorkspacePage> ListAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, string? path = null,
        string? cursor = null, int limit = 100, CancellationToken ct = default) => Fenced(tenant, id, working, human, (view, token) =>
    {
        var prefix = TenantWorkspaceRules.Path(view.Workspace, path ?? view.Workspace.LogicalRoot);
        if (cursor is not null) TenantWorkspaceRules.Path(view.Workspace, cursor);
        return files.ListTenantAsync(view.Workspace, prefix, cursor, limit, token);
    }, ct);
    public ValueTask<AgentWorkspacePage> SearchAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, string query,
        string? cursor = null, int limit = 100, CancellationToken ct = default) => Fenced(tenant, id, working, human, (view, token) =>
    {
        if (cursor is not null) TenantWorkspaceRules.Path(view.Workspace, cursor.TrimEnd('/'));
        return files.SearchTenantAsync(view.Workspace, query, cursor, limit, token);
    }, ct);
    public ValueTask<AgentWorkspaceContent> MetadataAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, string path, CancellationToken ct = default) =>
        Fenced(tenant, id, working, human, (view, token) => files.InspectTenantAsync(view.Workspace, TenantWorkspaceRules.Path(view.Workspace, path, file: true), 0, token), ct);
    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid tenant, Guid id, bool working, HumanSessionReference human, string path, CancellationToken ct = default) =>
        ProjectAsync(tenant, id, working, human, path, _ => checked((int)AgentWorkspaceLimits.MaxFileBytes), (content, _) => ValueTask.FromResult(content), ct);
    public ValueTask<T> ProjectAsync<T>(Guid tenant, Guid id, bool working, HumanSessionReference human, string path,
        Func<AgentWorkspaceItem, int> limit, Func<AgentWorkspaceContent, CancellationToken, ValueTask<T>> project, CancellationToken ct = default) =>
        Fenced(tenant, id, working, human, async (view, token) =>
        {
            var exact = TenantWorkspaceRules.Path(view.Workspace, path, file: true);
            var metadata = await files.InspectTenantAsync(view.Workspace, exact, 0, token).ConfigureAwait(false);
            var maximum = limit(metadata.Item);
            if (maximum < 1 || maximum > AgentWorkspaceLimits.MaxFileBytes) throw new ArgumentException("Invalid workspace read limit.");
            if (metadata.Item.ByteSize > maximum) throw new Sessions.AgentCoreException("preview_too_large", "The file exceeds its preview limit. Download the original.", 413);
            var content = await files.InspectTenantAsync(view.Workspace, exact, maximum, token).ConfigureAwait(false);
            if (content.Item != metadata.Item || content.Bytes.LongLength != content.Item.ByteSize) throw new SecurityConflictException("workspace_source_changed");
            var result = await project(content, token).ConfigureAwait(false);
            if ((await files.InspectTenantAsync(view.Workspace, exact, 0, token).ConfigureAwait(false)).Item != content.Item)
                throw new SecurityConflictException("workspace_source_changed");
            return result;
        }, ct);
}
