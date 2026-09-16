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

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
            var summary = lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
                ? "Retention window is 90 days for simulated cases."
                : "Order 91 is delayed under the simulated policy.";
            yield return new ModelToolCallEvent(new ModelToolCall(
                "call-2",
                ToolCatalog.ArtifactsCreate,
                $"{{\"displayName\":\"case-note.md\",\"contentType\":\"text/markdown\",\"content\":\"{summary}\"}}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            yield break;
        }

        if (_release is not null)
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var artifactId = ExtractArtifactId(lastTool);
        var body = lastUser.Contains("retention", StringComparison.OrdinalIgnoreCase)
            ? $"Cite 90-day retention. [[md:**90 days**]] [[artifact:{artifactId}]]"
            : $"Order 91 is delayed. [[md:**Delayed**]] [[artifact:{artifactId}]]";
        yield return new ModelTextDelta(body);
        yield return new ModelCompleted(ModelStopReason.Completed);
    }

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
}
