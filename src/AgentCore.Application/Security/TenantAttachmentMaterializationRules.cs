using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAttachmentMaterializationRules
{
    public static async ValueTask RequireAsync(TenantSecurityState state, TenantSession session, TenantAgent agent,
        Guid human, AttachmentRecord source, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct)
    {
        TenantAttachmentRules.Session(session.Owner, session, agent, true);
        if (source.TenantId != state.Tenant.TenantId || source.SessionId != session.Owner.SessionId || !TenantAttachmentRules.Available(source, at))
            throw new AccessDeniedException("resource_unavailable");
        await TenantAttachmentRules.PermissionsAsync(state, human, source.SessionId, source.AttachmentId, "attachments.read", iam, ct).ConfigureAwait(false);
        var workspace = new TenantWorkspaceReference.SessionWorking(state.Tenant.TenantId, source.SessionId);
        if (!await TenantWorkspaceMutationRules.Allowed(state, workspace, human, "workspaces.write", iam, ct).ConfigureAwait(false)
            || !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("artifacts.create"), TenantSessionRules.Scope(state.Tenant.TenantId, source.SessionId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
    }
    public static ArtifactRecord Record(TenantSessionRoot owner, Guid human, Guid id, AttachmentRecord source, string path, ReadOnlySpan<byte> bytes, DateTimeOffset at)
    {
        TenantAttachmentRules.Content(source, bytes);
        var name = path.StartsWith("/working/", StringComparison.Ordinal) ? path[9..] : "";
        if (owner.TenantId == Guid.Empty || owner.SessionId == Guid.Empty || id == Guid.Empty || human == Guid.Empty
            || source.TenantId != owner.TenantId || source.SessionId != owner.SessionId || name.Length is < 1 or > 200 || name[0] == '.' || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("Invalid materialization provenance.");
        // Reuse incumbent MIME/name/checksum validation without manufacturing a Run.
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(source.ContentType, out _) || source.ContentType.Length > 200 || source.ContentType.Any(char.IsControl))
            throw new ArgumentException("Invalid source content type.");
        return new(id, owner.SessionId, name, source.ContentType, source.ByteSize, source.Sha256Hex, source.AttachmentId, path,
            DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()), null, owner.TenantId, human);
    }
    public static void Publication(TenantAttachmentMutation proof, AttachmentRecord source, ArtifactRecord artifact, ReadOnlySpan<byte> bytes)
    {
        TenantAttachmentRules.Mutation(proof, "artifacts.create", source.AttachmentId);
        if (artifact != Record(proof.Owner, proof.Human.PrincipalId, proof.RequestId, source, artifact.WorkspaceLogicalPath ?? "", bytes, artifact.CreatedAt))
            throw new SecurityConflictException("artifact_publication_mismatch");
    }
}
