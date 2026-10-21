using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public static class TenantExperienceContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request, TenantApprovalOutcome outcome,
        string? inspection, string? unavailable)
    {
        var read = request.Operation.ExperienceOperation!.ToolName == "experience.source";
        var body = inspection is null ? null : JsonNode.Parse(inspection);
        if (body is not null && body["tenantExperienceSourceRead"]?.GetValue<bool>() != true) { unavailable = "output_limit"; body = null; }
        string Serialize(JsonNode? observation, string? reason) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()), outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()),
            reasonCode = outcome.ReasonCode, requestId = request.RequestId, operationId = request.Operation.OperationId, applied = request.Operation.ExperienceOperation!.ToolName != "experience.source" && outcome.Outcome == TenantOperationOutcome.Completed,
            readAuthorized = request.Operation.ExperienceOperation!.ToolName == "experience.source" && outcome.Outcome == TenantOperationOutcome.Completed, experienceId = request.Operation.ExperienceOperation!.ResultExperienceId, experienceSourceObservation = observation, observationUnavailable = read && observation is null,
            observationUnavailableReason = read ? reason : null, replayAllowed = false });
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(body, body is null ? unavailable ?? "read_not_authorized" : null)); }
        catch (ArgumentException) when (body is not null) { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
    }
}
