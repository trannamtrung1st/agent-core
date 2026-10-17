using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    internal async ValueTask<string?> LegacyApprovalPreparationErrorAsync(Guid sessionId, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (admission?.TenantId is not null)
            return """{"error":"forbidden","reasonCode":"tenant_effect_unavailable"}""";
        return tenantAuthorization is null ? null
            : await tenantAuthorization.PreparationErrorAsync(sessionId, admission, ct).ConfigureAwait(false);
    }
    internal ValueTask<TenantApprovalPreparationResult> RequestTenantPersonaApprovalAsync(Guid sessionId, ModelToolCall call,
        ToolExecutionAdmission admission, CancellationToken ct) => tenantAuthorization is not null
        ? tenantAuthorization.PreparePersonaAsync(sessionId, call, admission, ct)
        : throw new AccessDeniedException("tenant_context_required");
}
