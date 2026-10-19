using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAttachmentMaterialized(Guid ArtifactId, string WorkspaceLogicalPath);

public sealed class TenantAttachmentMaterializationService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    ITenantAttachmentStore attachments, ITenantAttachmentMutationStore mutations, ITenantWorkspaceMutationStore files,
    ITenantAuthorizationAuditStore audits, IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time)
{
    private sealed record View(TenantSecurityState State, TenantSession Session, TenantAgent Agent, AttachmentRecord Source);
    private async ValueTask<View> Require(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, long revision, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var saved = await sessions.FindTenantSessionAsync(tenant, session, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var agent = await agents.FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var source = await attachments.FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentMaterializationRules.RequireAsync(state, saved, agent, human.PrincipalId, source, iam, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (state.Tenant.Revision != revision) throw new SecurityConflictException("stale_revision");
        return new(state, saved, agent, source);
    }
    public async ValueTask<TenantAttachmentMaterialized> MaterializeAsync(Guid tenant, Guid session, Guid attachment, HumanSessionReference human, long revision, CancellationToken ct = default)
    {
        if (revision < 1) throw new ArgumentException("Use a current security revision.");
        var before = await Require(tenant, session, attachment, human, revision, ct).ConfigureAwait(false);
        var bytes = await attachments.ReadTenantContentAsync(tenant, session, attachment, ct).ConfigureAwait(false); TenantAttachmentRules.Content(before.Source, bytes);
        var workspace = new TenantWorkspaceReference(tenant, before.Session.Owner.AgentInstanceId, session);
        async ValueTask Guard(CancellationToken token)
        {
            var now = await Require(tenant, session, attachment, human, revision, token).ConfigureAwait(false);
            if (now.Source != before.Source || now.Session.Owner != before.Session.Owner || now.Agent.Instance.Revision != before.Agent.Instance.Revision)
                throw new SecurityConflictException("stale_revision");
            TenantAttachmentRules.Content(before.Source, await attachments.ReadTenantContentAsync(tenant, session, attachment, token).ConfigureAwait(false));
            if (!await audits.TryRecordWorkspaceAuthorizationAsync(new(workspace, human, revision, before.Agent.Instance.Revision, "workspaces.write",
                new(ids.NewId(), tenant, human.PrincipalId, "workspaces.write", TenantWorkspaceRules.Scope(workspace).ResourceId!, "allowed", "human_workspace_authorized",
                    DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()))), token).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        }
        string? chosen = null;
        try
        {
            var name = WorkspaceFileNames.Sanitize(before.Source.DisplayName);
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            for (var attempt = 1; attempt <= 128; attempt++)
            {
                var path = "/working/" + WorkspaceFileNames.Deduplicate(name, occupied);
                if (await files.TryWriteNewTenantAsync(workspace, path, bytes, Guard, ct).ConfigureAwait(false)) { chosen = path; break; }
                occupied.Add(path[9..].ToLowerInvariant());
            }
            if (chosen is null) throw new SecurityConflictException("workspace_name_conflict");
            await Guard(ct).ConfigureAwait(false);
            var id = ids.NewId();
            var artifact = TenantAttachmentMaterializationRules.Record(before.Session.Owner, human.PrincipalId, id, before.Source, chosen, bytes, time.GetUtcNow());
            var proof = new TenantAttachmentMutation(before.Session.Owner, human, revision, before.Session.Snapshot.Revision, before.Agent.Instance.Revision, id,
                new(id, tenant, human.PrincipalId, "artifacts.create", attachment.ToString("D"), "allowed", "human_attachment_authorized", artifact.CreatedAt));
            if (await mutations.TryPublishMaterializationAsync(proof, before.Source, artifact, bytes, ct).ConfigureAwait(false) is null)
                throw new SecurityConflictException("stale_revision");
            return new(id, chosen);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AgentCoreException("attachment_materialization_unknown", "The file copies could not be confirmed. Reload working files and Artifacts before deciding on another copy.", 409); }
        catch (Exception error) when (chosen is not null && error is (AccessDeniedException or SecurityConflictException or AgentCoreException))
        { throw new AgentCoreException("attachment_materialization_incomplete", "The working copy completed, but Artifact publication did not complete. Reload files before deciding on another copy.", 409); }
        catch (Exception error) when (chosen is not null && error is not OperationCanceledException)
        { throw new AgentCoreException("attachment_materialization_unknown", "Artifact publication could not be confirmed. Reload files and Artifacts before deciding on another copy.", 409); }
    }
}
