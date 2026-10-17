using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Model-visible result data, never an authorization token or another operation's arguments.
public sealed record TenantApprovalOutcome(Guid TenantId, Guid RunId, Guid RequestId, Guid OperationId,
    Guid InstanceId, Guid RootActivationId, TenantApprovalStatus ApprovalStatus, TenantOperationOutcome Outcome,
    string ReasonCode, long? InstanceRevision, long? PersonaRevision);

public static class TenantRunContinuationRules
{
    public static void Require(AgentRun run, TenantApprovalRequest? request)
    {
        var context = run.Admission.ExecutionContext;
        if (request is null || context is null || !Enum.IsDefined(request.Status) || request.Status == TenantApprovalStatus.Pending
            || request.RequestId != run.ApprovalRequestId || request.RunId != run.AgentRunId || request.SessionId != run.SessionId
            || request.TenantId != context.TenantId || request.ExecutorPrincipalId != context.AgentPrincipalId
            || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId || request.RootActivationId != context.RootActivationId
            || request.Operation.InstanceId != run.AgentInstanceId)
            throw new SecurityConflictException("approval_continuation_unavailable");
    }
}

public sealed class TenantRunContinuationService(ITenantRunStore runs, ITenantApprovalStore approvals,
    TenantApprovedOperationService effects, TimeProvider time)
{
    public async ValueTask<(AgentRun Run, TenantApprovalOutcome? Outcome)> ResumeAsync(AgentRun dispatched, CancellationToken ct = default)
    {
        var tenant = dispatched.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var run = await Current(dispatched, ct).ConfigureAwait(false);
        if (run.ApprovalRequestId is not { } requestId) return (run, null);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request);
        TenantOperationReceipt? receipt = null;
        if (request!.Status == TenantApprovalStatus.Approved)
            receipt = await effects.ExecutePersonaAsync(new(tenant, run.AgentRunId), requestId, request.Operation.OperationId,
                run.Claim!.Generation, request.Operation.ActionHash, ct).ConfigureAwait(false);
        run = await Current(dispatched, ct).ConfigureAwait(false);
        var reason = receipt?.ReasonCode ?? request.Status switch
        {
            TenantApprovalStatus.Rejected => "approval_rejected",
            TenantApprovalStatus.Expired => "approval_expired",
            TenantApprovalStatus.Cancelled => "approval_cancelled",
            _ => throw new SecurityConflictException("approval_continuation_unavailable")
        };
        return (run, new(tenant, run.AgentRunId, requestId, request.Operation.OperationId, request.Operation.InstanceId,
            request.RootActivationId, request.Status, receipt?.Outcome ?? TenantOperationOutcome.Denied,
            reason, receipt?.InstanceRevision, receipt?.PersonaRevision));
    }

    private async ValueTask<AgentRun> Current(AgentRun dispatched, CancellationToken ct)
    {
        var run = await runs.FindTenantRunAsync(dispatched.Owner.TenantId!.Value, dispatched.AgentRunId, ct).ConfigureAwait(false);
        if (run is null || run.Owner != dispatched.Owner || run.SessionId != dispatched.SessionId
            || run.Status != AgentRunStatus.Running || run.Claim?.Generation != dispatched.Claim?.Generation
            || run.CancellationRequested || run.Claim!.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
        return run;
    }
}
