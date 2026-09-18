using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers.OpenAICompatible;

namespace AgentCore.Infrastructure.Providers.OpenAI;

public sealed class SpeechRecognitionProviderOptions
{
    public string Adapter { get; set; } = "Synthetic";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; } = "gpt-live-transcribe";
    public bool IncludeTranscriptionDelay { get; set; }
    public Dictionary<string, string> AdditionalHeaders { get; set; } = [];
    public ProviderTimeoutOptions Timeouts { get; set; } = new();
    public Dictionary<string, bool> DisabledCapabilities { get; set; } = [];
}

public static class OpenAiRealtimeTranscriptionPayload
{
    public static string SessionUpdateJson(string model, bool includeDelay = false)
    {
        var transcription = new JsonObject
        {
            ["model"] = model
        };
        if (includeDelay)
        {
            transcription["delay"] = "low";
        }

        var session = new JsonObject
        {
            ["type"] = "transcription",
            ["audio"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["format"] = new JsonObject
                    {
                        ["type"] = "audio/pcm",
                        ["rate"] = 24000
                    },
                    ["transcription"] = transcription
                }
            },
            ["turn_detection"] = null
        };
        var root = new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = session
        };
        return root.ToJsonString();
    }
}

public sealed class OpenAiSpeechRecognizer : ISpeechRecognizer
{
    private readonly SpeechRecognitionProviderOptions _options;

    public OpenAiSpeechRecognizer(SpeechRecognitionProviderOptions options)
    {
        _options = options;
        Capabilities = new RecognitionCapabilities(true, true, false, true);
    }

    public RecognitionCapabilities Capabilities { get; }

    public ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = options;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw AgentCoreErrors.VoiceUnavailable();
        }

        return ValueTask.FromResult<ISpeechRecognitionSession>(new DeferredSession(_options));
    }

    private sealed class DeferredSession(SpeechRecognitionProviderOptions options) : ISpeechRecognitionSession
    {
        public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            _ = (options, frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask ObserveBoundaryAsync(
            Guid utteranceId,
            SpeechBoundary boundary,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<SpeechRecognitionEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class AsyncEnumerable
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
