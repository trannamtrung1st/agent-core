using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareWorkspaceRead(Guid id, Guid instance, PreparedWorkspaceRead value, long revision, long personaRevision)
    {
        var args = TenantWorkspaceReadContract.Parse(value.ToolName, value.ArgumentsJson);
        var owner = new TenantWorkspaceReference(Guid.Empty, instance, value.Working ? value.SessionId : null);
        if (value.ResourceMembersJson is not null) _ = ResourceReadMembers(value);
        else if (value.ResourceKey is { } key)
        {
            if (!TenantAgentChildScopes.TryParse(AccessScope.ForResource(instance, "resource", instance.ToString("D") + ":" + key), out _, out _)) throw new ArgumentException("Invalid resource key.");
            if (value.Working || value.ResourceRevision is null or < 0 || !TenantWorkspaceReadContract.IsFile(value.ToolName) || !TenantWorkspaceReadContract.IsResourcePath(args.Path)) throw new ArgumentException("Invalid exact resource observation.");
        }
        else
        {
            if (value.ResourceRevision is not null) throw new ArgumentException("Unexpected resource revision.");
            TenantWorkspaceRules.Path(owner, args.Path, TenantWorkspaceReadContract.IsFile(value.ToolName));
        }
        if (value.ResourceMembersJson is null && args.Cursor is not null && value.ToolName == ToolCatalog.WorkspaceList) TenantWorkspaceRules.Path(owner, args.Cursor);
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || revision < 1 || personaRevision < 1
            || TenantWorkspaceReadContract.Canonical(value.ToolName, args) != value.ArgumentsJson || value.ObservationHash is not { Length: 64 }
            || value.ObservationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Invalid exact workspace observation.");
        var json = JsonSerializer.Serialize(new { operation = value.ToolName, permission = value.ResourceMembersJson is not null ? "sessions.read" : value.ResourceKey is null ? "workspaces.read" : "resources.read", read = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), WorkspaceRead: value);
    }
}
