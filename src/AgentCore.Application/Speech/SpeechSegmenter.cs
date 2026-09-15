using System.Text;

namespace AgentCore.Application.Speech;

public sealed record SpeechSegment(Guid ResponseId, int SegmentIndex, int TextStart, string Text);

public sealed class SpeechSegmenter
{
    public const int MinSentenceCharacters = 20;
    public const int MinClauseCharacters = 40;
    public const int LatencyCharacters = 20;
    public const int SoftCapCharacters = 120;
    public const int HardCapCharacters = 240;
    public static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(300);

    private readonly Guid _responseId;
    private readonly StringBuilder _buffer = new();
    private int _textStart;
    private int _nextIndex;
    private DateTimeOffset? _bufferedSince;
    private bool _invalid;

    public SpeechSegmenter(Guid responseId)
    {
        _responseId = responseId;
    }

    public int BufferedLength => _buffer.Length;

    public bool HasBuffered => _buffer.Length > 0;

    public IReadOnlyList<SpeechSegment> Append(string text, DateTimeOffset now)
    {
        if (_invalid || string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (_buffer.Length == 0)
        {
            _bufferedSince = now;
        }

        _buffer.Append(text);
        return ReleaseReady(now, complete: false);
    }

    public IReadOnlyList<SpeechSegment> Tick(DateTimeOffset now) =>
        _invalid ? [] : ReleaseReady(now, complete: false);

    public IReadOnlyList<SpeechSegment> Complete()
    {
        if (_invalid)
        {
            return [];
        }

        var released = ReleaseReady(DateTimeOffset.MaxValue, complete: true).ToList();
        if (_buffer.Length > 0 && !string.IsNullOrWhiteSpace(_buffer.ToString()))
        {
            released.Add(Take(_buffer.Length));
        }
        else
        {
            _buffer.Clear();
            _bufferedSince = null;
        }

        return released;
    }

    public void Invalidate()
    {
        _invalid = true;
        _buffer.Clear();
        _bufferedSince = null;
    }

    private List<SpeechSegment> ReleaseReady(DateTimeOffset now, bool complete)
    {
        var released = new List<SpeechSegment>();
        while (true)
        {
            var cut = FindCut(now, complete);
            if (cut is null)
            {
                break;
            }

            released.Add(Take(cut.Value));
            if (_buffer.Length == 0)
            {
                _bufferedSince = null;
            }
            else if (!complete)
            {
                _bufferedSince = now;
            }
        }

        return released;
    }

    private int? FindCut(DateTimeOffset now, bool complete)
    {
        var text = _buffer.ToString();
        var length = text.Length;
        if (length == 0)
        {
            return null;
        }

        var sentence = FirstPunctuationCut(text, ['.', '?', '!'], MinSentenceCharacters, skipDecimal: true);
        if (sentence is not null)
        {
            return sentence;
        }

        var clause = FirstPunctuationCut(text, [';', ':'], MinClauseCharacters, skipDecimal: false);
        if (clause is not null)
        {
            return clause;
        }

        if (!complete
            && _bufferedSince is { } started
            && now - started >= Latency
            && length >= LatencyCharacters)
        {
            var whitespace = LastWhitespaceExclusive(text, length);
            if (whitespace >= LatencyCharacters)
            {
                return whitespace;
            }
        }

        if (length >= SoftCapCharacters)
        {
            var whitespace = LastWhitespaceAtOrBeyond(text, LatencyCharacters);
            if (whitespace is >= LatencyCharacters)
            {
                return whitespace;
            }

            if (length >= HardCapCharacters)
            {
                return SplitOnScalar(text, HardCapCharacters);
            }
        }

        if (complete && !string.IsNullOrWhiteSpace(text))
        {
            return length;
        }

        return null;
    }

    private static int? FirstPunctuationCut(string text, char[] marks, int minLength, bool skipDecimal)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (!marks.Contains(current))
            {
                continue;
            }

            if (skipDecimal
                && current == '.'
                && index > 0
                && index + 1 < text.Length
                && char.IsDigit(text[index - 1])
                && char.IsDigit(text[index + 1]))
            {
                continue;
            }

            var end = ConsumeClosers(text, index + 1);
            if (end >= minLength)
            {
                return end;
            }
        }

        return null;
    }

    private static int ConsumeClosers(string text, int start)
    {
        var index = start;
        while (index < text.Length && "\"'”’)]}»".Contains(text[index], StringComparison.Ordinal))
        {
            index++;
        }

        return index;
    }

    private static int LastWhitespaceExclusive(string text, int endExclusive)
    {
        for (var index = endExclusive - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static int? LastWhitespaceAtOrBeyond(string text, int minExclusive)
    {
        var cut = LastWhitespaceExclusive(text, text.Length);
        return cut >= minExclusive ? cut : null;
    }

    private static int SplitOnScalar(string text, int max)
    {
        var index = Math.Min(max, text.Length);
        if (index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
        {
            index--;
        }

        return Math.Max(1, index);
    }

    private SpeechSegment Take(int length)
    {
        var text = _buffer.ToString(0, length);
        _buffer.Remove(0, length);
        var segment = new SpeechSegment(_responseId, _nextIndex, _textStart, text);
        _nextIndex++;
        _textStart += length;
        return segment;
    }
}
