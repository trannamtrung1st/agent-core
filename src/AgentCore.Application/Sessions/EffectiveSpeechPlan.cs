using AgentCore.Application.Ports;

namespace AgentCore.Application.Sessions;

public static class SpeechTransport
{
    public const string ServerAudio = "serverAudio";
    public const string ClientTranscript = "clientTranscript";
    public const string ClientSpeech = "clientSpeech";
}

public sealed record EffectiveSpeechPlan(
    string InputTransport,
    string OutputTransport,
    bool RecognitionResolvable,
    bool SynthesisResolvable,
    RecognitionCapabilities? RecognitionCapabilities,
    SynthesisCapabilities? SynthesisCapabilities);
