using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAttachmentRules
{
    public static Sessions.AgentCoreException Quota() => new("AttachmentQuotaExceeded", "Attachment storage limit exceeded. Remove pending files or wait for cleanup.", 413);
    public static AccessScope Scope(Guid tenant, Guid attachment) => AccessScope.ForResource(tenant, "attachment", attachment.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = default;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "attachment"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static string BlobKey(Guid tenant, Guid session, Guid attachment) => $"tenants/{tenant:N}/{session:N}/{attachment:N}";
    public static void Query(Guid tenant, Guid session, int limit)
    { if (tenant == Guid.Empty || session == Guid.Empty || limit is < 1 or > 100) throw new ArgumentException("Use bounded owned attachment queries."); }
    public static bool Available(AttachmentRecord item, DateTimeOffset at) => item.State == AttachmentState.Bound
        || item.State == AttachmentState.Pending && item.ExpiresAt > at;
    public static void Content(AttachmentRecord item, ReadOnlySpan<byte> bytes)
    {
        if (item.ByteSize is < 0 or > AttachmentLimits.MaxBytesEach || item.ByteSize != bytes.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != item.Sha256Hex)
            throw new AccessDeniedException("attachment_content_unavailable");
    }
    public static AttachmentRecord Pending(TenantSessionRoot owner, Guid id, Guid request, Guid actor, string name, string type, bool readable, ReadOnlySpan<byte> bytes, DateTimeOffset at)
    {
        if (owner.TenantId == Guid.Empty || owner.SessionId == Guid.Empty || owner.AgentInstanceId == Guid.Empty || owner.ExecutorPrincipalId == Guid.Empty
            || id == Guid.Empty || request == Guid.Empty || actor == Guid.Empty || bytes.Length > AttachmentLimits.MaxBytesEach) throw new ArgumentException("Invalid owned attachment upload.");
        at = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
        return new(id, owner.SessionId, BlobKey(owner.TenantId, owner.SessionId, id), AttachmentClassification.SanitizeDisplayName(name), type,
            bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), AttachmentState.Pending, readable, null, false, at, at + AttachmentLimits.PendingTtl, null,
            owner.TenantId, request, actor);
    }
    public static bool SameUpload(AttachmentRecord saved, AttachmentRecord incoming) => saved.TenantId == incoming.TenantId && saved.SessionId == incoming.SessionId
        && saved.UploadRequestId == incoming.UploadRequestId && saved.UploadedByPrincipalId == incoming.UploadedByPrincipalId && saved.DisplayName == incoming.DisplayName
        && saved.ContentType == incoming.ContentType && saved.ByteSize == incoming.ByteSize && saved.Sha256Hex == incoming.Sha256Hex && saved.Readable == incoming.Readable;
    public static void Session(TenantSessionRoot owner, TenantSession session, TenantAgent agent, bool write)
    {
        if (session.Owner != owner || agent.TenantId != owner.TenantId || agent.Instance.InstanceId != owner.AgentInstanceId || agent.PrincipalId != owner.ExecutorPrincipalId
            || session.Snapshot.DurablyDeletedAt is not null) throw new AccessDeniedException("resource_unavailable");
        if (write && (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended))
            throw new SecurityConflictException("attachments_read_only");
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, Guid actor, Guid session, Guid? attachment, string permission, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, session)),
            (permission, attachment is { } id ? Scope(state.Tenant.TenantId, id) : TenantSessionRules.Scope(state.Tenant.TenantId, session)) })
            if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("resource_unavailable");
        if (attachment is { } file && permission != "attachments.read"
            && !(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get("attachments.read"), Scope(state.Tenant.TenantId, file), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("resource_unavailable");
    }
    public static void Upload(TenantAttachmentMutation mutation, TenantAttachmentUpload upload, DateTimeOffset now)
    {
        var item = upload.Record; Content(item, upload.Bytes);
        if (item.UploadedByPrincipalId != mutation.Human.PrincipalId || item.UploadRequestId != mutation.RequestId || item.CreatedAt > now || !Available(item, now)
            || item != Pending(mutation.Owner, item.AttachmentId, mutation.RequestId, mutation.Human.PrincipalId, item.DisplayName, item.ContentType, item.Readable, upload.Bytes, item.CreatedAt))
            throw new SecurityConflictException("attachment_upload_mismatch");
        var inspect = AttachmentClassification.Inspect(upload.Bytes.AsSpan(0, Math.Min(512, upload.Bytes.Length)),
            upload.Bytes.AsSpan(Math.Max(0, upload.Bytes.Length - 32)), upload.Bytes.Length, item.ContentType, item.DisplayName, true);
        if (!inspect.Accepted || inspect.ContentType != item.ContentType || inspect.Readable != item.Readable) throw new SecurityConflictException("attachment_upload_mismatch");
    }
    public static void Mutation(TenantAttachmentMutation value, string action, Guid? attachment = null)
    {
        var target = attachment?.ToString("D") ?? value.Owner.SessionId.ToString("D");
        if (value.Owner.TenantId == Guid.Empty || value.Human.PrincipalId == Guid.Empty || value.SecurityRevision < 1 || value.SessionRevision < 1 || value.InstanceRevision < 1
            || value.RequestId == Guid.Empty || value.Audit.TenantId != value.Owner.TenantId || value.Audit.ActorPrincipalId != value.Human.PrincipalId
            || value.Audit.AuditId != value.RequestId || value.Audit.Action != action || value.Audit.Target != target || value.Audit.Decision != "allowed" || value.Audit.ReasonCode != "human_attachment_authorized")
            throw new ArgumentException("Invalid attachment mutation evidence.");
    }
}
