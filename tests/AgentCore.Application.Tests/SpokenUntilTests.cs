using AgentCore.Application.Speech;

namespace AgentCore.Application.Tests;

public sealed class SpokenUntilTests
{
    [Fact]
    public void Timing_marks_credit_greatest_mark_at_or_before_consumed()
    {
        var until = new SpokenUntilAccumulator();
        until.TrackSegment(new SpeechSegment(Guid.Empty, 0, 0, "Hello world."), 0);
        until.AddTimingMark(5, 100);
        until.AddTimingMark(12, 240);
        until.CompleteSegment(0, 240);
        Assert.Equal(5, until.Credit(100));
        Assert.Equal(12, until.Credit(240));
        Assert.Equal(5, until.Credit(120));
    }

    [Fact]
    public void Without_marks_partial_segment_credits_zero_even_when_half_played()
    {
        var until = new SpokenUntilAccumulator();
        until.TrackSegment(new SpeechSegment(Guid.Empty, 0, 0, "AAAAAAAAAA. "), 0);
        until.CompleteSegment(0, 100);
        until.TrackSegment(new SpeechSegment(Guid.Empty, 1, 12, "BBBBBBBBBB."), 100);
        until.CompleteSegment(1, 100);
        Assert.Equal(12, until.Credit(150));
        Assert.Equal(12, until.Credit(199));
        Assert.Equal(23, until.Credit(200));
    }
}
