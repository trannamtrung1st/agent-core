using System.Threading.Channels;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Audio;

public abstract record IngressMessage;

public sealed record IngressAudio(AudioFrame Frame) : IngressMessage;

public sealed record IngressBoundary(Guid UtteranceId, SpeechBoundary Boundary, double? ActivityScore) : IngressMessage;

public sealed class AudioIngress
{
    private readonly Channel<IngressMessage> _channel = Channel.CreateBounded<IngressMessage>(
        new BoundedChannelOptions(25)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public bool TryWrite(IngressMessage message) => _channel.Writer.TryWrite(message);

    public ChannelReader<IngressMessage> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();
}
