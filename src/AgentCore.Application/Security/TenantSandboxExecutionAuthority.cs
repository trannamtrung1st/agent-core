using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxExecutionAuthorityView(AgentRun Run, TenantApprovalRequest Request, SandboxLease Lease, SandboxExecution Execution);

// Revalidate the actual reserved operation before and throughout SDK execution. No caller supplies its command.
public sealed class TenantSandboxExecutionAuthority(TenantOperationAuthorizer operations, ITenantSandboxRuntimeStore runtime,
    ITenantApprovalStore approvals, ITenantApprovedOperationStore decisions, ITenantRunStore runs,
    TenantSandboxOperationTarget targets, IamAuthorizer authorizer, TimeProvider time)
{
    public async ValueTask<TenantSandboxExecutionAuthorityView> RequireAsync(TenantRuntimeFrame frame, Guid executionId, CancellationToken ct = default)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var saved = await runtime.ReadTenantSandboxRuntimeAsync(tenant, frame.SessionId, executionId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, saved.Owner, run, time.GetUtcNow());
        var execution = saved.Execution ?? throw new AccessDeniedException("resource_unavailable");
        if (execution.ProcessStop is not null) throw new AccessDeniedException("sandbox_stop_requested");
        var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
        var request = await approvals.FindApprovalAsync(tenant, execution.ApprovalRequestId ?? Guid.Empty, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("sandbox_approval_mismatch");
        var decision = await decisions.FindApprovalDecisionAsync(tenant, request.RequestId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("approval_unavailable");
        var source = run.AgentRunId == request.RunId ? null : await runs.FindTenantRunAsync(tenant, request.RunId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request, source);
        if (execution.ClaimGeneration != frame.Generation || execution.AgentRunId != frame.RunId || execution.Id != executionId
            || execution.OperationId != request.Operation.OperationId || execution.ActionHash != request.Operation.ActionHash
            || request.Status != TenantApprovalStatus.Approved || request.Kind != ApprovalKind.Review || request.ExpiresAt <= time.GetUtcNow()
            || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != execution.ActionHash || decision.RequestRevision != request.Revision
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight || run.SideEffect.ToolCallId != execution.ToolCallId
            || run.SideEffect.ActionHash != execution.ActionHash || execution.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D"))
            throw new SecurityConflictException("sandbox_approval_mismatch");
        await targets.RequireAsync(request, run, ct, execution).ConfigureAwait(false);
        await TenantSandboxApprovalRules.RequireHumanAsync(state, request, decision, authorizer, ct).ConfigureAwait(false);
        var (after, latest) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        TenantRuntimeRules.Require(frame, saved.Owner, after, time.GetUtcNow());
        if (latest.Tenant.Revision != state.Tenant.Revision || after.Revision != run.Revision)
            throw new SecurityConflictException("stale_revision");
        return new(run, request, lease, execution);
    }
}
