using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class SpeechProviderBinderTests
{
    [Fact]
    public void Defaults_to_independent_synthetic_recognition_and_synthesis()
    {
        var bound = SpeechProviderBinder.Bind(Build([]));
        Assert.Equal("Synthetic", bound.Options.Recognition.Adapter);
        Assert.Equal("Synthetic", bound.Options.Synthesis.Adapter);
        Assert.True(bound.RecognitionKnown);
        Assert.True(bound.SynthesisKnown);
        Assert.False(bound.RecognitionMissingHostedKey);
        Assert.False(bound.SynthesisMissingHostedKey);
    }

    [Fact]
    public void Nested_speech_adapters_are_independent()
    {
        var bound = SpeechProviderBinder.Bind(Build(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "Browser",
            ["Providers:Speech:Synthesis:Adapter"] = "OpenAI"
        }));
        Assert.Equal("Browser", bound.Options.Recognition.Adapter);
        Assert.Equal("OpenAI", bound.Options.Synthesis.Adapter);
        Assert.True(bound.RecognitionKnown);
        Assert.True(bound.SynthesisKnown);
        Assert.False(bound.RecognitionMissingHostedKey);
        Assert.True(bound.SynthesisMissingHostedKey);
        Assert.True(string.IsNullOrWhiteSpace(bound.Options.Recognition.ApiKey));
        Assert.True(string.IsNullOrWhiteSpace(bound.Options.Synthesis.ApiKey));
    }

    [Theory]
    [InlineData("Synthetic", "Synthetic")]
    [InlineData("Browser", "Browser")]
    [InlineData("Browser", "OpenAI")]
    [InlineData("OpenAICompatibleBatch", "Browser")]
    [InlineData("OpenAI", "OpenAI")]
    public void Mixed_combinations_bind_without_keys(string recognition, string synthesis)
    {
        var bound = SpeechProviderBinder.Bind(Build(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = recognition,
            ["Providers:Speech:Synthesis:Adapter"] = synthesis
        }));
        Assert.Equal(recognition, bound.Options.Recognition.Adapter);
        Assert.Equal(synthesis, bound.Options.Synthesis.Adapter);
        Assert.True(bound.RecognitionKnown);
        Assert.True(bound.SynthesisKnown);
    }

    [Theory]
    [InlineData("Providers:SpeechRecognizers:primary-stt:Adapter")]
    [InlineData("Providers:SpeechSynthesizers:primary-tts:Adapter")]
    public void Retired_speech_maps_fail_even_with_canonical_configuration(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SpeechProviderBinder.Bind(Build(new()
        {
            [key] = "private-sentinel", ["Providers:Speech:Recognition:Adapter"] = "Browser"
        })));
        Assert.Contains(key, error.Message); Assert.DoesNotContain("private-sentinel", error.Message);
    }

    [Fact]
    public void OpenAI_api_key_fills_hosted_speech_only()
    {
        var bound = SpeechProviderBinder.Bind(Build(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "OpenAI",
            ["Providers:Speech:Synthesis:Adapter"] = "Browser",
            ["OPENAI_API_KEY"] = "hosted-secret"
        }));
        Assert.Equal("hosted-secret", bound.Options.Recognition.ApiKey);
        Assert.True(string.IsNullOrWhiteSpace(bound.Options.Synthesis.ApiKey));
        Assert.False(bound.RecognitionMissingHostedKey);
    }

    [Fact]
    public void Unknown_speech_adapter_does_not_throw()
    {
        var bound = SpeechProviderBinder.Bind(Build(new Dictionary<string, string?>
        {
            ["Providers:Speech:Recognition:Adapter"] = "NotAThing",
            ["Providers:Speech:Synthesis:Adapter"] = "Local"
        }));
        Assert.Equal("NotAThing", bound.Options.Recognition.Adapter);
        Assert.Equal("Local", bound.Options.Synthesis.Adapter);
        Assert.False(bound.RecognitionKnown);
        Assert.False(bound.SynthesisKnown);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
