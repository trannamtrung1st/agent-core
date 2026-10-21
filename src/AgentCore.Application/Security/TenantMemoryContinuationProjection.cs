using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantMemoryContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantApprovalOutcome outcome, string? inspection, string? unavailable)
    {
        var value = request.Operation.MemoryOperation!; var read = TenantMemoryToolContract.ReadOnly(value.ToolName);
        var data = inspection is null ? null : JsonNode.Parse(inspection);
        if (data is not null && data["tenantMemoryRead"]?.GetValue<bool>() != true) { unavailable = "output_limit"; data = null; }
        string Serialize(JsonNode? body, string? missing) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = outcome.OperationId,
            applied = !read && outcome.Outcome == TenantOperationOutcome.Completed, readAuthorized = read && outcome.Outcome == TenantOperationOutcome.Completed,
            memoryId = value.ResultMemoryId ?? (read ? (Guid?)null : TenantMemoryOperationRules.Sources(value).Single().MemoryId),
            memoryObservation = body, observationUnavailable = read && body is null, observationUnavailableReason = missing, replayAllowed = false
        });
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(data, data is null && read ? unavailable ?? "read_not_authorized" : null)); }
        catch (ArgumentException) when (data is not null) { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
    }
}
