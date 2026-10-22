using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static class TenantAutomationMutationContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request, TenantApprovalOutcome outcome) =>
        AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = request.Operation.OperationId,
            applied = outcome.Outcome == TenantOperationOutcome.Completed, automationId = request.Operation.AutomationMutation!.Proposed.AutomationId,
            revision = outcome.Outcome == TenantOperationOutcome.Completed ? request.Operation.AutomationMutation!.Proposed.Revision : (long?)null, replayAllowed = false }));
}
