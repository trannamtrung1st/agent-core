using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public static class DurableToolCallCheckpoint
{
    public static IReadOnlyList<ModelToolCall> PendingCalls(IReadOnlyList<ModelMessage> messages)
    {
        var batchIndex = FindLatestAssistantToolBatchIndex(messages);
        if (batchIndex < 0)
        {
            return [];
        }

        var batch = messages[batchIndex].ToolCalls!;
        var answered = new HashSet<string>(StringComparer.Ordinal);
        for (var index = batchIndex + 1; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == ModelRole.Tool && message.ToolCallId is not null)
            {
                answered.Add(message.ToolCallId);
            }
        }

        var pending = new List<ModelToolCall>();
        foreach (var call in batch)
        {
            if (!answered.Contains(call.Id))
            {
                pending.Add(call);
            }
        }

        return pending;
    }

    public static int ReservedToolSteps(IReadOnlyList<ModelMessage> messages)
    {
        var reserved = 0;
        foreach (var message in messages)
        {
            if (message.Role == ModelRole.Assistant && message.ToolCalls is { Count: > 0 } toolCalls)
            {
                reserved += toolCalls.Count;
            }
        }

        return reserved;
    }

    public static int NormalizeResumedStepCount(int checkpointStepCount, IReadOnlyList<ModelMessage> messages) =>
        Math.Max(checkpointStepCount, ReservedToolSteps(messages));

    public static string? TryResolveLegacyToolCallId(
        WorkCheckpoint? checkpoint,
        WorkSideEffectDisposition disposition,
        string? actionHash,
        WorkApproval? approval)
    {
        if (!TryReadMessages(checkpoint, out var messages, out _, out _) || messages is null)
        {
            return null;
        }

        var pending = PendingCalls(messages);
        if (approval is not null)
        {
            var matched = pending
                .Where(call => string.Equals(call.Name, approval.ToolName, StringComparison.Ordinal))
                .ToArray();
            if (matched.Length == 1)
            {
                return matched[0].Id;
            }

            if (matched.Length > 1 && !string.IsNullOrWhiteSpace(actionHash))
            {
                var hashMatched = matched
                    .Where(call => MatchesStoredAction(call, actionHash))
                    .ToArray();
                if (hashMatched.Length == 1)
                {
                    return hashMatched[0].Id;
                }
            }
        }

        if (disposition == WorkSideEffectDisposition.Succeeded)
        {
            var batchIndex = FindLatestAssistantToolBatchIndex(messages);
            if (batchIndex < 0)
            {
                return null;
            }

            var batch = messages[batchIndex].ToolCalls!;
            if (!string.IsNullOrWhiteSpace(actionHash))
            {
                var hashMatched = batch
                    .Where(call => MatchesStoredAction(call, actionHash))
                    .ToArray();
                return hashMatched.Length == 1 ? hashMatched[0].Id : null;
            }

            return batch.Count == 1 ? batch[0].Id : null;
        }

        return pending.Count == 1 ? pending[0].Id : null;
    }

    public static bool TryRead(WorkCheckpoint? checkpoint, out IReadOnlyList<ModelMessage>? messages) =>
        TryReadMessages(checkpoint, out messages, out _, out _);

    public static bool TryReadState(
        WorkCheckpoint? checkpoint,
        out IReadOnlyList<ModelMessage>? messages,
        out bool observationRequired,
        out string? blockedActionHash) =>
        TryReadMessages(checkpoint, out messages, out observationRequired, out blockedActionHash);

    public static string Write(
        IReadOnlyList<ModelMessage> messages,
        bool observationRequired = false,
        string? blockedActionHash = null) =>
        JsonSerializer.Serialize(new Document(
            Phase,
            messages.Select(MessageDto.From).ToArray(),
            observationRequired,
            blockedActionHash));

    public static bool TryWrite(IReadOnlyList<ModelMessage> messages, bool observationRequired,
        string? blockedActionHash, out string payload)
    {
        payload = Write(messages, observationRequired, blockedActionHash);
        return Encoding.UTF8.GetByteCount(payload) + RecoveryHeadroom(blockedActionHash) <= WorkLimits.MaxCheckpointBytes;
    }

    public static int ToolResultBudget(IReadOnlyList<ModelMessage> messages, ModelToolCall call,
        bool observationRequired, string? blockedActionHash)
    {
        var withResult = messages.Append(new ModelMessage(ModelRole.Tool, string.Empty,
            ToolCallId: call.Id, Name: call.Name)).ToArray();
        var overhead = Encoding.UTF8.GetByteCount(Write(withResult, observationRequired, blockedActionHash));
        // A UTF-8 byte can expand to six bytes in a JSON string (for example, a control character).
        // Tool adapters receive a conservative text budget; admission still checks the exact document.
        return Math.Max(0, WorkLimits.MaxCheckpointBytes - overhead - RecoveryHeadroom(blockedActionHash)) / 6;
    }

    // Recovery may need to persist a SHA-256 blocked-action hash without replaying an uncertain browser effect.
    private static int RecoveryHeadroom(string? blockedActionHash) => blockedActionHash is null ? 64 : 0;

    private const string Phase = "model-turn";

    private static int FindLatestAssistantToolBatchIndex(IReadOnlyList<ModelMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ModelRole.Assistant
                && messages[index].ToolCalls is { Count: > 0 })
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasToolResult(IReadOnlyList<ModelMessage> messages, string toolCallId) =>
        messages.Any(message =>
            message.Role == ModelRole.Tool
            && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));

    private static bool MatchesStoredAction(ModelToolCall call, string actionHash)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return string.Equals(ToolActionHash.Compute(call.Name, args), actionHash, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadMessages(
        WorkCheckpoint? checkpoint,
        out IReadOnlyList<ModelMessage>? messages,
        out bool observationRequired,
        out string? blockedActionHash)
    {
        messages = null;
        observationRequired = false;
        blockedActionHash = null;
        if (checkpoint is null || !checkpoint.PayloadJson.Contains(Phase, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(checkpoint.PayloadJson);
            if (document is null || !string.Equals(document.Phase, Phase, StringComparison.Ordinal) || document.Messages is null)
            {
                return false;
            }

            messages = document.Messages.Select(message => message.ToMessage()).ToArray();
            observationRequired = document.ObservationRequired;
            blockedActionHash = document.BlockedActionHash;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Document(
        string Phase,
        MessageDto[] Messages,
        bool ObservationRequired = false,
        string? BlockedActionHash = null);

    private sealed record MessageDto(
        string Role,
        string Text,
        string? ToolCallId,
        string? Name,
        ToolCallDto[]? ToolCalls)
    {
        public static MessageDto From(ModelMessage message) =>
            new(
                message.Role.ToString(),
                message.Text,
                message.ToolCallId,
                message.Name,
                message.ToolCalls?.Select(call => new ToolCallDto(call.Id, call.Name, call.ArgumentsJson)).ToArray());
        // Parts stay out of the checkpoint. A browser.capture result keeps its artifact id in text and is reloaded from IWorkCaptureStore.

        public ModelMessage ToMessage() =>
            new(
                Enum.Parse<ModelRole>(Role),
                Text,
                ToolCallId: ToolCallId,
                Name: Name,
                ToolCalls: ToolCalls?.Select(call => new ModelToolCall(call.Id, call.Name, call.ArgumentsJson)).ToArray());
    }

    private sealed record ToolCallDto(string Id, string Name, string ArgumentsJson);
}
