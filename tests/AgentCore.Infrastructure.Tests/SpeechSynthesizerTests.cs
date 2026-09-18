using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpeechSynthesizerTests
{
    [Fact]
    public async Task Synthetic_emits_contiguous_frames_marks_and_completion()
    {
        var synthesizer = new SyntheticSpeechSynthesizer();
        var request = new SpeechRequest(
            Guid.NewGuid(),
            0,
            0,
            "Hello there",
            "default",
            1.0,
            CanonicalAudio.Format);
        var events = new List<SpeechSynthesisEvent>();
        await foreach (var item in synthesizer.SynthesizeAsync(request))
        {
            events.Add(item);
        }

        var audio = events.OfType<SpeechAudio>().ToArray();
        Assert.NotEmpty(audio);
        Assert.Equal(1, audio[0].Frame.FrameSequence);
        Assert.Equal(0, audio[0].Frame.SampleOffset);
        for (var index = 1; index < audio.Length; index++)
        {
            var previous = audio[index - 1].Frame;
            var expected = previous.SampleOffset + previous.Data.Length / 2;
            Assert.Equal(expected, audio[index].Frame.SampleOffset);
            Assert.Equal(previous.FrameSequence + 1, audio[index].Frame.FrameSequence);
        }

        Assert.Contains(events, item => item is SpeechTimingMark);
        Assert.IsType<SpeechSynthesisCompleted>(events[^1]);
        Assert.True(synthesizer.Capabilities.TimingMarks);
        Assert.True(synthesizer.Capabilities.StreamingAudio);
    }

    [Fact]
    public async Task OpenAI_posts_pcm_speech_and_frames_bytes()
    {
        var pcm = new byte[CanonicalAudio.MaxFrameBytes + 20];
        var handler = new SpeechHandler(pcm);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/v1/") };
        var synthesizer = new OpenAiSpeechSynthesizer(
            http,
            new SpeechSynthesisProviderOptions
            {
                BaseUrl = "http://127.0.0.1/v1/",
                ApiKey = "test",
                DefaultModel = "tts-1"
            });
        var listed = new List<SpeechSynthesisEvent>();
        await foreach (var item in synthesizer.SynthesizeAsync(
                           new SpeechRequest(Guid.NewGuid(), 0, 0, "Hello", "alloy", 1.0, CanonicalAudio.Format)))
        {
            listed.Add(item);
        }

        Assert.Contains("audio/speech", handler.LastUri, StringComparison.Ordinal);
        Assert.Contains("\"response_format\":\"pcm\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"delay\"", handler.Body, StringComparison.Ordinal);
        Assert.True(listed.OfType<SpeechAudio>().Any());
        Assert.IsType<SpeechSynthesisCompleted>(listed[^1]);
        Assert.False(synthesizer.Capabilities.TimingMarks);
    }

    [Fact]
    public async Task OpenAI_maps_compatible_locale_to_configured_voice()
    {
        var pcm = new byte[CanonicalAudio.MaxFrameBytes];
        var handler = new SpeechHandler(pcm);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/v1/") };
        var synthesizer = new OpenAiSpeechSynthesizer(
            http,
            new SpeechSynthesisProviderOptions
            {
                BaseUrl = "http://127.0.0.1/v1/",
                ApiKey = "test",
                DefaultVoice = "alloy",
                Voices = new Dictionary<string, string> { ["fr-FR"] = "nova" }
            });
        await foreach (var _ in synthesizer.SynthesizeAsync(
                           new SpeechRequest(Guid.NewGuid(), 0, 0, "Bonjour", "alloy", 1.0, CanonicalAudio.Format, "fr")))
        {
        }

        Assert.Contains("\"voice\":\"nova\"", handler.Body, StringComparison.Ordinal);
        Assert.True(synthesizer.CanSynthesize("fr-FR"));
        Assert.False(synthesizer.CanSynthesize("ja-JP"));
    }

    [Fact]
    public async Task OpenAI_fails_closed_when_locale_has_no_configured_voice()
    {
        var handler = new SpeechHandler([]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/v1/") };
        var synthesizer = new OpenAiSpeechSynthesizer(
            http,
            new SpeechSynthesisProviderOptions
            {
                BaseUrl = "http://127.0.0.1/v1/",
                ApiKey = "test",
                Voices = new Dictionary<string, string> { ["fr-FR"] = "nova" }
            });
        var listed = new List<SpeechSynthesisEvent>();
        await foreach (var item in synthesizer.SynthesizeAsync(
                           new SpeechRequest(Guid.NewGuid(), 0, 0, "こんにちは", "alloy", 1.0, CanonicalAudio.Format, "ja-JP")))
        {
            listed.Add(item);
        }

        var failed = Assert.IsType<SpeechSynthesisFailed>(Assert.Single(listed));
        Assert.Equal(ProviderErrorCode.UnsupportedCapability, failed.Failure.Code);
        Assert.Equal("", handler.Body);
    }

    [Fact]
    public async Task OpenAI_without_key_is_voice_unavailable()
    {
        using var http = new HttpClient();
        var synthesizer = new OpenAiSpeechSynthesizer(http, new SpeechSynthesisProviderOptions { ApiKey = null });
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
        {
            await foreach (var _ in synthesizer.SynthesizeAsync(
                               new SpeechRequest(Guid.NewGuid(), 0, 0, "Hi", "alloy", 1.0, CanonicalAudio.Format)))
            {
            }
        });
    }

    [Fact]
    public void Synthetic_di_resolves_synthetic_synthesizer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAgentCoreInfrastructure(FindAgents(), "Synthetic");
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SyntheticSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
        Assert.IsNotType<OpenAiSpeechSynthesizer>(provider.GetRequiredService<ISpeechSynthesizer>());
    }

    [LiveOpenAiTtsFact]
    public void Live_openai_tts_is_opt_in()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        using var http = new HttpClient();
        var synthesizer = new OpenAiSpeechSynthesizer(http, new SpeechSynthesisProviderOptions
        {
            ApiKey = key,
            DefaultModel = "tts-1"
        });
        Assert.True(synthesizer.Capabilities.StreamingAudio);
        Assert.False(synthesizer.Capabilities.TimingMarks);
        Assert.Contains("pcm", OpenAiSpeechPayload.CreateJson("tts-1", "hi", "alloy", 1.0), StringComparison.Ordinal);
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

    private sealed class SpeechHandler(byte[] body) : HttpMessageHandler
    {
        public string LastUri { get; private set; } = "";

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString() ?? "";
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") }
                }
            };
        }
    }
}

public sealed class LiveOpenAiTtsFactAttribute : FactAttribute
{
    public LiveOpenAiTtsFactAttribute()
    {
        var optIn = string.Equals(Environment.GetEnvironmentVariable("AGENTCORE_LIVE_OPENAI_TTS"), "1", StringComparison.Ordinal);
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!optIn)
        {
            Skip = "Opt-in AGENTCORE_LIVE_OPENAI_TTS=1 is required; default suites must not call OpenAI.";
        }
        else if (string.IsNullOrWhiteSpace(key))
        {
            Skip = "OPENAI_API_KEY is missing.";
        }
    }
}
