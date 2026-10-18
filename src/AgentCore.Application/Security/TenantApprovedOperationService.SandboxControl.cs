using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteControlAsync(TenantRunReference reference, TenantApprovalRequest request,
        AgentRun run, AgentRun? source, TenantSecurityState state, TenantAgent agent, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantSandboxControlOperationRules.Reason(request.Operation.SandboxControl!));
        TenantSandboxControlEffectRules.Validate(request, decision, grant, run, commit, source);
        var at = time.GetUtcNow();
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        string? reason = request.ExpiresAt <= at ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
                    throw new SecurityConflictException("operation_witness_changed");
                var target = controlTargets ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await target.RequireAsync(request, run, ct).ConfigureAwait(false);
                await target.RequireReviewerAsync(request, state, decision.HumanPrincipalId, ct).ConfigureAwait(false);
                var scope = AccessScope.ForResource(reference.TenantId, request.ResourceType, request.ResourceId);
                if (!await operations.QualifiesAsync(state, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId,
                    PermissionCatalog.Get(request.Permission), scope, ApprovalKind.Review, ct).ConfigureAwait(false)) throw new AccessDeniedException("grantor_authority_lost");
                var eligibility = await operations.Check(state, run, request.Permission, scope, ct, managementReviewRequired: true).ConfigureAwait(false);
                if (eligibility.Decision == OperationDecisionKind.Denied || eligibility.ApprovalKind != ApprovalKind.Review)
                    throw new AccessDeniedException("operation_authority_changed");
            }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            catch (SecurityConflictException e) { reason = e.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, TenantSandboxControlOperationRules.AuditAction,
            request.ResourceId, reason is null ? "allowed" : "denied", commit.ReasonCode, at);
        var result = await effects.TryCommitSandboxControlAsync(next, state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false);
        if (result is not null) return result;
        await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        var existing = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return existing is not null ? Existing(existing, request, run) : throw new SecurityConflictException("stale_revision");
    }
}

public static class TenantSandboxControlEffectRules
{
    public static void Validate(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.SandboxControl is not { } control || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != TenantSandboxControlOperationRules.Reason(control)
            || commit.ReasonCode.Length > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void Audit(TenantSecurityState next, long expected, TenantApprovalRequest request, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != request.TenantId
            || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != TenantSandboxControlOperationRules.AuditAction || audit.Target != request.ResourceId)
            throw new ArgumentException("Invalid Sandbox control transaction.");
    }
}
