using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteMetadataReadAsync(TenantRunReference reference, TenantApprovalRequest request,
        AgentRun run, AgentRun? source, TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new AgentCore.Application.Ports.TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation,
            run.Revision, request.Revision, decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantCredentialReadOperationRules.Reason);
        TenantCredentialReadOperationRules.ValidateEffect(request, decision, grant, run, commit, source);
        if (run.Claim!.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                var reads = credentialReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, decision.HumanPrincipalId, ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            catch (SecurityConflictException e) { reason = e.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var receipt = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantCredentialReadOperationRules.AuditAction, request.ResourceId,
                reason is null ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (receipt is not null) return receipt;
        var previous = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return previous is not null ? Existing(previous, request, run) : throw new SecurityConflictException("stale_revision");
    }
}
