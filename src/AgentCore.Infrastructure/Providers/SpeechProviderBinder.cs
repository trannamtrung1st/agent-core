using AgentCore.Infrastructure.Providers.OpenAI;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Providers;

public static class SpeechProviderBinder
{
    public static SpeechProviderSelection Bind(IConfiguration configuration)
    {
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

    private static SpeechRecognitionProviderOptions BindRecognition(IConfiguration configuration)
    {
        var nested = configuration.GetSection("Providers:Speech:Recognition");
        var map = configuration.GetSection("Providers:SpeechRecognizers:primary-stt");
        if (HasAdapter(nested))
        {
            return BindRecognitionFrom(nested);
        }

        if (HasAdapter(map))
        {
            return BindRecognitionFrom(map);
        }

        return new SpeechRecognitionProviderOptions { Adapter = SpeechAdapterCatalog.Synthetic };
    }

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

    private static SpeechSynthesisProviderOptions BindSynthesis(IConfiguration configuration)
    {
        var nested = configuration.GetSection("Providers:Speech:Synthesis");
        var map = configuration.GetSection("Providers:SpeechSynthesizers:primary-tts");
        if (HasAdapter(nested))
        {
            return BindSynthesisFrom(nested);
        }

        if (HasAdapter(map))
        {
            return BindSynthesisFrom(map);
        }

        return new SpeechSynthesisProviderOptions { Adapter = SpeechAdapterCatalog.Synthetic };
    }

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

    private static bool HasAdapter(IConfigurationSection section) =>
        section.Exists() && !string.IsNullOrWhiteSpace(section["Adapter"]);

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
