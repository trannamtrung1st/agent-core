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
        Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    private sealed class SpeechHostFactory(Dictionary<string, string?> extra) : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => extra;
    }
}
