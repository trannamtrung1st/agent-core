using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantArtifactContent(ArtifactRecord Artifact, byte[] Bytes);

public sealed class TenantArtifactService(IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs,
    ITenantArtifactStore artifacts, IdentityService identity, IamAuthorizer authorizer)
{
    private async ValueTask<(TenantSecurityState State, TenantSession Session)> Require(Guid tenant, Guid session, HumanSessionReference human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(tenant, session), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
        return (state, saved);
    }
    private async ValueTask RequireArtifact(TenantSecurityState state, Guid actor, ArtifactRecord item, CancellationToken ct)
    {
        if (item.TenantId != state.Tenant.TenantId || !(await authorizer.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get("artifacts.read"),
            TenantArtifactRules.Scope(state.Tenant.TenantId, item.ArtifactId), ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("resource_unavailable");
    }
    private async ValueTask Fence(TenantSecurityState state, TenantSession session, HumanSessionReference human, CancellationToken ct)
    {
        var current = await Require(state.Tenant.TenantId, session.Owner.SessionId, human, ct).ConfigureAwait(false);
        var authority = await TenantHumanSessionAuthority.RequireAsync(security, identity, state.Tenant.TenantId, human, ct).ConfigureAwait(false);
        if (authority.Tenant.Revision != state.Tenant.Revision || current.State.Tenant.Revision != state.Tenant.Revision || current.Session.Owner != session.Owner
            || current.Session.Snapshot.Revision != session.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<ArtifactPage> ListAsync(Guid tenant, Guid session, HumanSessionReference human, Guid? before = null,
        int limit = 20, Guid? run = null, CancellationToken ct = default)
    {
        TenantArtifactRules.Query(tenant, session, limit);
        var (state, saved) = await Require(tenant, session, human, ct).ConfigureAwait(false);
        if (run is { } runId && ((await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false))?.SessionId != session
            || !(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agent-runs.read"), TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false)).Allowed))
            throw new AccessDeniedException("resource_unavailable");
        List<Guid>? allowed = null;
        if (!(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("artifacts.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed)
        {
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId).Select(a => a.Scope).Distinct())
                if (TenantArtifactRules.TryId(scope, out var id) && scope.TenantId == tenant
                    && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("artifacts.read"), scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(id);
        }
        var page = await artifacts.ListTenantAsync(tenant, session, allowed, before, limit, run, ct).ConfigureAwait(false);
        foreach (var item in page.Items) await RequireArtifact(state, human.PrincipalId, item, ct).ConfigureAwait(false);
        await Fence(state, saved, human, ct).ConfigureAwait(false); return page;
    }
    public async ValueTask<ArtifactRecord> ReadAsync(Guid tenant, Guid session, Guid artifact, HumanSessionReference human, CancellationToken ct = default)
    {
        var (state, saved) = await Require(tenant, session, human, ct).ConfigureAwait(false);
        var item = await artifacts.FindTenantAsync(tenant, session, artifact, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireArtifact(state, human.PrincipalId, item, ct).ConfigureAwait(false);
        await Fence(state, saved, human, ct).ConfigureAwait(false); return item;
    }
    public async ValueTask<TenantArtifactContent> ReadContentAsync(Guid tenant, Guid session, Guid artifact, HumanSessionReference human, CancellationToken ct = default)
        => await ProjectContentAsync(tenant, session, artifact, human, _ => int.MaxValue,
            (source, _) => ValueTask.FromResult(source), ct).ConfigureAwait(false);

    public async ValueTask<T> ProjectContentAsync<T>(Guid tenant, Guid session, Guid artifact, HumanSessionReference human,
        Func<ArtifactRecord, int> limit, Func<TenantArtifactContent, CancellationToken, ValueTask<T>> project, CancellationToken ct = default)
    {
        var (state, saved) = await Require(tenant, session, human, ct).ConfigureAwait(false);
        var item = await artifacts.FindTenantAsync(tenant, session, artifact, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireArtifact(state, human.PrincipalId, item, ct).ConfigureAwait(false);
        var maximum = limit(item);
        if (item.ByteSize > maximum) throw new Sessions.AgentCoreException("preview_too_large", "The file exceeds its preview limit. Download the original.", 413);
        byte[] bytes;
        try { bytes = await artifacts.ReadTenantContentAsync(tenant, session, artifact, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AccessDeniedException("resource_unavailable"); }
        TenantArtifactRules.RequireContent(item, bytes);
        var result = await project(new(item, bytes), ct).ConfigureAwait(false);
        if (await artifacts.FindTenantAsync(tenant, session, artifact, ct).ConfigureAwait(false) != item) throw new AccessDeniedException("resource_unavailable");
        await Fence(state, saved, human, ct).ConfigureAwait(false); return result;
    }
}
