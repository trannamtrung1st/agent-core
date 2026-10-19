using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static IReadOnlyList<PreparedAttachmentReadMember> AttachmentReadMembers(PreparedWorkspaceRead value)
    {
        var args = TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson);
        if (value.Working || value.ResourceKey is not null || value.ResourceRevision is not null || value.ResourceMembersJson is not null || value.Knowledge is not null
            || args.Path != "/attachments" || value.ToolName is not (ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch)
            || value.AttachmentMembersJson is null || Encoding.UTF8.GetByteCount(value.AttachmentMembersJson) > 65536) throw new ArgumentException("Use an exact bounded Attachment namespace observation.");
        var members = JsonSerializer.Deserialize<PreparedAttachmentReadMember[]>(value.AttachmentMembersJson);
        if (members is null || members.Length > 257 || members.Any(m => m is null || m.AttachmentId == Guid.Empty || m.EntryId == Guid.Empty
                || m.Sha256Hex is not { Length: 64 } || m.Sha256Hex.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            || members.Select(m => m.AttachmentId).Distinct().Count() != members.Length
            || JsonSerializer.Serialize(members.OrderBy(m => m.AttachmentId.ToString("D"), StringComparer.Ordinal)) != value.AttachmentMembersJson) throw new ArgumentException("Invalid exact Attachment member set.");
        return members;
    }
}
