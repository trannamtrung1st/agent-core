using AgentCore.Application.Observability;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class HostingTopologyOptionsTests
{
    [Fact]
    public void Hosted_hybrid_and_on_prem_shapes_bind_without_secrets()
    {
        var hosted = Build(new Dictionary<string, string?>
        {
            ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = "https://openrouter.ai/api/v1/",
            ["Providers:LanguageModels:primary-llm:DefaultModel"] = "operator-fixed-model",
            ["Providers:SpeechRecognizers:primary-stt:Adapter"] = "OpenAI",
            ["Providers:SpeechSynthesizers:primary-tts:Adapter"] = "OpenAI",
            ["Observability:TimelineCapacity"] = "64",
            ["Observability:LogConversationContent"] = "false",
            ["Observability:OtlpEnabled"] = "false",
            ["Hosting:BindUrl"] = "http://127.0.0.1:5080",
            ["Hosting:AllowedOrigins:0"] = "http://127.0.0.1:5173"
        });
        var hybrid = Build(new Dictionary<string, string?>
        {
            ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = "https://openrouter.ai/api/v1/",
            ["Providers:SpeechRecognizers:primary-stt:Adapter"] = "Local",
            ["Providers:SpeechRecognizers:primary-stt:BaseUrl"] = "http://127.0.0.1:9000/v1/",
            ["Providers:SpeechSynthesizers:primary-tts:Adapter"] = "Local",
            ["Providers:SpeechSynthesizers:primary-tts:BaseUrl"] = "http://127.0.0.1:9001/v1/"
        });
        var onPrem = Build(new Dictionary<string, string?>
        {
            ["Providers:LanguageModels:primary-llm:Adapter"] = "OpenAICompatible",
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = "http://127.0.0.1:8000/v1/",
            ["Providers:LanguageModels:primary-llm:DefaultModel"] = "local-model",
            ["Providers:SpeechRecognizers:primary-stt:Adapter"] = "Local",
            ["Providers:SpeechSynthesizers:primary-tts:Adapter"] = "Local",
            ["Observability:OtlpEnabled"] = "false"
        });

        Assert.Equal("OpenAICompatible", hosted.GetSection("Providers:LanguageModels:primary-llm").Get<LanguageModelProviderOptions>()!.Adapter);
        Assert.Contains("openrouter.ai", hosted["Providers:LanguageModels:primary-llm:BaseUrl"], StringComparison.Ordinal);
        Assert.Equal("Local", hybrid["Providers:SpeechRecognizers:primary-stt:Adapter"]);
        Assert.StartsWith("http://127.0.0.1:8000", onPrem["Providers:LanguageModels:primary-llm:BaseUrl"]);
        Assert.Null(hosted["Providers:LanguageModels:primary-llm:ApiKey"]);
        var observability = hosted.GetSection("Observability").Get<ObservabilityOptions>();
        Assert.NotNull(observability);
        Assert.InRange(observability.TimelineCapacity, 1, 1000);
        Assert.False(observability.OtlpEnabled);
        Assert.False(observability.LogConversationContent);
        var hosting = hosted.GetSection("Hosting").Get<HostingOptions>();
        Assert.Equal("http://127.0.0.1:5080", hosting!.BindUrl);
    }

    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
