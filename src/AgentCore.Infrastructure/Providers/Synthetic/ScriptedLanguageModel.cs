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
            if (index == 0 && lastUser.Contains("hold the line", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
            var speaks = root.TryGetProperty("speaksThisSilencePeriod", out var speaksNode)
                ? speaksNode.GetInt32()
                : 0;
            var maxSpeaks = root.TryGetProperty("maxPerSilencePeriod", out var maxSpeaksNode)
                ? maxSpeaksNode.GetInt32()
                : 1;
            var consecutive = root.TryGetProperty("consecutiveProactiveSpeaks", out var consecutiveNode)
                ? consecutiveNode.GetInt32()
                : 0;
            var consecutiveCap = root.TryGetProperty("consecutiveCap", out var capNode)
                ? capNode.GetInt32()
                : 1;
            var speak = speaks < maxSpeaks && consecutive < consecutiveCap;
            json = speak
                ? """{"decision":"speak","reason":"Synthetic initiative allows one more proactive turn."}"""
                : """{"decision":"staySilent","reason":"Synthetic initiative cap or duplicate nudge.","nextWaitMs":120000}""";
            return true;
        }
        catch (JsonException)
        {
            json = """{"decision":"staySilent","reason":"Synthetic initiative fallback."}""";
            return true;
        }
    }
}
