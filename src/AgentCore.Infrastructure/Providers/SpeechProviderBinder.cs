using AgentCore.Infrastructure.Providers.OpenAI;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Providers;

public static class SpeechProviderBinder
{
    public static SpeechProviderSelection Bind(IConfiguration configuration)
    {
        Configuration.ConfigurationCutover.Validate(configuration);
        var recognition = BindRecognition(configuration);
        var synthesis = BindSynthesis(configuration);
        ApplyOpenAiKey(configuration, recognition, synthesis);
        var options = new SpeechProvidersOptions
        {
            Recognition = recognition,
            Synthesis = synthesis
        };
        return new SpeechProviderSelection
        {
            Options = options,
            RecognitionKnown = SpeechAdapterCatalog.IsKnownRecognition(recognition.Adapter),
            SynthesisKnown = SpeechAdapterCatalog.IsKnownSynthesis(synthesis.Adapter),
            RecognitionMissingHostedKey = SpeechAdapterCatalog.RecognitionRequiresHostedApiKey(recognition.Adapter)
                && string.IsNullOrWhiteSpace(recognition.ApiKey),
            SynthesisMissingHostedKey = SpeechAdapterCatalog.SynthesisRequiresHostedApiKey(synthesis.Adapter)
                && string.IsNullOrWhiteSpace(synthesis.ApiKey)
        };
    }

    private static SpeechRecognitionProviderOptions BindRecognition(IConfiguration configuration) =>
        BindRecognitionFrom(configuration.GetSection("Providers:Speech:Recognition"));

    private static SpeechRecognitionProviderOptions BindRecognitionFrom(IConfigurationSection section)
    {
        var options = section.Get<SpeechRecognitionProviderOptions>() ?? new SpeechRecognitionProviderOptions();
        options.Adapter = section["Adapter"] ?? options.Adapter;
        if (string.IsNullOrEmpty(options.ApiKey) && !string.IsNullOrEmpty(section["ApiKey"]))
        {
            options.ApiKey = section["ApiKey"];
        }

        return options;
    }

    private static SpeechSynthesisProviderOptions BindSynthesis(IConfiguration configuration) =>
        BindSynthesisFrom(configuration.GetSection("Providers:Speech:Synthesis"));

    private static SpeechSynthesisProviderOptions BindSynthesisFrom(IConfigurationSection section)
    {
        var options = section.Get<SpeechSynthesisProviderOptions>() ?? new SpeechSynthesisProviderOptions();
        options.Adapter = section["Adapter"] ?? options.Adapter;
        if (string.IsNullOrEmpty(options.ApiKey) && !string.IsNullOrEmpty(section["ApiKey"]))
        {
            options.ApiKey = section["ApiKey"];
        }

        return options;
    }

    private static void ApplyOpenAiKey(
        IConfiguration configuration,
        SpeechRecognitionProviderOptions recognition,
        SpeechSynthesisProviderOptions synthesis)
    {
        var key = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(recognition.ApiKey)
            && SpeechAdapterCatalog.RecognitionRequiresHostedApiKey(recognition.Adapter))
        {
            recognition.ApiKey = key;
        }

        if (string.IsNullOrWhiteSpace(synthesis.ApiKey)
            && SpeechAdapterCatalog.SynthesisRequiresHostedApiKey(synthesis.Adapter))
        {
            synthesis.ApiKey = key;
        }
    }
}
