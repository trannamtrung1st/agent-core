using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public sealed class ScriptedLanguageModel : ILanguageModel
{
    private readonly IReadOnlyList<string> _chunks;
    private readonly TaskCompletionSource? _release;
    private readonly bool _emitAfterCancel;
    private readonly bool _alwaysToolCall;

    public ScriptedLanguageModel(
        IReadOnlyList<string>? chunks = null,
        TaskCompletionSource? releaseAfterFirstChunk = null,
        bool emitAfterCancel = false,
        bool alwaysToolCall = false)
    {
        _chunks = chunks ?? DefaultChunks;
        _release = releaseAfterFirstChunk;
        _emitAfterCancel = emitAfterCancel;
        _alwaysToolCall = alwaysToolCall;
    }

    public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

    public static IReadOnlyList<string> DefaultChunks { get; } = ["Hello", " from ", "synthetic."];

    public static IReadOnlyList<string> LongerChunks { get; } =
        ["There are three points. ", "First, stay present. ", "Second, listen. ", "Third, answer briefly."];

    public static IReadOnlyList<string> ShortChunks { get; } = ["OK."];

    public static IReadOnlyList<string> MarkdownChunks { get; } =
        ["The architecture has **three** pieces:\n\n", "1. Session runtime\n2. Agent execution\n\n", "Use `IAgentProvider`.\n"];

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (TryInitiativeDecision(request, out var initiativeJson))
        {
            yield return new ModelTextDelta(initiativeJson);
            yield return new ModelCompleted(ModelStopReason.Completed);
            yield break;
        }

        if (request.Tools is { Count: > 0 } && (_alwaysToolCall || ShouldScriptTools(request)))
        {
            await foreach (var item in GenerateToolScriptAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var chunks = Select(lastUser);
        for (var index = 0; index < chunks.Count; index++)
        {
            if (!_emitAfterCancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (index == 1 && _release is not null)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (index == 0 && lastUser.Contains("markdown", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }

            yield return new ModelTextDelta(chunks[index]);
            if (index == 0 && IsSteerQueueProbeUser(lastUser))
            {
                await Task.Delay(4000, cancellationToken).ConfigureAwait(false);
            }

            if (index == 0 && lastUser.Contains("hold the line", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (_release is not null && index == 0 && chunks.Count == 1)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private async IAsyncEnumerable<ModelGenerationEvent> GenerateToolScriptAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var toolRounds = request.Messages.Count(message => message.Role == ModelRole.Tool);
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        var lastTool = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool)?.Text ?? string.Empty;
        if (_alwaysToolCall)
        {
            yield return new ModelToolCallEvent(new ModelToolCall($"call-{toolRounds + 1}", ToolCatalog.KnowledgeRetrieve, """{"identity":"support-order-policy"}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 0)
        {
            var identity = lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
                || lastUser.Contains("compliance", StringComparison.OrdinalIgnoreCase)
                ? "compliance-retention"
                : "support-order-policy";
            var name = Offers(request, ToolCatalog.KnowledgeRetrieve) ? ToolCatalog.KnowledgeRetrieve : request.Tools![0].Name;
            yield return new ModelToolCallEvent(new ModelToolCall("call-1", name, $"{{\"identity\":\"{identity}\"}}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (toolRounds == 1 && Offers(request, ToolCatalog.ArtifactsCreate))
        {
            var summary = BuildArtifactSummary(lastUser, lastTool);
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-2",
                ToolCatalog.ArtifactsCreate,
                $"{{\"displayName\":\"case-note.md\",\"contentType\":\"text/markdown\",\"content\":\"{EscapeJson(summary)}\"}}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (_release is not null)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var artifactId = ExtractArtifactId(lastTool);
        var body = BuildFinalAnswer(lastUser, request.Messages, artifactId);
        yield return new ModelTextDelta(body);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

    private static string BuildArtifactSummary(string lastUser, string lastTool)
    {
        if (TryReadKnowledgeTool(lastTool, out var knowledge))
        {
            return SummarizeKnowledgeContent(knowledge.Content);
        }

        return lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            ? "Retention applies to the current demonstration session."
            : "Order 91 is delayed under the simulated policy.";
    }

    private static string SummarizeKnowledgeContent(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            return trimmed.TrimEnd('.');
        }

        return content.Trim();
    }

    private static string BuildFinalAnswer(
        string lastUser,
        IReadOnlyList<ModelMessage> messages,
        string artifactId)
    {
        var knowledgeJson = messages
            .LastOrDefault(message => message.Role == ModelRole.Tool && TryReadKnowledgeTool(message.Text, out _))
            ?.Text;
        if (knowledgeJson is not null && TryReadKnowledgeTool(knowledgeJson, out var document))
        {
            if (document.Identity.Contains("compliance-retention", StringComparison.Ordinal))
            {
                return
                    $"Transcripts are kept for the current demonstration session. [[md:**demonstration session**]] Cite {document.Citation}. [[artifact:{artifactId}]]";
            }

            return $"Order 91 is delayed. [[md:**Delayed**]] [[artifact:{artifactId}]]";
        }

        return lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            ? $"Transcripts are kept for the current demonstration session. [[artifact:{artifactId}]]"
            : $"Order 91 is delayed. [[md:**Delayed**]] [[artifact:{artifactId}]]";
    }

    private static bool TryReadKnowledgeTool(string toolJson, out KnowledgeToolPayload document)
    {
        document = default!;
        if (string.IsNullOrWhiteSpace(toolJson))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(toolJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("identity", out var identity)
                || !root.TryGetProperty("citation", out var citation)
                || !root.TryGetProperty("content", out var content))
            {
                return false;
            }

            document = new KnowledgeToolPayload(
                identity.GetString() ?? string.Empty,
                citation.GetString() ?? string.Empty,
                content.GetString() ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record KnowledgeToolPayload(string Identity, string Citation, string Content);

    private static bool ShouldScriptTools(ModelRequest request)
    {
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        return lastUser.Contains("support case", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("order 91", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            || lastUser.Contains("compliance case", StringComparison.OrdinalIgnoreCase)
            || request.Messages.Any(message => message.Role == ModelRole.Tool);
    }

    private static bool Offers(ModelRequest request, string name) =>
        request.Tools?.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)) == true;

    private static string ExtractArtifactId(string toolJson)
    {
        const string key = "\"artifactId\":\"";
        var start = toolJson.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return FixtureArtifactReferenceAuthorizer.AuthorizedId;
        }

        start += key.Length;
        var end = toolJson.IndexOf('"', start);
        return end < 0 ? FixtureArtifactReferenceAuthorizer.AuthorizedId : toolJson[start..end];
    }

    private static bool IsSteerQueueProbeUser(string lastUser) =>
        lastUser is "Alpha" or "Beta" or "Bravo" or "Charlie";

    private IReadOnlyList<string> Select(string lastUser)
    {
        if (_chunks != DefaultChunks)
        {
            return _chunks;
        }

        if (lastUser.Contains("markdown", StringComparison.OrdinalIgnoreCase))
        {
            return MarkdownChunks;
        }

        if (lastUser.Contains("explain", StringComparison.OrdinalIgnoreCase))
        {
            return LongerChunks;
        }

        if (lastUser.Contains("thanks", StringComparison.OrdinalIgnoreCase))
        {
            return ShortChunks;
        }

        return DefaultChunks;
    }

    private static bool TryInitiativeDecision(ModelRequest request, out string json)
    {
        json = string.Empty;
        var system = request.Messages.FirstOrDefault(message => message.Role == ModelRole.System)?.Text;
        if (system is null || !system.Contains(InitiativeEvaluator.Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            json = SyntheticInitiativeScript.Decide(root);
            return true;
        }
        catch (JsonException)
        {
            json = """{"decision":"staySilent","reason":"Synthetic initiative fallback."}""";
            return true;
        }
    }
}

internal static class SyntheticInitiativeScript
{
    private const int ExaminerLongSilenceMs = 90_000;
    private const int SupportAdvanceSilenceMs = 45_000;

    public static string Decide(JsonElement root)
    {
        var speaks = ReadInt(root, "speaksThisSilencePeriod");
        var maxSpeaks = ReadInt(root, "maxPerSilencePeriod", 1);
        var consecutive = ReadInt(root, "consecutiveProactiveSpeaks");
        var consecutiveCap = ReadInt(root, "consecutiveCap", 1);
        var silenceMs = ReadInt(root, "silenceMs");
        var agentId = ReadAgentId(root);
        var trigger = ReadString(root, "trigger");
        var pendingTopic = ReadString(root, "pendingTopic");

        if (consecutive >= consecutiveCap || speaks >= maxSpeaks)
        {
            return StaySilent("Synthetic initiative cap reached.", 120_000);
        }

        if (string.Equals(trigger, "UnfinishedInteraction", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(pendingTopic))
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic unfinished interaction warrants a proactive follow-up.");
        }

        if (string.Equals(trigger, "EnvironmentUpdate", StringComparison.Ordinal))
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic environment update is actionable.");
        }

        if (string.Equals(agentId, "examiner", StringComparison.Ordinal))
        {
            return DecideExaminer(root, speaks, silenceMs);
        }

        if (string.Equals(agentId, "customer-support", StringComparison.Ordinal))
        {
            return DecideSupport(root, speaks, silenceMs);
        }

        if (speaks >= 1)
        {
            return StaySilent("Synthetic initiative avoids repeated readiness nudges.", 120_000);
        }

        return silenceMs >= 60_000
            ? Speak(InitiativeIntent.Other, "Synthetic initiative allows one proactive turn.")
            : StaySilent("Synthetic initiative waiting for longer silence.", 30_000);
    }

    private static string DecideExaminer(JsonElement root, int speaks, int silenceMs)
    {
        _ = root;
        if (speaks >= 1)
        {
            return StaySilent("Synthetic examiner avoids a second empty nudge.", 120_000);
        }

        if (silenceMs >= ExaminerLongSilenceMs)
        {
            return Speak(
                InitiativeIntent.Hint,
                "Synthetic long silence during practice exam; offer a concise scaffold or hint.");
        }

        return StaySilent("Synthetic examiner waiting for longer candidate silence.", 30_000);
    }

    private static string DecideSupport(JsonElement root, int speaks, int silenceMs)
    {
        if (!HasUnresolvedSupportContext(root))
        {
            return StaySilent("Synthetic support has no unresolved work.", 120_000);
        }

        if (speaks >= 1 && silenceMs < SupportAdvanceSilenceMs)
        {
            return StaySilent("Synthetic support pauses briefly between proactive updates.", 20_000);
        }

        if (silenceMs >= SupportAdvanceSilenceMs)
        {
            return Speak(InitiativeIntent.FollowUp, "Synthetic support advances the simulated order conversation.");
        }

        return StaySilent("Synthetic support waiting for longer silence.", 20_000);
    }

    private static bool HasUnresolvedSupportContext(JsonElement root)
    {
        if (!root.TryGetProperty("recentTurns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var sawSupportIssue = false;
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("role", out var roleNode)
                || !turn.TryGetProperty("text", out var textNode))
            {
                continue;
            }

            var role = roleNode.GetString() ?? string.Empty;
            var text = textNode.GetString() ?? string.Empty;
            if (string.Equals(role, "User", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsSupportIssue(text))
                {
                    sawSupportIssue = true;
                }

                if (sawSupportIssue && IsSupportClosure(text))
                {
                    return false;
                }
            }
        }

        return sawSupportIssue;
    }

    private static bool ContainsSupportIssue(string text) =>
        text.Contains("order", StringComparison.OrdinalIgnoreCase)
        || text.Contains("shipment", StringComparison.OrdinalIgnoreCase)
        || text.Contains("delivery", StringComparison.OrdinalIgnoreCase)
        || text.Contains("refund", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportClosure(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.Contains("that's all", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("thats all", StringComparison.OrdinalIgnoreCase)
            || (normalized.Contains("thank", StringComparison.OrdinalIgnoreCase)
                && !ContainsSupportIssue(normalized));
    }

    private static string ReadAgentId(JsonElement root)
    {
        if (!root.TryGetProperty("agent", out var agent) || !agent.TryGetProperty("id", out var idNode))
        {
            return string.Empty;
        }

        return idNode.GetString() ?? string.Empty;
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue = 0)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Number)
        {
            return defaultValue;
        }

        return node.TryGetInt32(out var value) ? value : defaultValue;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return node.GetString() ?? string.Empty;
    }

    private static string Speak(InitiativeIntent intent, string objective) =>
        JsonSerializer.Serialize(new { decision = "speak", intent = InitiativeIntents.ToWire(intent), objective });

    private static string StaySilent(string reason, int nextWaitMs) =>
        JsonSerializer.Serialize(new { decision = "staySilent", reason, nextWaitMs });
}
