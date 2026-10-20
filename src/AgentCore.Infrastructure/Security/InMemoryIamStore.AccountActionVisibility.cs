using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private static bool AccountActionApprovalVisible(TenantApprovalRequest request, IReadOnlyList<Guid>? uses,
        TenantCredentialReadApprovalVisibility? accounts, TenantWorkspaceMutationApprovalVisibility? workspace)
    {
        var action = request.Operation.AccountAction!; var id = action.Credential.CredentialId;
        bool Account(IReadOnlyList<Guid>? allowed) => allowed is null || allowed.Contains(id);
        if (!Account(uses) || !Account(accounts?.Credentials) || accounts?.Sessions is { } sessions && !sessions.Contains(request.SessionId)
            || action.Purpose == AgentCore.Domain.Credentials.CredentialPurpose.GoogleDriveAccount && !Account(accounts?.DriveCredentials)) return false;
        if (!Account(action.Permission switch { "email.draft" => accounts?.EmailDraftCredentials, "email.send" => accounts?.EmailSendCredentials,
            "drive.write" => accounts?.DriveWriteCredentials, "drive.delete" => accounts?.DriveDeleteCredentials, _ => null })) return false;
        var args = TenantAccountActionRules.Arguments(action);
        if (args.Path is not { } path) return true;
        if (path.StartsWith("/home/", StringComparison.Ordinal) && workspace?.HomeAgents is { } agents && !agents.Contains(request.Operation.InstanceId)) return false;
        var resource = (path.StartsWith("/working/", StringComparison.Ordinal) ? "working:" + request.SessionId.ToString("D") : "home:" + request.Operation.InstanceId.ToString("D"));
        var scopes = args.Upload ? workspace?.Reads : workspace?.Writes;
        return scopes is null || scopes.Contains(resource, StringComparer.Ordinal);
    }
}
