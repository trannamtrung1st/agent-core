using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteTaskDelegationAsync(TenantRunReference reference, TenantApprovalRequest request,
        AgentRun run, AgentRun? source, TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Denied, "operation_authority_changed");
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        try
        {
            var current = await (taskTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, run, state, ct);
            await TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new(security), ct);
            commit = commit with { Outcome = TenantOperationOutcome.Completed, ReasonCode = "task_created",
                TaskDelegationAdmission = TenantTaskDelegationTarget.Build(request, current, state.Tenant.Revision, ids.NewId()) };
        }
        catch (AccessDeniedException error) { commit = commit with { ReasonCode = error.ReasonCode }; }
        catch (SecurityConflictException error) { commit = commit with { ReasonCode = error.ReasonCode }; }
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var receipt = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantTaskConsentRules.AuditAction, request.ResourceId,
                commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct);
        return receipt ?? await effects.FindOperationReceiptAsync(reference.TenantId, request.Operation.OperationId, ct) ?? throw new SecurityConflictException("stale_revision");
    }
}
