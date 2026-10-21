using System.Threading.Channels;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Audio;

public abstract record IngressMessage;

public sealed record IngressAudio(AudioFrame Frame) : IngressMessage;

public sealed record IngressBoundary(Guid UtteranceId, SpeechBoundary Boundary, double? ActivityScore) : IngressMessage;

public sealed class AudioIngress
{
    public const int MaxQueuedSamples = CanonicalAudio.SampleRateHz / 2;
    private readonly object _gate = new();
    private int _queuedSamples;
    private readonly Channel<IngressMessage> _channel = Channel.CreateBounded<IngressMessage>(
        new BoundedChannelOptions(25)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public bool TryWrite(IngressMessage message)
    {
        lock (_gate)
        {
            var samples = message is IngressAudio audio ? PcmCodec.SampleCount(audio.Frame.Data) : 0;
            if (samples > MaxQueuedSamples - _queuedSamples || !_channel.Writer.TryWrite(message)) return false;
            _queuedSamples += samples; return true;
        }
    }

    public ChannelReader<IngressMessage> Reader => new AccountedReader(this);

    private sealed class AccountedReader(AudioIngress owner) : ChannelReader<IngressMessage>
    {
        public override Task Completion => owner._channel.Reader.Completion;
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => owner._channel.Reader.WaitToReadAsync(cancellationToken);
        public override bool TryRead(out IngressMessage item)
        {
            lock (owner._gate)
            {
                if (!owner._channel.Reader.TryRead(out item!)) return false;
                if (item is IngressAudio audio) owner._queuedSamples -= PcmCodec.SampleCount(audio.Frame.Data);
                return true;
            }
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
