using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using System.Text.Json;
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
    TenantApprovedOperationService effects, TimeProvider time, ITenantRuntimeStore? runtimeStore = null, IIdGenerator? ids = null)
{
    public async ValueTask<(AgentRun Run, TenantApprovalOutcome? Outcome)> ResumeAsync(AgentRun dispatched, CancellationToken ct = default)
    {
        var tenant = dispatched.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var run = await Current(dispatched, ct).ConfigureAwait(false);
        if (run.ApprovalRequestId is not { } requestId) return (run, null);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
        TenantRunContinuationRules.Require(run, request);
        ValidateAuthorizationCheckpoint(run, request!);
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
        var outcome = new TenantApprovalOutcome(tenant, run.AgentRunId, requestId, request.Operation.OperationId, request.Operation.InstanceId,
            request.RootActivationId, request.Status, receipt?.Outcome ?? TenantOperationOutcome.Denied,
            reason, receipt?.InstanceRevision, receipt?.PersonaRevision);
        if (run.Checkpoint is { } checkpoint && AgentRunToolCallCheckpoint.TryRead(checkpoint, out var transcript))
        {
            var pending = AgentRunToolCallCheckpoint.PendingCalls(transcript!);
            if (pending.Any(c => c.Name == ToolCatalog.RequestAuthorization))
            {
                ValidateAuthorizationCheckpoint(run, request);
                if (runtimeStore is null || ids is null)
                    throw new SecurityConflictException("approval_continuation_mismatch");
                var text = JsonSerializer.Serialize(new { approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
                    reasonCode = reason, operationId = outcome.OperationId, applied = outcome.Outcome == TenantOperationOutcome.Completed });
                var resumed = AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, pending[0], text);
                run = await runtimeStore.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.Checkpoint(run.Revision, time.GetUtcNow(), run.Claim!.Generation, resumed, null), ids.NewId(), ct, run.Claim.Generation).ConfigureAwait(false);
            }
        }
        return (run, outcome);
    }

    private static void ValidateAuthorizationCheckpoint(AgentRun run, TenantApprovalRequest request)
    {
        if (run.Checkpoint is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var transcript)) return;
        var pending = AgentRunToolCallCheckpoint.PendingCalls(transcript!);
        if (!pending.Any(c => c.Name == ToolCatalog.RequestAuthorization)) return;
        try
        {
            if (pending.Count != 1 || TenantApprovalToolContract.Parse(pending[0]) != request.Operation.Persona)
                throw new ArgumentException("Pending authorization differs from the saved operation.");
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { throw new SecurityConflictException("approval_continuation_mismatch"); }
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
