using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers.OpenAI;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Providers;

public sealed class SpeechResolution
{
    public required EffectiveSpeechPlan Plan { get; init; }
    public ISpeechRecognizer? Recognizer { get; init; }
    public ISpeechSynthesizer? Synthesizer { get; init; }
}

internal static class SpeechFactory
{
    public static SpeechResolution Create(IServiceProvider provider, SpeechProvidersOptions? options)
    {
        options ??= provider.GetService<SpeechProvidersOptions>() ?? new SpeechProvidersOptions();
        var recognition = ResolveRecognition(options.Recognition);
        var synthesis = ResolveSynthesis(options.Synthesis);
        return new SpeechResolution
        {
            Recognizer = recognition.Adapter,
            Synthesizer = synthesis.Adapter,
            Plan = new EffectiveSpeechPlan(
                recognition.Transport,
                synthesis.Transport,
                recognition.Resolvable,
                synthesis.Resolvable,
                recognition.Adapter?.Capabilities,
                synthesis.Adapter?.Capabilities)
        };
    }

    private static ResolvedRecognition ResolveRecognition(SpeechRecognitionProviderOptions options)
    {
        if (SpeechAdapterCatalog.IsBrowser(options.Adapter))
        {
            return new ResolvedRecognition(null, SpeechTransport.ClientTranscript, true);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.Synthetic))
        {
            var adapter = new SyntheticSpeechRecognizer();
            return new ResolvedRecognition(adapter, SpeechTransport.ServerAudio, true);
        }

        // OpenAI realtime STT is a deferred no-op and must not be selectable.
        // OpenAICompatibleBatch stays gated until hosted speech wiring.
        return new ResolvedRecognition(null, SpeechTransport.ServerAudio, false);
    }

    private static ResolvedSynthesis ResolveSynthesis(SpeechSynthesisProviderOptions options)
    {
        if (SpeechAdapterCatalog.IsBrowser(options.Adapter))
        {
            return new ResolvedSynthesis(null, SpeechTransport.ClientSpeech, true);
        }

        if (SpeechAdapterCatalog.EqualsName(options.Adapter, SpeechAdapterCatalog.Synthetic))
        {
            var adapter = new SyntheticSpeechSynthesizer();
            return new ResolvedSynthesis(adapter, SpeechTransport.ServerAudio, true);
        }

        // OpenAI TTS stays gated until hosted speech wiring.
        return new ResolvedSynthesis(null, SpeechTransport.ServerAudio, false);
    }

    private readonly record struct ResolvedRecognition(
        ISpeechRecognizer? Adapter,
        string Transport,
        bool Resolvable);

    private readonly record struct ResolvedSynthesis(
        ISpeechSynthesizer? Adapter,
        string Transport,
        bool Resolvable);
}
