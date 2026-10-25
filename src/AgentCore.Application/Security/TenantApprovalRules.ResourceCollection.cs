using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static IReadOnlyList<PreparedResourceReadMember> ResourceReadMembers(PreparedWorkspaceRead value)
    {
        var args = TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson);
        if (value.Working || value.ResourceKey is not null || value.ResourceRevision is not null
            || value.ToolName is not (ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch) || !TenantWorkspaceReadContract.IsResourcePath(args.Path)
            || value.ResourceMembersJson is null || Encoding.UTF8.GetByteCount(value.ResourceMembersJson) > 65536)
            throw new ArgumentException("Use a bounded exact resource observation.");
        var refs = JsonSerializer.Deserialize<PreparedResourceReadMember[]>(value.ResourceMembersJson);
        if (refs is null || refs.Length > 257 || refs.Any(r => r is null || r.Revision < 0
                || !TenantResourceScopes.IsKey(r.Key))
            || refs.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != refs.Length
            || JsonSerializer.Serialize(refs.OrderBy(r => r.Key, StringComparer.Ordinal)) != value.ResourceMembersJson)
            throw new ArgumentException("Invalid exact resource member set.");
        return refs;
    }
}
