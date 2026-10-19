using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAttachmentContent(AttachmentRecord Attachment, byte[] Bytes);

public sealed class TenantAttachmentService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    ITenantAttachmentStore attachments, ITenantAttachmentMutationStore mutations, IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time)
{
    private sealed record View(TenantSecurityState State, TenantSession Session, TenantAgent Agent);
    private async ValueTask<View> Load(Guid tenant, Guid session, HumanSessionReference human, bool write, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantAttachmentRules.Session(saved.Owner, saved, agent, write);
        if (!(await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(tenant, session), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
        return new(state, saved, agent);
    }
    private async ValueTask Fence(View before, HumanSessionReference human, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var after = await Load(before.State.Tenant.TenantId, before.Session.Owner.SessionId, human, false, ct).ConfigureAwait(false);
        if (before.State.Tenant.Revision != after.State.Tenant.Revision || before.Session.Owner != after.Session.Owner
            || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision || before.Agent.Instance.Revision != after.Agent.Instance.Revision)
            throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<TenantAttachmentPage> ListAsync(Guid tenant, Guid session, HumanSessionReference human, Guid? before = null, int limit = 20, CancellationToken ct = default)
    {
        TenantAttachmentRules.Query(tenant, session, limit); var view = await Load(tenant, session, human, false, ct).ConfigureAwait(false);
        List<Guid>? allowed = null;
        if (!(await iam.CheckTenantAsync(view.State, human.PrincipalId, PermissionCatalog.Get("attachments.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed)
        {
            allowed = [];
            foreach (var scope in view.State.Assignments.Where(a => a.PrincipalId == human.PrincipalId).Select(a => a.Scope).Distinct())
                if (scope.TenantId == tenant && TenantAttachmentRules.TryId(scope, out var id)
                    && (await iam.CheckTenantAsync(view.State, human.PrincipalId, PermissionCatalog.Get("attachments.read"), scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(id);
        }
        var page = await attachments.ListTenantAsync(tenant, session, allowed, before, limit, ct).ConfigureAwait(false);
        foreach (var item in page.Items) await TenantAttachmentRules.PermissionsAsync(view.State, human.PrincipalId, session, item.AttachmentId, "attachments.read", iam, ct).ConfigureAwait(false);
        await Fence(view, human, ct).ConfigureAwait(false);
        if (page.Items.Any(item => !TenantAttachmentRules.Available(item, time.GetUtcNow()))) throw new AccessDeniedException("resource_unavailable");
        return page;
    }
    public async ValueTask<AttachmentRecord> ReadAsync(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, CancellationToken ct = default)
    {
        var view = await Load(tenant, session, human, false, ct).ConfigureAwait(false);
        var item = await attachments.FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentRules.PermissionsAsync(view.State, human.PrincipalId, session, attachment, "attachments.read", iam, ct).ConfigureAwait(false);
        await Fence(view, human, ct).ConfigureAwait(false);
        if (!TenantAttachmentRules.Available(item, time.GetUtcNow())) throw new AccessDeniedException("resource_unavailable");
        return item;
    }
    public ValueTask<TenantAttachmentContent> ReadContentAsync(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, CancellationToken ct = default) =>
        ProjectAsync(tenant, session, attachment, human, _ => int.MaxValue, (source, _) => ValueTask.FromResult(source), ct);
    public async ValueTask<T> ProjectAsync<T>(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, Func<AttachmentRecord, int> maximum,
        Func<TenantAttachmentContent, CancellationToken, ValueTask<T>> project, CancellationToken ct = default)
    {
        var view = await Load(tenant, session, human, false, ct).ConfigureAwait(false);
        var item = await attachments.FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentRules.PermissionsAsync(view.State, human.PrincipalId, session, attachment, "attachments.read", iam, ct).ConfigureAwait(false);
        if (item.ByteSize > maximum(item)) throw new Sessions.AgentCoreException("preview_too_large", "The file exceeds its preview limit. Download the original.", 413);
        var bytes = await attachments.ReadTenantContentAsync(tenant, session, attachment, ct).ConfigureAwait(false); TenantAttachmentRules.Content(item, bytes);
        var result = await project(new(item, bytes), ct).ConfigureAwait(false);
        if (await attachments.FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) != item) throw new AccessDeniedException("resource_unavailable");
        await Fence(view, human, ct).ConfigureAwait(false);
        if (!TenantAttachmentRules.Available(item, time.GetUtcNow())) throw new AccessDeniedException("resource_unavailable");
        return result;
    }
    private TenantAttachmentMutation Proof(View view, HumanSessionReference human, Guid request, long revision, long sessionRevision, string action, Guid? attachment = null) =>
        new(view.Session.Owner, human, revision, sessionRevision, view.Agent.Instance.Revision, request,
            new(request, view.State.Tenant.TenantId, human.PrincipalId, action, (attachment ?? view.Session.Owner.SessionId).ToString("D"), "allowed", "human_attachment_authorized",
                DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds())));
    public async ValueTask AuthorizeUploadAsync(Guid tenant, Guid session, HumanSessionReference human, CancellationToken ct = default)
    {
        var view = await Load(tenant, session, human, true, ct).ConfigureAwait(false);
        await TenantAttachmentRules.PermissionsAsync(view.State, human.PrincipalId, session, null, "attachments.create", iam, ct).ConfigureAwait(false);
    }
    public async ValueTask<Guid> UploadAsync(Guid tenant, Guid session, HumanSessionReference human, Guid request, long revision, long sessionRevision,
        string name, string type, Stream content, CancellationToken ct = default)
    {
        if (request == Guid.Empty || revision < 1 || sessionRevision < 1) throw new ArgumentException("Use an upload identity and current revisions.");
        var view = await Load(tenant, session, human, true, ct).ConfigureAwait(false);
        await TenantAttachmentRules.PermissionsAsync(view.State, human.PrincipalId, session, null, "attachments.create", iam, ct).ConfigureAwait(false);
        var upload = await attachments.PrepareUploadTenantAsync(view.Session.Owner, ids.NewId(), request, human.PrincipalId, name, type, content, ct).ConfigureAwait(false);
        AttachmentRecord saved;
        try { saved = await mutations.TryUploadAttachmentAsync(Proof(view, human, request, revision, sessionRevision, "attachments.create"), upload, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new Sessions.AgentCoreException("attachment_effect_unknown", "The upload could not be confirmed. Reload files, then retry with the same upload identity if needed.", 409); }
        // Protected commit completed: return only a factual identity; metadata/download access stays independent.
        return saved.AttachmentId;
    }
    public async ValueTask AbortAsync(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, Guid request, long revision, long sessionRevision, CancellationToken ct = default)
    {
        var view = await Load(tenant, session, human, true, ct).ConfigureAwait(false);
        if (!await mutations.TryAbortAttachmentAsync(Proof(view, human, request, revision, sessionRevision, "attachments.delete", attachment), attachment, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
    }
}
