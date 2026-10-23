using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceMutationContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantOperationOutcome outcome, string reason, TenantPublicDownloadReceipt? download = null)
    {
        var output = JsonSerializer.SerializeToNode(new {
            tenantWorkspaceMutation = true, approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
            outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()), reasonCode = reason, requestId = request.RequestId, operationId = request.Operation.OperationId,
            applied = outcome == TenantOperationOutcome.Completed ? (bool?)true : outcome == TenantOperationOutcome.Denied ? false : null,
            partialEffectsPossible = outcome == TenantOperationOutcome.Indeterminate, replayAllowed = false, readAuthorityGranted = false
        })!;
        if (download is not null) output["download"] = JsonSerializer.SerializeToNode(TenantPublicDownloadRules.Project(download));
        return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, output.ToJsonString());
    }
    public static AgentRun Save(AgentRun before, AgentRun saved, TenantApprovalRequest request, TenantOperationOutcome outcome, string reason, DateTimeOffset at, TenantPublicDownloadReceipt? download = null)
    {
        if (saved.Status != AgentRunStatus.Running || saved.CancellationRequested || saved.Claim is null || saved.Claim.LeaseExpiresAtUtc <= at
            || before.Claim?.Generation != before.SideEffect.ExternalEffect?.Generation || saved.Checkpoint is not { } checkpoint
            || !AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages)) return saved;
        var pending = AgentRunToolCallCheckpoint.PendingCalls(messages!); if (pending.Count == 0) return saved;
        try
        {
            TenantRunContinuationService.ValidateAuthorizationCheckpoint(saved, request, requirePending: true);
            var completed = Complete(checkpoint.PauseBudgetClock(at), pending[0], request, outcome, reason, download);
            var checkpointed = saved.SaveCheckpoint(saved.Revision, saved.Claim.Generation, completed, null, at);
            return checkpointed.AwaitApprovedExternalResultDelivery(checkpointed.Revision, saved.Claim.Generation, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, at);
        }
        catch (Exception e) when (e is ArgumentException or SecurityConflictException or AgentRunTransitionException)
        { return saved; } // Factual settlement survives unusable delivery checkpoints/headroom.
    }
}
