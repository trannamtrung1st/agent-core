using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Exact source provenance only; source IAM and creation authority remain independent compound gates.
public static class TenantAttachmentArtifactRules
{
    public static bool IsPath(string path) => path.StartsWith("/attachments/", StringComparison.Ordinal);
    public static Guid Id(string path)
    {
        if (!IsPath(path) || !Guid.TryParseExact(path[13..], "D", out var id) || id == Guid.Empty || path != "/attachments/" + id.ToString("D"))
            throw new ArgumentException("Use an exact canonical Attachment source path.");
        return id;
    }
    public static TenantWorkspaceArtifactObservation Observation(TenantSessionRoot owner, AttachmentRecord item)
    {
        if (item.TenantId != owner.TenantId || item.SessionId != owner.SessionId || item.State != AttachmentState.Bound || item.EntryId is null)
            throw new AccessDeniedException("attachment_source_changed");
        if (item.EntryId == Guid.Empty) throw new AccessDeniedException("attachment_source_changed");
        return new(new(owner.TenantId, owner.AgentInstanceId, owner.SessionId), "/attachments/" + item.AttachmentId.ToString("D"), item.ContentType,
            item.ByteSize, item.Sha256Hex, Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(item))), AttachmentId: item.AttachmentId, EntryId: item.EntryId, AttachmentName: item.DisplayName);
    }
    public static void Require(PreparedArtifactSource saved, TenantSessionRoot owner, AttachmentRecord? item)
    {
        if (item is null || TenantArtifactSourceRules.Witness(Observation(owner, item)) != saved)
            throw new AccessDeniedException("attachment_source_changed");
    }
}
