using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static class TenantManagementMutationContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request, TenantApprovalOutcome outcome, TenantOperationReceipt? receipt)
    {
        var value = request.Operation.ManagementMutation!;
        return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = request.Operation.OperationId, applied = receipt?.Outcome == TenantOperationOutcome.Completed,
            instanceRevision = receipt?.InstanceRevision, tool = value.ToolName, changesApplyTo = value.ToolName == "management.freeze" ? "immediateRevocation" : "nextRun",
            revocationsApplyTo = value.ToolName is "management.freeze" or "management.policy.update" ? "immediate" : null,
            replayAllowed = false, currentRunConfigurationUnchanged = true }));
    }
}
