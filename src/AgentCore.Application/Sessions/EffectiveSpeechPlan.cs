using AgentCore.Application.Ports;

namespace AgentCore.Application.Sessions;

public static class SpeechTransport
{
    public const string ServerAudio = "serverAudio";
    public const string ClientTranscript = "clientTranscript";
    public const string ClientSpeech = "clientSpeech";
}

public static class ClientSpeechCapabilities
{
    public static RecognitionCapabilities Recognition { get; } = new(
        StreamingAudio: false,
        PartialTranscripts: true,
        SpeechBoundaryEvents: true,
        Cancellation: true);

    public static SynthesisCapabilities Synthesis { get; } = new(
        StreamingAudio: false,
        TimingMarks: false,
        Cancellation: true,
        VoiceSelection: true,
        SpeakingRate: true,
        SupportedFormats: []);
}

public sealed record EffectiveSpeechPlan(
    string InputTransport,
    string OutputTransport,
    bool RecognitionResolvable,
    bool SynthesisResolvable,
    RecognitionCapabilities? RecognitionCapabilities,
    SynthesisCapabilities? SynthesisCapabilities);
