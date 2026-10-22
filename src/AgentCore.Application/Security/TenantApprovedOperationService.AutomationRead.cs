using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteAutomationReadAsync(TenantRunReference reference, TenantApprovalRequest request, AgentRun run, AgentRun? source,
        TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision,
            decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, TenantAutomationReadRules.Completed);
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        string? reason = request.ExpiresAt <= time.GetUtcNow() ? "approval_expired" : null;
        if (reason is null)
        {
            try {
                var service = (automationTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).Reads; var value = request.Operation.AutomationRead!;
                var observed = await service.PrepareAsync(reference, generation, value.ToolName, value.ArgumentsJson, ct, value).ConfigureAwait(false);
                await TenantAutomationReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(security), ct).ConfigureAwait(false);
            } catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) { reason = error.ReasonCode; }
        }
        if (reason is not null) commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
        var receipt = await effects.TryCommitLocalEffectAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, request.ExecutorPrincipalId, "operations.automation.read", request.ResourceId, reason is null ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (receipt is not null) return receipt;
        var prior = await effects.FindOperationReceiptAsync(reference.TenantId, commit.OperationId, ct).ConfigureAwait(false);
        return prior is not null ? Existing(prior, request, run) : throw new SecurityConflictException("stale_revision");
    }
}
