using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAccountMaintenanceRules
{
    public static void Transaction(TenantSecurityState next, long expected, TenantAccountDispatch fence, TenantApprovalRequest request,
        CredentialOAuthCheck value, AuthorizationAudit audit)
    {
        if (request.Operation.AccountAction is null || request.RequestId != fence.RequestId || request.Operation.OperationId != fence.OperationId
            || request.Operation.ActionHash != fence.ActionHash || request.TenantId != fence.TenantId || next.Tenant.TenantId != request.TenantId
            || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId
            || audit.Target != request.ResourceId || audit.Decision != "allowed" || audit.Action != "operations.accounts.oauth.maintain"
            || audit.ReasonCode != (value.ReconnectRequired ? "oauth_reconnect_required" : "oauth_grant_rotated")
            || value.RotatedProtectedPayload is null && !value.ReconnectRequired)
            throw new ArgumentException("Invalid exact account effect maintenance.");
    }
}
