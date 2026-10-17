using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantOperationAudit(ITenantAuthorizationAuditStore store, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask RecordAsync(TenantSecurityState state, AgentRun run, OperationAuthorizationDecision decision,
        ExecutionAuthorizationPurpose purpose, CancellationToken ct)
    {
        var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        var execution = new ExecutionAuthorizationAudit(context, run.Revision, state.Tenant.Revision, run.Claim?.Generation,
            PermissionCatalog.TryGet(decision.Permission, out _) ? decision.Permission : null, decision.Resource, decision.ApprovalKind, purpose);
        var audit = new AuthorizationAudit(ids.NewId(), context.TenantId, context.AgentPrincipalId, TenantOperationAuditRules.Action,
            decision.Resource.ResourceId ?? context.TenantId.ToString("D"), decision.Decision switch
            { OperationDecisionKind.Allowed => "allowed", OperationDecisionKind.RequiresApproval => "requiresApproval", _ => "denied" }, decision.ReasonCode,
            DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()), execution);
        if (!await store.TryRecordOperationAuthorizationAsync(audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
}
