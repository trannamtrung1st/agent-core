using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Work;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Work;

public static class DurableToolCallCheckpoint
{
    public const string CompletionCallId = "work-complete";
    public const string FinishRequired = """{"error":"finish_required","reason":"checkpoint_capacity","message":"Finish from evidence already collected; do not request more tools."}""";
    public static string FinishRequiredResult(int budget) => Encoding.UTF8.GetByteCount(FinishRequired) <= budget ? FinishRequired
        : budget >= "{\"error\":\"finish_required\"}".Length ? "{\"error\":\"finish_required\"}"
        : ToolJsonResults.MinimalValidJson(Math.Max(0, budget));
    // Checkpoints are data, never HTML. Relaxed encoding keeps the compact terminal base64 alphabet unescaped.
    private static readonly JsonSerializerOptions CheckpointJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Lazy<int> TerminalReserve = new(ComputeCompletionReserve);
    private static readonly Lazy<int> CapacityResponseReserve = new(ComputeFinishRequiredReserve);
    public static int CompletionReserve(TriggerKind kind) => TerminalReserve.Value;
    public static int FinishRequiredReserve() => CapacityResponseReserve.Value;
    private static int ComputeCompletionReserve()
    {
        using var schema = JsonDocument.Parse(WorkCompletionRequest.Contract.ParametersJson);
        var max = schema.RootElement.GetProperty("properties").GetProperty("summary").GetProperty("maxLength").GetInt32();
        var args = JsonSerializer.Serialize(new { summary = new string('x', max), attentionRequired = true, outcome = "AttentionRequested" });
        return Encoding.UTF8.GetByteCount(Write([new(ModelRole.Assistant, "", ToolCalls:
            [new(CompletionCallId, ToolCatalog.WorkComplete, args)])])) - Encoding.UTF8.GetByteCount(Write([])) + 1;
    }

    private static int ComputeFinishRequiredReserve()
    {
        // One bounded Continuity search can be refused without consuming mandatory terminal space.
        using var schema = JsonDocument.Parse(ToolRegistry.All.Single(t => t.Name == ToolCatalog.ContinuitySearch).ModelDefinition.ParametersJson);
        var max = schema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("maxLength").GetInt32();
        var call = new ModelToolCall(new string('x', WorkLimits.MaxToolNameCharacters), ToolCatalog.ContinuitySearch,
            JsonSerializer.Serialize(new { query = new string('\u0001', max), limit = 10 }));
        // Appending to a nonempty message array also adds one comma.
        return Encoding.UTF8.GetByteCount(Write([new(ModelRole.Assistant, "", ToolCalls: [call]),
            new(ModelRole.Tool, FinishRequired, ToolCallId: call.Id, Name: call.Name)])) - Encoding.UTF8.GetByteCount(Write([])) + 1;
    }

