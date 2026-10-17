using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantOperationAuditRules
{
    public const string Action = "authorization.operation";
    public static void Validate(AuthorizationAudit audit)
    {
        var e = audit.Execution ?? throw new ArgumentException("Execution audit context is required.");
        if (audit.AuditId == Guid.Empty || audit.TenantId != e.Context.TenantId || audit.ActorPrincipalId != e.Context.AgentPrincipalId
            || audit.Action != Action || audit.Target != (e.Resource.ResourceId ?? e.Context.TenantId.ToString("D"))
            || audit.Decision is not ("allowed" or "denied" or "requiresApproval") || audit.At.Offset != TimeSpan.Zero
            || audit.ReasonCode.Length is < 1 or > 64 || audit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
            || e.Purpose == ExecutionAuthorizationPurpose.EffectResult || e.ApprovalRequestId is not null || e.OperationId is not null || e.GrantId is not null
            || e.RunRevision < 1 || e.SecurityRevision < 1 || e.ClaimGeneration == Guid.Empty || !Enum.IsDefined(e.Purpose)
            || e.Resource.TenantId != audit.TenantId || e.Resource.Kind == AccessScopeKind.System
            || e.Permission is { } permission && !PermissionCatalog.TryGet(permission, out _)
            || e.ApprovalKind is { } kind && !Enum.IsDefined(kind)
            || (audit.Decision == "requiresApproval") != (e.ApprovalKind is not null))
            throw new ArgumentException("Invalid execution authorization audit.");
        if (IamResourceScopes.Create(e.Context.TenantId, e.Resource.ResourceType, e.Resource.ResourceId) != e.Resource)
            throw new ArgumentException("Audit resource must be a normalized tenant scope.");
    }

    public static AuthorizationAudit WithLocalEffectResult(AuthorizationAudit audit, TenantSecurityState state, AgentRun run,
        Guid operationId, Guid? requestId = null, Guid? grantId = null) => audit with
    {
        Execution = new(run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required"), run.Revision,
            state.Tenant.Revision, run.Claim?.Generation, "agents.update",
            AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")),
            null, ExecutionAuthorizationPurpose.EffectResult, requestId, operationId, grantId)
    };

    public static bool Matches(AuthorizationAudit audit, AgentRun run, long securityRevision) =>
        audit.Execution is { } e && e.Context == run.Admission.ExecutionContext && e.RunRevision == run.Revision
        && e.ClaimGeneration == run.Claim?.Generation && e.SecurityRevision == securityRevision;
}
