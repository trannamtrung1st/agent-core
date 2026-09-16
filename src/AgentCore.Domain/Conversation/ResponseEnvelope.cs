using System.Globalization;
using System.Text;

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

public sealed record ResponseEnvelope(
    string DisplayText,
    string? SpeechText,
    IReadOnlyList<ResponseBlock> Blocks);

public static class ResponseEnvelopeParser
{
    public const string UnsupportedFallback = "[Unsupported content]";
    public const string UnauthorizedArtifactFallback = "[Unavailable artifact]";

    public static ResponseEnvelope Parse(
        string raw,
        Func<string, bool>? artifactAllowed = null,
        bool finalize = false)
    {
        raw ??= string.Empty;
        var work = raw;
        if (!finalize)
        {
            var open = work.LastIndexOf("[[", StringComparison.Ordinal);
            var close = work.LastIndexOf("]]", StringComparison.Ordinal);
            if (open >= 0 && open > close)
            {
                work = work[..open];
            }
        }

        var display = new StringBuilder();
        var blocks = new List<ResponseBlock>();
        string? speech = null;
        var index = 0;
        var blockOrdinal = 0;
        while (index < work.Length)
        {
            var start = work.IndexOf("[[", index, StringComparison.Ordinal);
            if (start < 0)
            {
                display.Append(work.AsSpan(index));
                break;
            }

            display.Append(work.AsSpan(index, start - index));
            var end = work.IndexOf("]]", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                if (finalize)
                {
                    display.Append(work.AsSpan(start));
                }

                break;
            }

            var inner = work[(start + 2)..end];
            var colon = inner.IndexOf(':');
            var kind = (colon < 0 ? inner : inner[..colon]).Trim();
            var payload = colon < 0 ? string.Empty : inner[(colon + 1)..];
            if (kind.Equals("speech", StringComparison.OrdinalIgnoreCase))
            {
                speech ??= payload;
            }
            else
            {
                blockOrdinal++;
                var blockId = "b" + blockOrdinal.ToString(CultureInfo.InvariantCulture);
                blocks.Add(ParseBlock(blockId, kind, payload, artifactAllowed));
            }

            index = end + 2;
        }

        return new ResponseEnvelope(
            display.ToString(),
            string.IsNullOrEmpty(speech) ? null : speech,
            blocks);
    }

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

    private static ResponseBlock ParseBlock(
        string blockId,
        string kind,
        string payload,
        Func<string, bool>? artifactAllowed)
    {
        if (kind.Equals("md", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            return new ResponseBlock(blockId, ResponseBlockKind.Markdown, payload, payload, null, null, false);
        }

        if (kind.Equals("attachment", StringComparison.OrdinalIgnoreCase))
        {
            return new ResponseBlock(
                blockId,
                ResponseBlockKind.AttachmentReference,
                payload,
                payload,
                payload,
                null,
                false);
        }

        if (kind.Equals("artifact", StringComparison.OrdinalIgnoreCase))
        {
            if (artifactAllowed?.Invoke(payload) == true)
            {
                return new ResponseBlock(
                    blockId,
                    ResponseBlockKind.ArtifactReference,
                    payload,
                    payload,
                    null,
                    payload,
                    false);
            }

            return new ResponseBlock(
                blockId,
                ResponseBlockKind.Unknown,
                string.Empty,
                UnauthorizedArtifactFallback,
                null,
                null,
                false);
        }

        return new ResponseBlock(
            blockId,
            ResponseBlockKind.Unknown,
            string.Empty,
            UnsupportedFallback,
            null,
            null,
            false);
    }
}
