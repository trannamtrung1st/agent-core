using AgentCore.Application.Speech;

namespace AgentCore.Application.Tests;

public sealed class SpeechSegmenterTests
{
    [Fact]
    public void Example_splits_on_sentence_boundaries_not_tokens()
    {
        var responseId = Guid.Parse("019944af-0000-7000-8000-0000000000aa");
        var segmenter = new SpeechSegmenter(responseId);
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var released = segmenter.Append(
            "Sure, there are three things I'd suggest. First, check the connection. Then try again.",
            now);
        var remainder = segmenter.Complete();
        var all = released.Concat(remainder).ToArray();
        Assert.Equal(
            [
                "Sure, there are three things I'd suggest.",
                " First, check the connection.",
                " Then try again."
            ],
            all.Select(segment => segment.Text).ToArray());
        Assert.Equal(0, all[0].TextStart);
        Assert.Equal(all[0].Text.Length, all[1].TextStart);
        Assert.Equal(0, all[0].SegmentIndex);
        Assert.Equal(1, all[1].SegmentIndex);
        Assert.Equal(2, all[2].SegmentIndex);
        Assert.All(all, segment => Assert.Equal(responseId, segment.ResponseId));
    }

    [Fact]
    public void Decimal_points_are_not_sentence_boundaries()
    {
        var segmenter = new SpeechSegmenter(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var released = segmenter.Append("The value is 3.14 exactly", now);
        Assert.Empty(released);
        var completed = segmenter.Complete();
        Assert.Equal("The value is 3.14 exactly", Assert.Single(completed).Text);
    }

    [Fact]
    public void Latency_rule_releases_through_whitespace()
    {
        var segmenter = new SpeechSegmenter(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(segmenter.Append("abcdefghijklmnopqrs ", now));
        var later = now + SpeechSegmenter.Latency;
        var released = segmenter.Tick(later);
        Assert.Equal("abcdefghijklmnopqrs ", Assert.Single(released).Text);
    }

    [Fact]
    public void Hard_cap_splits_on_a_unicode_scalar()
    {
        var segmenter = new SpeechSegmenter(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var text = new string('a', SpeechSegmenter.HardCapCharacters + 10);
        var released = segmenter.Append(text, now);
        Assert.Equal(SpeechSegmenter.HardCapCharacters, Assert.Single(released).Text.Length);
    }

    [Fact]
    public void Whitespace_only_remainder_is_not_a_speech_job()
    {
        var segmenter = new SpeechSegmenter(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Empty(segmenter.Complete());
        segmenter.Append("   ", now);
        Assert.Empty(segmenter.Complete());
    }

    [Fact]
    public void Invalidate_drops_unsent_buffer()
    {
        var segmenter = new SpeechSegmenter(Guid.NewGuid());
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        segmenter.Append("Hello there, this is enough. More", now);
        segmenter.Invalidate();
        Assert.Empty(segmenter.Complete());
    }
}
