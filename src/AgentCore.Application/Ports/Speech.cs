namespace AgentCore.Application.Ports;

public sealed record AudioFormat(string Encoding, int SampleRateHz, int Channels);

public sealed record AudioFrame(long FrameSequence, long SampleOffset, ReadOnlyMemory<byte> Data);

public sealed record RecognitionCapabilities(
    bool StreamingAudio,
    bool PartialTranscripts,
    bool SpeechBoundaryEvents,
    bool Cancellation);

public sealed record SynthesisCapabilities(
    bool StreamingAudio,
    bool TimingMarks,
    bool Cancellation,
    bool VoiceSelection,
    bool SpeakingRate,
    IReadOnlyList<AudioFormat> SupportedFormats);

public static class CanonicalAudio
{
    public static AudioFormat Format { get; } = new("pcm_s16le", 24000, 1);
}
