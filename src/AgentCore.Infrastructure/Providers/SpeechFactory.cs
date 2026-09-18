using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Providers;

public sealed class SpeechResolution
{
    public required EffectiveSpeechPlan Plan { get; init; }
    public ISpeechRecognizer? Recognizer { get; init; }
    public ISpeechSynthesizer? Synthesizer { get; init; }
    public ISpeechLocaleSupport LocaleSupport { get; init; } = UnrestrictedSpeechLocaleSupport.Instance;
}

internal static class SpeechFactory
{
    public static SpeechResolution Create(IServiceProvider provider, SpeechProvidersOptions? options)
    {
        options ??= provider.GetService<SpeechProvidersOptions>() ?? new SpeechProvidersOptions();
        var recognition = ResolveRecognition(provider, options.Recognition);
        var synthesis = ResolveSynthesis(provider, options.Synthesis);
        return new SpeechResolution
        {
            Recognizer = recognition.Adapter,
            Synthesizer = synthesis.Adapter,
            Plan = new EffectiveSpeechPlan(
                recognition.Transport,
                synthesis.Transport,
                recognition.Resolvable,
                synthesis.Resolvable,
                recognition.Capabilities,
                synthesis.Capabilities),
            LocaleSupport = new CompositeSpeechLocaleSupport(
                LocaleSupportOf(recognition.Adapter),
                LocaleSupportOf(synthesis.Adapter))
        };
    }

    private static ResolvedRecognition ResolveRecognition(
        IServiceProvider provider,
        SpeechRecognitionProviderOptions options)
    {
        if (SpeechAdapterCatalog.IsBrowser(options.Adapter))
        {
            return new ResolvedRecognition(
                null,
                SpeechTransport.ClientTranscript,
                true,
                ClientSpeechCapabilities.Recognition);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.Synthetic))
        {
            var adapter = new SyntheticSpeechRecognizer();
            return new ResolvedRecognition(adapter, SpeechTransport.ServerAudio, true, adapter.Capabilities);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.OpenAICompatibleBatch))
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                return new ResolvedRecognition(null, SpeechTransport.ServerAudio, false, null);
            }

            var http = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(OpenAICompatibleBatchSpeechRecognizer.HttpClientName);
            var adapter = new OpenAICompatibleBatchSpeechRecognizer(http, options);
            return new ResolvedRecognition(adapter, SpeechTransport.ServerAudio, true, adapter.Capabilities);
        }

        // OpenAI realtime STT remains a deferred no-op and is not selectable.
        return new ResolvedRecognition(null, SpeechTransport.ServerAudio, false, null);
    }

    private static ResolvedSynthesis ResolveSynthesis(
        IServiceProvider provider,
        SpeechSynthesisProviderOptions options)
    {
        if (SpeechAdapterCatalog.IsBrowser(options.Adapter))
        {
            return new ResolvedSynthesis(
                null,
                SpeechTransport.ClientSpeech,
                true,
                ClientSpeechCapabilities.Synthesis);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.Synthetic))
        {
            var adapter = new SyntheticSpeechSynthesizer();
            return new ResolvedSynthesis(adapter, SpeechTransport.ServerAudio, true, adapter.Capabilities);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.OpenAI))
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                return new ResolvedSynthesis(null, SpeechTransport.ServerAudio, false, null);
            }

            var http = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(OpenAiSpeechSynthesizer.HttpClientName);
            var adapter = new OpenAiSpeechSynthesizer(http, options);
            return new ResolvedSynthesis(adapter, SpeechTransport.ServerAudio, true, adapter.Capabilities);
        }

        return new ResolvedSynthesis(null, SpeechTransport.ServerAudio, false, null);
    }

    private readonly record struct ResolvedRecognition(
        ISpeechRecognizer? Adapter,
        string Transport,
        bool Resolvable,
        RecognitionCapabilities? Capabilities);

    private readonly record struct ResolvedSynthesis(
        ISpeechSynthesizer? Adapter,
        string Transport,
        bool Resolvable,
        SynthesisCapabilities? Capabilities);

    private static ISpeechLocaleSupport LocaleSupportOf(object? adapter) =>
        adapter as ISpeechLocaleSupport ?? UnrestrictedSpeechLocaleSupport.Instance;

    private sealed class CompositeSpeechLocaleSupport(
        ISpeechLocaleSupport recognition,
        ISpeechLocaleSupport synthesis) : ISpeechLocaleSupport
    {
        public bool CanRecognize(string locale) => recognition.CanRecognize(locale);

        public bool CanSynthesize(string locale) => synthesis.CanSynthesize(locale);
    }
}
