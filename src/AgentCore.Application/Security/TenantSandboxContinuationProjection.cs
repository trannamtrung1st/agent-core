using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantApprovalOutcome outcome, TenantSandboxResultRead observation)
    {
        // Fit the actual serialized checkpoint, retaining exact outcome even when streams need clipping.
        for (var streamBudget = 16384; ; streamBudget /= 2)
        {
            var result = observation.Result;
            var stdout = ToolJsonResults.ClipUtf8Prefix(result?.Stdout ?? "", streamBudget);
            var stderr = ToolJsonResults.ClipUtf8Prefix(result?.Stderr ?? "", streamBudget);
            var text = JsonSerializer.Serialize(new {
                approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
                outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()), outcome.ReasonCode, outcome.OperationId,
                applied = (bool?)null,
                sandbox = result is null ? null : new { result.ExecutionId, result.Status, result.ExitCode, stdout, stderr,
                    truncated = result.Truncated || stdout != result.Stdout || stderr != result.Stderr, combinedLogs = result.Background,
                    result.DurationMs, result.FilesystemEffectsMayHaveOccurred, result.TerminationConfirmed, result.RemoteTerminationConfirmed,
                    result.GenerationRecycled, result.ProfileId, result.ProfileRevision, result.Generation, result.Phase, result.FailureCategory, result.CancellationReason, result.DiagnosticId },
                resultUnavailable = result is null, resultUnavailableReason = observation.UnavailableReason
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, text); }
            catch (ArgumentException) when (streamBudget > 0) { }
        }
    }
    public static AgentRunCheckpoint CompleteRead(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantApprovalOutcome outcome, string? observation, string? unavailableReason)
    {
        var original = observation is null ? null : JsonNode.Parse(observation);
        for (var budget = 16384; ; budget /= 2)
        {
            var read = original?.DeepClone();
            if (read?["execution"] is JsonObject execution)
                foreach (var stream in new[] { "stdout", "stderr" })
                {
                    var text = execution[stream]?.GetValue<string>() ?? "";
                    var clipped = ToolJsonResults.ClipUtf8Prefix(text, budget);
                    execution[stream] = clipped;
                    if (clipped != text) execution["truncated"] = true;
                }
            if (read?["profiles"] is JsonArray profiles)
            {
                var limit = Math.Min(profiles.Count, budget / 512);
                while (profiles.Count > limit) { profiles.RemoveAt(profiles.Count - 1); read["truncated"] = true; }
            }
            var textResult = Serialize(read, read is null ? unavailableReason ?? "read_not_authorized" : null);
            try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, textResult); }
            catch (ArgumentException) when (budget > 0) { }
            catch (ArgumentException) when (budget == 0 && read is not null)
            { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
        }
        string Serialize(JsonNode? read, string? reason) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
            outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.Outcome.ToString()), reasonCode = outcome.ReasonCode, operationId = outcome.OperationId,
            applied = false, readAuthorized = outcome.Outcome == TenantOperationOutcome.Completed,
            inspection = read, inspectionUnavailable = read is null, inspectionUnavailableReason = reason
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

}
