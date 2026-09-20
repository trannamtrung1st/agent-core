namespace AgentCore.Domain.Conversation;

public enum ResponseBlockKind
{
    Markdown,
    AttachmentReference,
    ArtifactReference,
    Unknown
}

public sealed record ResponseBlock(
    string BlockId,
    ResponseBlockKind Kind,
    string DisplayText,
    string FallbackText,
    string? AttachmentId,
    string? ArtifactId,
    bool DisplayDelivered);

public enum ResponseSpeechMode
{
    Same,
    Custom,
    None
}

public sealed record ResponseSpeech(ResponseSpeechMode Mode, string? Text);

public sealed record ResponseEnvelope(
    string DisplayText,
    string? SpeechText,
    IReadOnlyList<ResponseBlock> Blocks,
    ResponseSpeechMode SpeechMode)
{
    public ResponseEnvelope(string displayText, string? speechText, IReadOnlyList<ResponseBlock> blocks)
        : this(
            displayText,
            speechText,
            blocks,
            string.IsNullOrEmpty(speechText) ? ResponseSpeechMode.Same : ResponseSpeechMode.Custom)
    {
    }

    public ResponseSpeech Speech => SpeechMode switch
    {
        ResponseSpeechMode.Custom => new ResponseSpeech(ResponseSpeechMode.Custom, SpeechText),
        ResponseSpeechMode.None => new ResponseSpeech(ResponseSpeechMode.None, null),
        _ => new ResponseSpeech(ResponseSpeechMode.Same, null)
    };

    public static ResponseEnvelope Create(
        string displayText,
        ResponseSpeech speech,
        IReadOnlyList<ResponseBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(speech);
        blocks ??= [];
        if (string.IsNullOrWhiteSpace(displayText))
        {
            throw new ArgumentException("displayText is required for a normal assistant response.", nameof(displayText));
        }

        switch (speech.Mode)
        {
            case ResponseSpeechMode.Same:
                if (speech.Text is not null)
                {
                    throw new ArgumentException("speech.text must be null when mode is same.", nameof(speech));
                }

                return new ResponseEnvelope(displayText, null, blocks, ResponseSpeechMode.Same);
            case ResponseSpeechMode.Custom:
                if (string.IsNullOrWhiteSpace(speech.Text))
                {
                    throw new ArgumentException("speech.text is required when mode is custom.", nameof(speech));
                }

                return new ResponseEnvelope(displayText, speech.Text, blocks, ResponseSpeechMode.Custom);
            case ResponseSpeechMode.None:
                if (speech.Text is not null)
                {
                    throw new ArgumentException("speech.text is forbidden when mode is none.", nameof(speech));
                }

                return new ResponseEnvelope(displayText, null, blocks, ResponseSpeechMode.None);
            default:
                throw new ArgumentOutOfRangeException(nameof(speech), speech.Mode, "Unsupported speech mode.");
        }
    }

    public string? PublicCustomSpeech()
    {
        if (SpeechMode != ResponseSpeechMode.Custom || string.IsNullOrWhiteSpace(SpeechText))
        {
            return null;
        }

        var custom = SpeechText.Trim();
        var display = DisplayText.Trim();
        if (string.Equals(custom, display, StringComparison.Ordinal))
        {
            return null;
        }

        return SpeechText;
    }
}

public static class ResponseEnvelopeParser
{
    public const string UnsupportedFallback = "[Unsupported content]";
    public const string UnauthorizedArtifactFallback = "[Unavailable artifact]";

    public static ResponseEnvelope MergeDelivery(ResponseEnvelope? durable, ResponseEnvelope? current)
    {
        var source = current ?? durable;
        if (source is null)
        {
            return new ResponseEnvelope(string.Empty, null, []);
        }

        if (durable is null || durable.Blocks.Count == 0)
        {
            return source;
        }

        var delivered = durable.Blocks
            .Where(block => block.DisplayDelivered)
            .Select(block => block.BlockId)
            .ToHashSet(StringComparer.Ordinal);
        var blocks = source.Blocks
            .Select(block => delivered.Contains(block.BlockId) ? block with { DisplayDelivered = true } : block)
            .ToArray();
        return source with { Blocks = blocks };
    }
}
