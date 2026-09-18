using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SpeechConfigurationHostTests
{
    [Fact]
    public async Task Synthetic_text_session_survives_unknown_speech_adapters()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "NotAThing",
            ["Providers:Speech:Synthesis:Adapter"] = "Local"
        });

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var speech = factory.Services.GetRequiredService<SpeechProvidersOptions>();
        Assert.Equal("NotAThing", speech.Recognition.Adapter);
        Assert.Equal("Local", speech.Synthesis.Adapter);
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.Null(resolution.Recognizer);
        Assert.Null(resolution.Synthesizer);
        Assert.False(resolution.Plan.RecognitionResolvable);

        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Real_text_session_starts_without_openai_speech_keys()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["AgentCore:Profile"] = "Real",
            ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = "https://openrouter.ai/api/v1/",
            ["Providers:LanguageModels:primary-llm:DefaultModel"] = "openai/gpt-4o-mini-2024-07-18",
            ["Providers:LanguageModels:primary-llm:ApiKey"] = "test-text-only",
            ["Providers:Speech:Recognition:Adapter"] = "OpenAI",
            ["Providers:Speech:Synthesis:Adapter"] = "OpenAI"
        });

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var body = await health.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Real", body!.Profile);

        var speech = factory.Services.GetRequiredService<SpeechProvidersOptions>();
        Assert.Equal("OpenAI", speech.Recognition.Adapter);
        Assert.Equal("OpenAI", speech.Synthesis.Adapter);
        Assert.True(string.IsNullOrWhiteSpace(speech.Recognition.ApiKey));
        Assert.True(string.IsNullOrWhiteSpace(speech.Synthesis.ApiKey));
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.Null(resolution.Recognizer);
        Assert.Null(resolution.Synthesizer);
        Assert.False(resolution.Plan.RecognitionResolvable);
        Assert.False(resolution.Plan.SynthesisResolvable);

        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("text", view!.Mode);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Browser_speech_selection_needs_no_api_key_and_keeps_synthetic_voice_create()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "Browser",
            ["Providers:Speech:Synthesis:Adapter"] = "Browser"
        });

        var speech = factory.Services.GetRequiredService<SpeechProvidersOptions>();
        Assert.Equal("Browser", speech.Recognition.Adapter);
        Assert.Equal("Browser", speech.Synthesis.Adapter);
        Assert.True(string.IsNullOrWhiteSpace(speech.Recognition.ApiKey));
        Assert.True(string.IsNullOrWhiteSpace(speech.Synthesis.ApiKey));
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.Null(resolution.Recognizer);
        Assert.Null(resolution.Synthesizer);
        Assert.Equal(SpeechTransport.ClientTranscript, resolution.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ClientSpeech, resolution.Plan.OutputTransport);
        Assert.True(resolution.Plan.RecognitionResolvable);

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "voice"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        Assert.Contains(agents!.Agents, agent => agent.Id == "examiner" && agent.VoiceAvailable);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Default_synthetic_host_resolves_config_synthetic_adapters()
    {
        await using var factory = new AgentCoreApiFactory();
        var options = factory.Services.GetRequiredService<SpeechProvidersOptions>();
        Assert.Equal("Synthetic", options.Recognition.Adapter);
        Assert.Equal("Synthetic", options.Synthesis.Adapter);
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.IsType<AgentCore.Infrastructure.Providers.Synthetic.SyntheticSpeechRecognizer>(resolution.Recognizer);
        Assert.IsType<AgentCore.Infrastructure.Providers.Synthetic.SyntheticSpeechSynthesizer>(resolution.Synthesizer);
        Assert.Equal(SpeechTransport.ServerAudio, resolution.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, resolution.Plan.OutputTransport);

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var voice = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "voice"));
        Assert.Equal(HttpStatusCode.Created, voice.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Batch_stt_and_browser_tts_do_not_start_paid_clients()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "OpenAICompatibleBatch",
            ["Providers:Speech:Recognition:ApiKey"] = "not-a-live-key",
            ["Providers:Speech:Synthesis:Adapter"] = "Browser"
        });
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.Null(resolution.Recognizer);
        Assert.Null(resolution.Synthesizer);
        Assert.Equal(SpeechTransport.ServerAudio, resolution.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ClientSpeech, resolution.Plan.OutputTransport);
        Assert.False(resolution.Plan.RecognitionResolvable);
        Assert.True(resolution.Plan.SynthesisResolvable);

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var text = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, text.StatusCode);
        var voice = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "voice"));
        Assert.Equal(HttpStatusCode.Conflict, voice.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Browser_stt_and_openai_tts_with_dummy_key_do_not_start_paid_clients()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "Browser",
            ["Providers:Speech:Synthesis:Adapter"] = "OpenAI",
            ["OPENAI_API_KEY"] = "not-a-live-key"
        });
        var resolution = factory.Services.GetRequiredService<SpeechResolution>();
        Assert.Null(resolution.Recognizer);
        Assert.Null(resolution.Synthesizer);
        Assert.Equal(SpeechTransport.ClientTranscript, resolution.Plan.InputTransport);
        Assert.Equal(SpeechTransport.ServerAudio, resolution.Plan.OutputTransport);
        Assert.False(string.IsNullOrWhiteSpace(factory.Services.GetRequiredService<SpeechProvidersOptions>().Synthesis.ApiKey));

        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var text = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, text.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Gated_hosted_speech_does_not_advertise_voice_available()
    {
        await using var factory = new SpeechHostFactory(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "OpenAI",
            ["Providers:Speech:Synthesis:Adapter"] = "OpenAI"
        });
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        Assert.Contains(agents!.Agents, agent => agent.Id == "examiner" && !agent.VoiceAvailable);
    }

    private sealed class SpeechHostFactory(Dictionary<string, string?> extra) : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => extra;
    }
}
