using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteReadAsync(TenantRunReference reference, TenantApprovalRequest request,
        AgentRun run, AgentRun? source, TenantSecurityState state, TenantAgent agent, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantSandboxReadOperationRules.Reason);
        TenantSandboxReadEffectRules.Validate(request, decision, grant, run, commit, source);
        var at = time.GetUtcNow();
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        string? reason = request.ExpiresAt <= at ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                var reads = sandboxReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, decision.HumanPrincipalId, ct).ConfigureAwait(false);
                var eligibility = await operations.Check(state, run, request.Permission, AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId), ct,
                    managementReviewRequired: true).ConfigureAwait(false);
                if (eligibility.Decision == OperationDecisionKind.Denied || eligibility.ApprovalKind != ApprovalKind.Review) throw new AccessDeniedException("operation_authority_changed");
            }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            catch (SecurityConflictException e) { reason = e.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantSandboxReadOperationRules.AuditAction,
            request.ResourceId, reason is null ? "allowed" : "denied", commit.ReasonCode, at);
        var result = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false);
        if (result is not null) return result;
        await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        var previous = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return previous is not null ? Existing(previous, request, run) : throw new SecurityConflictException("stale_revision");
    }
}

public static class TenantSandboxReadEffectRules
{
    public static void Validate(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.SandboxRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != TenantSandboxReadOperationRules.Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
}
