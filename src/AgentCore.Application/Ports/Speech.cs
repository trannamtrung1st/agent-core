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
    public const int SampleRateHz = 24000;
    public const int FrameSamples20Ms = 480;
    public const int MaxFrameBytes = 1920;
    public static AudioFormat Format { get; } = new("pcm_s16le", SampleRateHz, 1);
}

public sealed record RecognitionOptions(AudioFormat Format, string Language);

public enum SpeechBoundary { Started, Ended }

public interface ISpeechRecognizer
{
    RecognitionCapabilities Capabilities { get; }

    ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default);
}

public interface ISpeechRecognitionSession : IAsyncDisposable
{
    ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    ValueTask ObserveBoundaryAsync(Guid utteranceId, SpeechBoundary boundary, CancellationToken cancellationToken = default);
    ValueTask CompleteInputAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}
