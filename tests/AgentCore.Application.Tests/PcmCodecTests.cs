using AgentCore.Application.Audio;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests;

public sealed class PcmCodecTests
{
    [Fact]
    public void Reads_little_endian_pcm16()
    {
        var frame = new AudioFrame(1, 0, new byte[] { 0x34, 0x12, 0x00, 0x80 });
        PcmCodec.ValidateFrame(frame);
        Assert.Equal(2, PcmCodec.SampleCount(frame.Data));
        Assert.Equal(0x1234, PcmCodec.ReadSample(frame.Data.Span, 0));
        Assert.Equal(unchecked((short)0x8000), PcmCodec.ReadSample(frame.Data.Span, 1));
    }

    [Fact]
    public void Rejects_oversized_and_odd_frames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PcmCodec.ValidateFrame(new AudioFrame(1, 0, new byte[1922])));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PcmCodec.ValidateFrame(new AudioFrame(1, 0, new byte[] { 1 })));
    }

    [Fact]
    public void Detects_sample_offset_gaps()
    {
        var next = new AudioFrame(2, 480, new byte[960]);
        Assert.True(PcmCodec.IsContiguous(480, next));
        Assert.False(PcmCodec.IsContiguous(0, next));
    }
}
