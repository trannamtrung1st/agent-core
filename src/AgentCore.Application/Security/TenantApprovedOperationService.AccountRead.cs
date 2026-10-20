using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteAccountReadAsync(TenantRunReference reference, TenantApprovalRequest request, AgentRun run,
        AgentRun? source, TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantAccountReadRules.Reason);
        TenantAccountReadRules.ValidateEffect(request, decision, grant, run, commit, source);
        if (run.Claim!.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : grant?.ExpiresAt <= time.GetUtcNow() ? "grant_expired" : null;
        if (reason is null)
        {
            try
            {
                await (accountReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await TenantAccountReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(security), ct).ConfigureAwait(false);
                var eligibility = await operations.Check(state, run, "credentials.use", TenantCredentialRules.Scope(request.TenantId, request.Operation.AccountRead!.Credential.CredentialId),
                    ct, managementReviewRequired: true).ConfigureAwait(false);
                if (eligibility.Decision == OperationDecisionKind.Denied || request.Kind == ApprovalKind.Review && eligibility.ApprovalKind == ApprovalKind.ExceptionalGrant)
                    throw new AccessDeniedException("operation_authority_changed");
            }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            catch (SecurityConflictException e) { reason = e.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var receipt = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantAccountReadRules.AuditAction, request.ResourceId,
                reason is null ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        return receipt ?? (await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false) is { } prior
            ? Existing(prior, request, run) : throw new SecurityConflictException("stale_revision"));
    }
}
