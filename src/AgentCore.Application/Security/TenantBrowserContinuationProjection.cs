using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBrowserContinuationProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Observation(BrowserResult result)
    {
        // No binary/download delivery or raw provider object crosses into a checkpoint.
        var snapshot = result.Observation;
        for (var budget = 16384; ; budget /= 2)
        {
            var observed = snapshot is null ? null : snapshot with {
                Content = ToolJsonResults.ClipUtf8Prefix(snapshot.Content, budget),
                Url = ToolJsonResults.ClipUtf8Prefix(snapshot.Url, 2048), Title = ToolJsonResults.ClipUtf8Prefix(snapshot.Title, 512),
                Targets = snapshot.Targets.Take(budget / 512).ToArray(), Boxes = null,
                Frames = snapshot.Frames?.Take(budget / 512).ToArray(),
                ContentTruncated = snapshot.ContentTruncated || Encoding.UTF8.GetByteCount(snapshot.Content) > budget || snapshot.Targets.Count > budget / 512 };
            JsonNode? data = null;
            try { if (result.DataJson is { } text && Encoding.UTF8.GetByteCount(text) <= budget) data = JsonNode.Parse(text); }
            catch (JsonException) { }
            var textResult = JsonSerializer.Serialize(new { untrustedBrowserContent = true, observation = observed, data,
                pages = result.Pages?.Take(budget / 512), status = ToolJsonResults.ClipUtf8Prefix(result.Status ?? "", 128), result.EffectAttempted, result.EffectConfirmedBySdk,
                applicationOutcomeVerified = false, truncated = budget < 16384 || result.DataJson is not null && data is null }, Json);
            if (Encoding.UTF8.GetByteCount(textResult) <= 32768) return textResult;
            if (budget == 0) return """{"untrustedBrowserContent":true,"truncated":true,"observationUnavailable":true}""";
        }
    }
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint, ModelToolCall call, TenantApprovalRequest request,
        TenantOperationOutcome outcome, string reason, string? observation, string? unavailable = null)
    {
        var node = observation is null ? null : JsonNode.Parse(observation);
        var result = Serialize(node, node is null ? unavailable ?? "browser_observation_not_retained" : null);
        try { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, result); }
        catch (ArgumentException) when (node is not null)
        { return AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint, call, Serialize(null, "result_budget_exhausted")); }
        string Serialize(JsonNode? value, string? missing) => JsonSerializer.Serialize(new {
            approvalStatus = JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),
            outcome = JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()), reasonCode = reason, requestId = request.RequestId, operationId = request.Operation.OperationId,
            applied = (bool?)null, commandCompleted = outcome == TenantOperationOutcome.Completed, applicationOutcomeVerified = false,
            browser = value, observationUnavailable = value is null, observationUnavailableReason = missing,
            replayAllowed = false
        }, Json);
    }
    public static AgentRun Save(AgentRun before, AgentRun saved, TenantApprovalRequest request, TenantOperationOutcome outcome, string reason,
        DateTimeOffset at, TenantBrowserEffectObservation? observation)
    {
        if (saved.Status != AgentRunStatus.Running || saved.CancellationRequested || saved.Claim is null || saved.Claim.LeaseExpiresAtUtc <= at
            || before.Claim?.Generation != before.SideEffect.ExternalEffect?.Generation || saved.Checkpoint is not { } checkpoint
            || !AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages)) return saved;
        var calls = AgentRunToolCallCheckpoint.PendingCalls(messages!);
        if (calls.Count == 0) return saved;
        TenantRunContinuationService.ValidateAuthorizationCheckpoint(saved, request, requirePending: true);
        try
        {
            var completed = Complete(checkpoint.PauseBudgetClock(at), calls[0], request, outcome, reason,
                outcome == TenantOperationOutcome.Completed && observation?.RunRevision == before.Revision ? observation.Json : null);
            var checkpointed = saved.SaveCheckpoint(saved.Revision, saved.Claim.Generation, completed, null, at);
            return checkpointed.AwaitApprovedExternalResultDelivery(checkpointed.Revision, saved.Claim.Generation, request.RequestId, request.Operation.OperationId, request.Operation.ActionHash, at);
        }
        catch (ArgumentException) { return saved; } // Factual settlement must survive exhausted checkpoint headroom.
    }
}
