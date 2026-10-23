using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed class VoiceAvailability
{
    public EffectiveSpeechPlan? Plan { get; init; }
    public bool SpeechAdaptersResolved { get; init; } = true;
    public ISpeechLocaleSupport LocaleSupport { get; init; } = UnrestrictedSpeechLocaleSupport.Instance;

    public EffectiveSpeechPlan EffectivePlan =>
        Plan ?? new EffectiveSpeechPlan(
            SpeechTransport.ServerAudio,
            SpeechTransport.ServerAudio,
            SpeechAdaptersResolved,
            SpeechAdaptersResolved,
            RecognitionCapabilities: null,
            SynthesisCapabilities: null);

    public bool IsAvailable(AgentDefinition definition, string? locale = null)
    {
        if (!definition.Voice.Enabled
            || string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechRecognizer)
            || string.IsNullOrWhiteSpace(definition.ProviderPreferences.SpeechSynthesizer)
            || !EffectivePlan.RecognitionResolvable
            || !EffectivePlan.SynthesisResolvable)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(locale))
        {
            return true;
        }

        return LocaleSupport.CanRecognize(locale) && LocaleSupport.CanSynthesize(locale);
    }
}
