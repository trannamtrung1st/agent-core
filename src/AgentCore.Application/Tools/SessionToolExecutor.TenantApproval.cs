using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    internal ValueTask<TenantApprovalPreparationResult> RequestTenantAuthorizationAsync(Guid sessionId, ModelToolCall call,
        ToolExecutionAdmission admission, CancellationToken ct) => tenantAuthorization is not null
        ? tenantAuthorization.PrepareAsync(sessionId, call, admission, ct)
        : throw new AccessDeniedException("tenant_context_required");
}
