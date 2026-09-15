using AgentCore.Application.Audio;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public sealed class SyntheticSpeechSynthesizer : ISpeechSynthesizer
{
    public const int SamplesPerCharacter = CanonicalAudio.FrameSamples20Ms;

    public SynthesisCapabilities Capabilities { get; } = new(
        StreamingAudio: true,
        TimingMarks: true,
        Cancellation: true,
        VoiceSelection: false,
        SpeakingRate: true,
        SupportedFormats: [CanonicalAudio.Format]);

    public async IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
        SpeechRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = request.Voice;
        var text = request.Text;
        var units = Math.Max(1, text.Trim().Length);
        var totalSamples = (long)units * SamplesPerCharacter;
        long offset = 0;
        long frameSequence = 1;
        var phase = 0;
        while (offset < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = (int)Math.Min(CanonicalAudio.FrameSamples20Ms, totalSamples - offset);
            yield return new SpeechAudio(new AudioFrame(frameSequence, offset, PcmCodec.ToneFrame(samples, phase)));
            phase += samples;
            offset += samples;
            frameSequence++;
            await Task.Yield();
        }

        var cursor = 0;
        while (cursor < text.Length)
        {
            var end = cursor;
            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            if (end <= cursor)
            {
                break;
            }

            var markSamples = text.Length == 0 ? offset : offset * end / text.Length;
            yield return new SpeechTimingMark(end, markSamples);
            cursor = end;
        }

        if (text.Length > 0)
        {
            yield return new SpeechTimingMark(text.Length, offset);
        }

        yield return new SpeechSynthesisCompleted(offset);
    }
}
