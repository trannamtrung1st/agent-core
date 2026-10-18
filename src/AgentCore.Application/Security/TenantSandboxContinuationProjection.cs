using System.Text.Json;
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
}
