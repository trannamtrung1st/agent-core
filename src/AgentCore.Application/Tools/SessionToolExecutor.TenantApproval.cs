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
}
