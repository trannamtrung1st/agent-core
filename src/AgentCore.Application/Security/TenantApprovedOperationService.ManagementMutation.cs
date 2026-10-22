using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteManagementMutationAsync(TenantRunReference reference, TenantApprovalRequest request, AgentRun run, AgentRun? source,
        TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantManagementMutationRules.Completed);
        TenantManagementMutationRules.ValidateEffect(request, decision, grant, run, commit, source);
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                var value = request.Operation.ManagementMutation!;
                var observed = await (managementTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(reference, generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
                var eligibility = await managementTargets.DecisionAsync(observed, ct).ConfigureAwait(false);
                if (eligibility.Decision != OperationDecisionKind.RequiresApproval || eligibility.ApprovalKind != request.Kind) throw new AccessDeniedException("operation_authority_changed");
                await TenantManagementOperationTarget.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(security), ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) { reason = error.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var receipt = await effects.TryCommitLocalEffectAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, "operations.management.apply", request.ResourceId, reason is null ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (receipt is not null) return receipt;
        var prior = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return prior is not null ? Existing(prior, request, run) : throw new SecurityConflictException("stale_revision");
    }
}
