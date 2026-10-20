using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantRuntimeAccountReadRules
{
    public static void Transaction(TenantSecurityState next, long expected, TenantRuntimeAccountReadProof proof, TenantApprovalRequest request,
        CredentialOAuthCheck? maintenance, AuthorizationAudit audit)
    {
        if (request.Operation.AccountRead is null || request.RequestId != proof.RequestId || request.Operation.OperationId != proof.OperationId
            || proof.Frame.Owner.TenantId != request.TenantId || next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(expected + 1)
            || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Target != request.ResourceId || audit.Decision != "allowed"
            || audit.Action != (maintenance is null ? "operations.accounts.read" : "operations.accounts.oauth.maintain")
            || audit.ReasonCode != (maintenance is null ? "connected_account_read" : maintenance.ReconnectRequired ? "oauth_reconnect_required" : "oauth_grant_rotated")
            || maintenance is not null && maintenance.RotatedProtectedPayload is null && !maintenance.ReconnectRequired)
            throw new ArgumentException("Invalid exact runtime account-read transaction.");
    }
}
