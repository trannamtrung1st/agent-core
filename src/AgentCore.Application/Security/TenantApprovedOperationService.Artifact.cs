using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteArtifactAsync(TenantRunReference reference, TenantApprovalRequest request, AgentRun run,
        AgentRun? source, TenantSecurityState state, TenantAgent agent, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision,
            request.Revision, decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        TenantArtifactReviewRules.ValidateEffect(request, decision, grant, run, commit, source);
        if (run.Claim!.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        string? reason = grant is not null && grant.ExpiresAt <= time.GetUtcNow() ? "grant_expired" : request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                var sessions = security as ITenantSessionStore ?? throw new AccessDeniedException("tenant_effect_unavailable");
                var session = await sessions.FindTenantSessionAsync(reference.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var current = await configuration.ResolveAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
                _ = TenantArtifactReviewRules.Require(request, run, current.Agent, current.Configuration.Configuration.Definition, session, source);
                var iam = new IamAuthorizer(security);
                await TenantArtifactReviewRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
                try { await TenantArtifactReviewRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false); }
                catch (AccessDeniedException) { throw new AccessDeniedException("grantor_authority_lost"); }
                if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                    reason = "operation_witness_changed";
            }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            catch (SecurityConflictException e) { reason = e.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var receipt = await effects.TryCommitLocalEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantArtifactOperationRules.AuditAction, request.ResourceId,
                reason is null ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (receipt is not null) return receipt;
        var previous = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return previous is not null ? Existing(previous, request, run) : throw new SecurityConflictException("stale_revision");
    }
}
