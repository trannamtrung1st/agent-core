using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static class TenantAutomationReadContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request, TenantApprovalOutcome outcome,
        string? inspection, string? unavailable)
    {
        var body = inspection is null ? null : JsonNode.Parse(inspection);
        if (body is not null && body["tenantAutomationRead"]?.GetValue<bool>() != true) { unavailable = "output_limit"; body = null; }
        string Serialize(JsonNode? observation, string? reason) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = request.Operation.OperationId, applied = false,
            readAuthorized = outcome.Outcome == TenantOperationOutcome.Completed, automationReadObservation = observation, observationUnavailable = observation is null,
            observationUnavailableReason = reason, replayAllowed = false });
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(body, body is null ? unavailable ?? "read_not_authorized" : null)); }
        catch (ArgumentException) when (body is not null) { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
    }
}
