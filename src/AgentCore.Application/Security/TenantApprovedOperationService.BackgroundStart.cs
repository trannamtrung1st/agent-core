using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovedOperationService
{
    private async ValueTask<TenantOperationReceipt> ExecuteBackgroundStartAsync(TenantRunReference reference, TenantApprovalRequest request, AgentRun run,
        AgentRun? source, TenantSecurityState state, Guid generation, CancellationToken ct)
    {
        var decision = await effects.FindApprovalDecisionAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(reference.TenantId, request.RequestId, ct).ConfigureAwait(false);
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, run.AgentRunId, generation, run.Revision, request.Revision, decision.DecisionId, grant?.Revision,
            request.Operation.ActionHash, TenantOperationOutcome.Denied, "operation_authority_changed");
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        string? refusal = null;
        try
        {
            var current = await (backgroundCreations ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, run, state, ct).ConfigureAwait(false);
            await TenantBackgroundCreationRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(security), ct).ConfigureAwait(false);
            commit = commit with { Outcome = TenantOperationOutcome.Completed, ReasonCode = "background_created",
                BackgroundAdmission = TenantBackgroundCreationTarget.Build(request, run, current.Session, current.Configuration, time.GetUtcNow()) };
        }
        catch (AccessDeniedException e) { refusal = e.ReasonCode; }
        catch (SecurityConflictException e) { refusal = e.ReasonCode; }
        if (refusal is not null) commit = commit with { ReasonCode = refusal };
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var receipt = await effects.TryCommitLocalEffectAsync(next, state.Tenant.Revision, commit,
            new(ids.NewId(), reference.TenantId, run.Owner.ExecutorPrincipalId!.Value, TenantBackgroundCreationRules.AuditAction, request.ResourceId,
                commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", commit.ReasonCode, time.GetUtcNow()), ct).ConfigureAwait(false);
        return receipt ?? await effects.FindOperationReceiptAsync(reference.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
}
