using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareAttachmentRead(Guid id, Guid instance, PreparedAttachmentRead value, long revision, long personaRevision)
    {
        var args = TenantAttachmentReadContract.Parse(value.ArgumentsJson);
        TenantAttachmentReadContract.Window(args, value.ContentType);
        _ = TenantAttachmentContextRules.Merge(null, [new(value.AttachmentId, value.EntryId, value.DisplayName, value.ContentType, value.ByteSize, value.Sha256Hex, value.ProcessorVersion)]);
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || revision < 1 || personaRevision < 1
            || args.AttachmentId != value.AttachmentId || TenantAttachmentReadContract.Canonical(args) != value.ArgumentsJson)
            throw new ArgumentException("Invalid exact Attachment read.");
        var json = JsonSerializer.Serialize(new { operation = "attachments.read", permission = "attachments.read", resourceType = "attachment",
            resourceId = value.AttachmentId.ToString("D"), read = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), AttachmentRead: value);
    }
}