    public static bool TryWriteWithReserve(IReadOnlyList<ModelMessage> messages, bool observationRequired,
        string? blockedActionHash, int reserve, out string payload, IReadOnlyList<string>? loadedCapabilityIds = null, int capabilityLoadCount = 0, ExecutionSkillState? skillState = null) =>
        TryWrite(messages, observationRequired, blockedActionHash, out payload, loadedCapabilityIds, capabilityLoadCount, skillState)
        && Encoding.UTF8.GetByteCount(payload) + RecoveryHeadroom(blockedActionHash) + reserve <= WorkLimits.MaxCheckpointBytes;

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
        string? blockedActionHash = null, IReadOnlyList<string>? loadedCapabilityIds = null, int capabilityLoadCount = 0, ExecutionSkillState? skillState = null) =>
        JsonSerializer.Serialize(new Document(
            Phase,
            messages.Select(MessageDto.From).ToArray(),
            observationRequired,
            blockedActionHash, loadedCapabilityIds, capabilityLoadCount, skillState), CheckpointJson);

    public static bool TryWrite(IReadOnlyList<ModelMessage> messages, bool observationRequired,
        string? blockedActionHash, out string payload, IReadOnlyList<string>? loadedCapabilityIds = null, int capabilityLoadCount = 0, ExecutionSkillState? skillState = null)
    {
        payload = Write(messages, observationRequired, blockedActionHash, loadedCapabilityIds, capabilityLoadCount, skillState);
        return Encoding.UTF8.GetByteCount(payload) + RecoveryHeadroom(blockedActionHash) <= WorkLimits.MaxCheckpointBytes;
    }

    public static int ToolResultBudget(IReadOnlyList<ModelMessage> messages, ModelToolCall call,
        bool observationRequired, string? blockedActionHash, TriggerKind kind = TriggerKind.ScheduledOccurrence,
        IReadOnlyList<string>? loadedCapabilityIds = null, int capabilityLoadCount = 0, ExecutionSkillState? skillState = null)
    {
        var withResult = messages.Append(new ModelMessage(ModelRole.Tool, string.Empty,
            ToolCallId: call.Id, Name: call.Name)).ToArray();
        var overhead = Encoding.UTF8.GetByteCount(Write(withResult, observationRequired, blockedActionHash, loadedCapabilityIds, capabilityLoadCount, skillState));
        // A UTF-8 byte can expand to six bytes in a JSON string (for example, a control character).
        // Tool adapters receive a conservative text budget; admission still checks the exact document.
        return Math.Max(0, WorkLimits.MaxCheckpointBytes - overhead - RecoveryHeadroom(blockedActionHash) - CompletionReserve(kind) - FinishRequiredReserve()) / 6;
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
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

    public static (IReadOnlyList<string> Ids, int Calls) ReadCapabilityState(WorkCheckpoint? checkpoint)
    {
        if (checkpoint is null) return ([], 0);
        var document = JsonSerializer.Deserialize<Document>(checkpoint.PayloadJson);
        var ids = document?.LoadedCapabilityIds ?? [];
        var calls = document?.CapabilityLoadCount ?? 0;
        if (calls is < 0 or > 8 || ids.Count > 64 || AgentCore.Domain.Definitions.AgentDefinitionValidator.ToolAllowlistFindings(ids).Count > 0)
            throw AgentCore.Application.Sessions.AgentCoreErrors.Conflict("Capability checkpoint state is invalid.");
        return (ids, calls);
    }

    public static ExecutionSkillState ReadSkillState(WorkCheckpoint? checkpoint)
    {
        var state = checkpoint is null ? null : JsonSerializer.Deserialize<Document>(checkpoint.PayloadJson)?.SkillState;
        if (state is null) throw new InvalidOperationException("Pinned Skill checkpoint is missing.");
        if (state.LoadCount is < 0 or > 2 || state.ActiveKeys.Distinct(StringComparer.Ordinal).Count() != state.ActiveKeys.Count)
            throw new InvalidOperationException("Pinned Skill checkpoint load state is invalid.");
        var catalog = SkillPolicy.FreezeCatalog(state.Catalog);
        SkillPolicy.ValidateActive(catalog, state.ActiveKeys);
        return state with { Catalog = catalog, ActiveKeys = Array.AsReadOnly(state.ActiveKeys.ToArray()) };
    }
    public sealed record ExecutionSkillState(IReadOnlyList<EffectiveSkill> Catalog, IReadOnlyList<string> ActiveKeys, int LoadCount);

    private sealed record Document(
        string Phase,
        MessageDto[] Messages,
        bool ObservationRequired = false,
        string? BlockedActionHash = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? LoadedCapabilityIds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int CapabilityLoadCount = 0, ExecutionSkillState? SkillState = null);

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
                message.ToolCalls?.Select(ToolCallDto.From).ToArray());
        // Parts stay out of the checkpoint. A browser.screenshot result keeps its artifact id in text and is reloaded from IWorkCaptureStore.

        public ModelMessage ToMessage() =>
            new(
                Enum.Parse<ModelRole>(Role),
                Text,
                ToolCallId: ToolCallId,
                Name: Name,
                ToolCalls: ToolCalls?.Select(call => new ModelToolCall(call.Id, call.Name, call.Completion?.ArgumentsJson() ?? call.ArgumentsJson!)).ToArray());
    }

    // Optional compact completion data preserves old checkpoints and avoids double JSON escaping of a
    // maximum valid summary. UTF-16 base64 has a fixed bound for every .NET character, independent of content.
    private sealed record ToolCallDto(string Id, string Name, string? ArgumentsJson,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompletionDto? Completion = null)
    {
        public static ToolCallDto From(ModelToolCall call)
        {
            if (call.Name == ToolCatalog.WorkComplete)
            {
                try
                {
                    using var json = JsonDocument.Parse(call.ArgumentsJson);
                    var args = json.RootElement;
                    if (args.ValueKind != JsonValueKind.Object) return new(call.Id, call.Name, call.ArgumentsJson);
                    string summary; bool attention;
                    if (WorkCompletionRequest.TryParse(args, [], out var result, out attention, out _))
                    {
                        summary = WorkCompletionRequest.Summary(result);
                        return new(call.Id, call.Name, null, new(Convert.ToBase64String(Encoding.Unicode.GetBytes(summary)), attention,
                            args.GetProperty("outcome").GetString()));
                    }
                }
                catch (JsonException) { }
            }
            return new(call.Id, call.Name, call.ArgumentsJson);
        }
    }
    private sealed record CompletionDto(string SummaryUtf16, bool AttentionRequired, string? Outcome)
    {
        public string ArgumentsJson()
        {
            var summary = Encoding.Unicode.GetString(Convert.FromBase64String(SummaryUtf16));
            return Outcome is null ? JsonSerializer.Serialize(new { summary, attentionRequired = AttentionRequired })
                : JsonSerializer.Serialize(new { summary, attentionRequired = AttentionRequired, outcome = Outcome });
        }
    }
}
