using System.Net;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpeechRecognizerTests
{
    [Fact]
    public void Session_update_omits_delay_by_default_and_includes_it_on_fixture()
    {
        var omitted = OpenAiRealtimeTranscriptionPayload.SessionUpdateJson("gpt-live-transcribe");
        Assert.Contains("\"type\":\"transcription\"", omitted, StringComparison.Ordinal);
        Assert.Contains("\"turn_detection\":null", omitted, StringComparison.Ordinal);
        Assert.Contains("\"rate\":24000", omitted, StringComparison.Ordinal);
        Assert.DoesNotContain("delay", omitted, StringComparison.Ordinal);
        var included = OpenAiRealtimeTranscriptionPayload.SessionUpdateJson("gpt-live-transcribe", includeDelay: true);
        Assert.Contains("\"delay\":\"low\"", included, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_without_key_is_voice_unavailable()
    {
        var recognizer = new OpenAiSpeechRecognizer(new SpeechRecognitionProviderOptions { ApiKey = null });
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
            await recognizer.OpenAsync(new RecognitionOptions(CanonicalAudio.Format, "en")));
    }

    [Fact]
    public async Task Batch_posts_wav_and_emits_one_final()
    {
        var handler = new BatchHandler("""{"text":"batch ok"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/v1/") };
        var recognizer = new OpenAICompatibleBatchSpeechRecognizer(
            http,
            new SpeechRecognitionProviderOptions
            {
                BaseUrl = "http://127.0.0.1/v1/",
                ApiKey = "test",
                DefaultModel = "whisper-1"
            });
        await using var session = await recognizer.OpenAsync(new RecognitionOptions(CanonicalAudio.Format, "en"));
        var utterance = Guid.NewGuid();
        await session.PushAudioAsync(new AudioFrame(1, 0, new byte[960]));
        await session.ObserveBoundaryAsync(utterance, SpeechBoundary.Ended);
        var listed = new List<SpeechRecognitionEvent>();
        var read = session.ReadEventsAsync();
        await foreach (var item in read)
        {
            listed.Add(item);
            break;
        }

        var final = Assert.IsType<SpeechFinal>(listed[0]);
        Assert.Equal("batch ok", final.Text);
        Assert.Equal(1, handler.Posts);
        Assert.Contains("audio/transcriptions", handler.LastUri, StringComparison.Ordinal);
        Assert.False(recognizer.Capabilities.PartialTranscripts);
        Assert.False(recognizer.Capabilities.StreamingAudio);
    }

    [Fact]
    public async Task Batch_posts_whisper_language_hint_from_effective_locale()
    {
        var handler = new BatchHandler("""{"text":"bonjour"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/v1/") };
        var recognizer = new OpenAICompatibleBatchSpeechRecognizer(
            http,
            new SpeechRecognitionProviderOptions
            {
                BaseUrl = "http://127.0.0.1/v1/",
                ApiKey = "test",
                DefaultModel = "whisper-1"
            });
        await using var session = await recognizer.OpenAsync(new RecognitionOptions(CanonicalAudio.Format, "fr-FR"));
        var utterance = Guid.NewGuid();
        await session.PushAudioAsync(new AudioFrame(1, 0, new byte[960]));
        await session.ObserveBoundaryAsync(utterance, SpeechBoundary.Ended);
        await foreach (var _ in session.ReadEventsAsync())
        {
            break;
        }

        Assert.Equal("fr", handler.LastLanguage);
        Assert.Equal("fr", OpenAICompatibleBatchSpeechRecognizer.WhisperLanguageHint("fr-FR"));
        Assert.True(recognizer.CanRecognize("ja-JP"));
        Assert.False(recognizer.CanSynthesize("en"));
    }

    [Fact]
    public void Synthetic_di_resolves_synthetic_recognizer_without_openai()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic", new LanguageModelProviderOptions { Adapter = "Scripted" });
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SyntheticSpeechRecognizer>(provider.GetRequiredService<ISpeechRecognizer>());
        Assert.IsNotType<OpenAiSpeechRecognizer>(provider.GetRequiredService<ISpeechRecognizer>());
    }

    [LiveOpenAiSpeechFact]
    public void Live_openai_stt_is_opt_in()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var recognizer = new OpenAiSpeechRecognizer(new SpeechRecognitionProviderOptions
        {
            ApiKey = key,
            DefaultModel = "gpt-live-transcribe"
        });
        Assert.True(recognizer.Capabilities.StreamingAudio);
        Assert.True(recognizer.Capabilities.PartialTranscripts);
        Assert.False(string.IsNullOrWhiteSpace(OpenAiRealtimeTranscriptionPayload.SessionUpdateJson("gpt-live-transcribe")));
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return Path.Combine(dir.FullName, "agents");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class BatchHandler(string body) : HttpMessageHandler
    {
        public int Posts { get; private set; }

        public string LastUri { get; private set; } = "";

        public string LastLanguage { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts++;
            LastUri = request.RequestUri?.ToString() ?? "";
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (string.Equals(name, "language", StringComparison.Ordinal))
                    {
                        LastLanguage = await part.ReadAsStringAsync(cancellationToken);
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class LiveOpenAiSpeechFactAttribute : FactAttribute
{
    public LiveOpenAiSpeechFactAttribute()
    {
        var optIn = string.Equals(Environment.GetEnvironmentVariable("AGENTCORE_LIVE_OPENAI_STT"), "1", StringComparison.Ordinal);
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!optIn)
        {
            Skip = "Opt-in AGENTCORE_LIVE_OPENAI_STT=1 is required; default suites must not call OpenAI.";
        }
        else if (string.IsNullOrWhiteSpace(key))
        {
            Skip = "OPENAI_API_KEY is missing.";
        }
    }
}
