using System.Threading.Channels;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public sealed class SyntheticSpeechRecognizer : ISpeechRecognizer
{
    private readonly IReadOnlyList<string> _utterances;
    private int _next;

    public SyntheticSpeechRecognizer(
        IReadOnlyList<string>? utterances = null,
        RecognitionCapabilities? capabilities = null)
    {
        _utterances = utterances ?? ["Hello there"];
        Capabilities = capabilities ?? new RecognitionCapabilities(true, true, true, true);
    }

    public RecognitionCapabilities Capabilities { get; }

    public ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = options;
        return ValueTask.FromResult<ISpeechRecognitionSession>(new Session(this));
    }

    private string NextUtterance()
    {
        var text = _utterances[Math.Min(_next, _utterances.Count - 1)];
        if (_next < _utterances.Count - 1)
        {
            _next++;
        }

        return text;
    }

    private sealed class Session(SyntheticSpeechRecognizer owner) : ISpeechRecognitionSession
    {
        private readonly Channel<SpeechRecognitionEvent> _events = Channel.CreateUnbounded<SpeechRecognitionEvent>();
        private int _partials;

        public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            PcmTouch(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask ObserveBoundaryAsync(
            Guid utteranceId,
            SpeechBoundary boundary,
            CancellationToken cancellationToken = default)
        {
            if (boundary == SpeechBoundary.Ended)
            {
                var text = owner.NextUtterance();
                if (owner.Capabilities.PartialTranscripts)
                {
                    _ = _events.Writer.TryWrite(new SpeechPartial(utteranceId, ++_partials, text, 0.9));
                }

                _ = _events.Writer.TryWrite(new SpeechFinal(utteranceId, text, 0.9));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default)
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static void PcmTouch(AudioFrame frame) => _ = frame.Data.Length;
    }
}
