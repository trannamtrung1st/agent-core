using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAccountReadContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantApprovalOutcome outcome, string? inspection, string? unavailable)
    {
        var data = inspection is null ? null : JsonNode.Parse(inspection);
        if (data is not null && data["tenantAccountRead"]?.GetValue<bool>() != true) { data = null; unavailable = "account_result_invalid"; }
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(data)); }
        catch (ArgumentException) when (data is not null)
        { unavailable = "result_budget_exhausted"; return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null)); }
        string Serialize(JsonNode? value) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = outcome.OperationId, applied = false,
            readAuthorized = outcome.Outcome == TenantOperationOutcome.Completed, accountRead = value, accountReadUnavailable = value is null,
            accountReadUnavailableReason = value is null ? unavailable ?? "read_not_authorized" : null, replayAllowed = false
        });
    }
}
