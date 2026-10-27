using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Read-only proof over the saved effect lineage. It grants no claim or dispatch authority.
internal static class TenantSandboxRetainedSources
{
    internal sealed record Source(AgentRun Run, TenantSecurityState State, TenantApprovalRequest Request,
        TenantOperationReceipt Receipt, TenantApprovalDecision Decision);
    internal static async ValueTask<Source> ReadAsync(AgentRun final, Guid operationId, TenantOperationAuthorizer operations,
        ITenantApprovalStore approvals, ITenantApprovedOperationStore effects, CancellationToken ct)
    {
        var tenant = final.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (stored, state) = await operations.ResolveAsync(new(tenant, final.AgentRunId), ct, retained: true);
        if (stored.Owner != final.Owner || stored.SessionId != final.SessionId || stored.Revision != final.Revision)
            throw new SecurityConflictException("sandbox_source_changed");
        var receipt = await effects.FindOperationReceiptAsync(tenant, operationId, ct) ?? throw new AccessDeniedException("sandbox_source_unavailable");
        var request = await approvals.FindApprovalAsync(tenant, receipt.RequestId ?? throw new AccessDeniedException("sandbox_source_unavailable"), ct) ?? throw new AccessDeniedException("approval_unavailable");
        var decision = await effects.FindApprovalDecisionAsync(tenant, request.RequestId, ct) ?? throw new AccessDeniedException("approval_unavailable");
        if (receipt.Outcome != TenantOperationOutcome.Completed || receipt.TenantId != tenant || receipt.SessionId != final.SessionId
            || receipt.InstanceId != final.AgentInstanceId || receipt.ExecutorPrincipalId != final.Owner.ExecutorPrincipalId
            || request.TenantId != tenant || request.SessionId != final.SessionId || request.ExecutorPrincipalId != final.Owner.ExecutorPrincipalId
            || request.Operation.InstanceId != final.AgentInstanceId || request.RootActivationId != receipt.RootActivationId
            || request.OriginalInitiatorPrincipalId != final.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
            || decision.RequestId != request.RequestId || decision.TenantId != tenant
            || request.Status != TenantApprovalStatus.Approved || decision.Status != TenantApprovalStatus.Approved
            || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash
            || receipt.ActionHash != request.Operation.ActionHash || receipt.OperationId != request.Operation.OperationId
            || receipt.RootActivationId != final.Admission.ExecutionContext?.RootActivationId)
            throw new AccessDeniedException("sandbox_source_unavailable");
        var current = final; var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64 && seen.Add(current.AgentRunId); depth++)
        {
            if (current.AgentRunId == receipt.RunId) return new(current, state, request, receipt, decision);
            if (current.Admission.ApprovalResume?.SourceRunId is not { } previousId
                || await operations.FindRunAsync(new(tenant, previousId), ct) is not { } previous
                || previous.Owner != final.Owner || previous.SessionId != final.SessionId
                || previous.Admission.DelegatedTask != final.Admission.DelegatedTask
                || previous.Result?.ContinuationRunId != current.AgentRunId) break;
            current = previous;
        }
        throw new AccessDeniedException("sandbox_source_unavailable");
    }
    internal static async ValueTask FenceAsync(AgentRun final, long revision, TenantOperationAuthorizer operations, CancellationToken ct)
    {
        var (run, state) = await operations.ResolveAsync(new(final.Owner.TenantId!.Value, final.AgentRunId), ct, retained: true);
        if (run.Owner != final.Owner || run.SessionId != final.SessionId || run.Revision != final.Revision || state.Tenant.Revision != revision)
            throw new SecurityConflictException("sandbox_source_changed");
    }
}
