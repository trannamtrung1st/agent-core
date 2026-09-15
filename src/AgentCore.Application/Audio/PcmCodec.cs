using AgentCore.Application.Ports;

namespace AgentCore.Application.Audio;

public static class PcmCodec
{
    public static void ValidateFrame(AudioFrame frame)
    {
        if (frame.FrameSequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "FrameSequence starts at 1.");
        }

        if (frame.SampleOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "SampleOffset must be nonnegative.");
        }

        if (frame.Data.Length == 0 || frame.Data.Length % 2 != 0 || frame.Data.Length > CanonicalAudio.MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "PCM frame must be 2..1920 even bytes.");
        }
    }

    public static int SampleCount(ReadOnlyMemory<byte> data) => data.Length / 2;

    public static short ReadSample(ReadOnlySpan<byte> data, int index) =>
        (short)(data[index * 2] | (data[(index * 2) + 1] << 8));

    public static bool IsContiguous(long previousSampleEnd, AudioFrame next) =>
        next.SampleOffset == previousSampleEnd;

    public static byte[] ToneFrame(int samples, int phase)
    {
        var data = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            var value = (short)(Math.Sin((phase + index) * 2.0 * Math.PI * 440.0 / CanonicalAudio.SampleRateHz) * 8000);
            data[index * 2] = (byte)(value & 0xff);
            data[(index * 2) + 1] = (byte)((value >> 8) & 0xff);
        }

        return data;
    }
}
