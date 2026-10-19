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
        TenantWorkspaceRules.Path(owner, args.Path, TenantWorkspaceReadContract.IsFile(value.ToolName));
        if (args.Cursor is not null && value.ToolName == ToolCatalog.WorkspaceList) TenantWorkspaceRules.Path(owner, args.Cursor);
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || revision < 1 || personaRevision < 1
            || TenantWorkspaceReadContract.Canonical(value.ToolName, args) != value.ArgumentsJson || value.ObservationHash is not { Length: 64 }
            || value.ObservationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new ArgumentException("Invalid exact workspace observation.");
        var json = JsonSerializer.Serialize(new { operation = value.ToolName, permission = "workspaces.read", read = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(id, instance, null, revision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), WorkspaceRead: value);
    }
}
