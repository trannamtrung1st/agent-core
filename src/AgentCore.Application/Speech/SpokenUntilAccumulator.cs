namespace AgentCore.Application.Speech;

public sealed class SpokenUntilAccumulator
{
    private readonly List<TrackedSegment> _segments = [];
    private readonly List<(int TextEndExclusive, long SampleOffset)> _marks = [];
    private bool _hasMarks;

    public void Reset()
    {
        _segments.Clear();
        _marks.Clear();
        _hasMarks = false;
    }

    public void TrackSegment(SpeechSegment segment, long sampleOrigin)
    {
        _segments.Add(new TrackedSegment(segment.SegmentIndex, segment.TextStart, segment.Text.Length, sampleOrigin, 0, false));
    }

    public void CompleteSegment(int segmentIndex, long totalSamples)
    {
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index];
            if (segment.Index == segmentIndex)
            {
                _segments[index] = segment with { SampleCount = totalSamples, Complete = true };
                return;
            }
        }
    }

    public void AddTimingMark(int absoluteTextEndExclusive, long responseSampleOffset)
    {
        _hasMarks = true;
        _marks.Add((Math.Max(0, absoluteTextEndExclusive), Math.Max(0, responseSampleOffset)));
    }

    public int Credit(long consumedSamples)
    {
        if (consumedSamples <= 0)
        {
            return 0;
        }

        if (_hasMarks)
        {
            var credited = 0;
            foreach (var (textEnd, sampleOffset) in _marks)
            {
                if (sampleOffset <= consumedSamples)
                {
                    credited = Math.Max(credited, textEnd);
                }
            }

            return credited;
        }

        var end = 0;
        foreach (var segment in _segments.OrderBy(item => item.Index))
        {
            if (!segment.Complete)
            {
                continue;
            }

            var segmentEnd = segment.SampleOrigin + segment.SampleCount;
            if (segmentEnd <= consumedSamples)
            {
                end = segment.TextStart + segment.TextLength;
            }
        }

        return end;
    }

    private sealed record TrackedSegment(
        int Index,
        int TextStart,
        int TextLength,
        long SampleOrigin,
        long SampleCount,
        bool Complete);
}
