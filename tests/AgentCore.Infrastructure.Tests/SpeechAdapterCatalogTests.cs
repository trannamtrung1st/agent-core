using AgentCore.Infrastructure.Providers;

namespace AgentCore.Infrastructure.Tests;

public sealed class SpeechAdapterCatalogTests
{
    [Theory]
    [InlineData("Synthetic")]
    [InlineData("Browser")]
    [InlineData("OpenAI")]
    [InlineData("OpenAICompatibleBatch")]
    public void Recognition_names_after_p1_are_known(string adapter) =>
        Assert.True(SpeechAdapterCatalog.IsKnownRecognition(adapter));

    [Theory]
    [InlineData("Synthetic")]
    [InlineData("Browser")]
    [InlineData("OpenAI")]
    public void Synthesis_names_after_p1_are_known(string adapter) =>
        Assert.True(SpeechAdapterCatalog.IsKnownSynthesis(adapter));

    [Fact]
    public void Browser_never_requires_a_hosted_api_key()
    {
        Assert.False(SpeechAdapterCatalog.RecognitionRequiresHostedApiKey("Browser"));
        Assert.False(SpeechAdapterCatalog.SynthesisRequiresHostedApiKey("Browser"));
        Assert.True(SpeechAdapterCatalog.IsBrowser("browser"));
    }

    [Fact]
    public void Hosted_openai_paths_require_a_key_only_when_that_path_is_used()
    {
        Assert.True(SpeechAdapterCatalog.RecognitionRequiresHostedApiKey("OpenAI"));
        Assert.True(SpeechAdapterCatalog.RecognitionRequiresHostedApiKey("OpenAICompatibleBatch"));
        Assert.True(SpeechAdapterCatalog.SynthesisRequiresHostedApiKey("OpenAI"));
        Assert.False(SpeechAdapterCatalog.RecognitionRequiresHostedApiKey("Synthetic"));
        Assert.False(SpeechAdapterCatalog.SynthesisRequiresHostedApiKey("Synthetic"));
    }

    [Fact]
    public void Unknown_adapters_are_not_known_but_are_not_synthetic_defaults()
    {
        Assert.False(SpeechAdapterCatalog.IsKnownRecognition("NotAThing"));
        Assert.False(SpeechAdapterCatalog.IsKnownSynthesis("OpenAICompatibleSpeech"));
    }
}
