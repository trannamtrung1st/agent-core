using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantAttachmentReadContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantApprovalOutcome outcome, string? inspection, string? unavailable)
    {
        var data = inspection is null ? null : JsonNode.Parse(inspection);
        if (data is not null && data["tenantAttachmentRead"]?.GetValue<bool>() != true) { unavailable = "output_limit"; data = null; }
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(data, data is null ? unavailable ?? "read_not_authorized" : null)); }
        catch (ArgumentException) when (data is not null)
        { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
        string Serialize(JsonNode? value, string? missing) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
            outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()), reasonCode = outcome.ReasonCode,
            requestId = request.RequestId, operationId = outcome.OperationId, applied = false,
            readAuthorized = outcome.Outcome == TenantOperationOutcome.Completed, attachmentObservation = value,
            observationUnavailable = value is null, observationUnavailableReason = missing, replayAllowed = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
