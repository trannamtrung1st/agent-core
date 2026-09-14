using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public sealed class ScriptedLanguageModel : ILanguageModel
{
    private readonly IReadOnlyList<string> _chunks;
    private readonly TaskCompletionSource? _release;
    private readonly bool _emitAfterCancel;

    public ScriptedLanguageModel(
        IReadOnlyList<string>? chunks = null,
        TaskCompletionSource? releaseAfterFirstChunk = null,
        bool emitAfterCancel = false)
    {
        _chunks = chunks ?? DefaultChunks;
        _release = releaseAfterFirstChunk;
        _emitAfterCancel = emitAfterCancel;
    }

    public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

    public static IReadOnlyList<string> DefaultChunks { get; } = ["Hello", " from ", "synthetic."];

    public static IReadOnlyList<string> LongerChunks { get; } =
        ["There are three points. ", "First, stay present. ", "Second, listen. ", "Third, answer briefly."];

    public static IReadOnlyList<string> ShortChunks { get; } = ["OK."];

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelCompleted(ModelStopReason.Completed);
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
