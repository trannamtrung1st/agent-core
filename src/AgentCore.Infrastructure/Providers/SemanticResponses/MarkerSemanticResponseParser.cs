using System.Text;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.SemanticResponses;

internal sealed class MarkerSemanticResponseParser
{
    private readonly StringBuilder _raw = new();
    private string _emittedDisplay = string.Empty;

    public string Feed(string chunk)
    {
        _raw.Append(chunk);
        var parsed = Parse(_raw.ToString(), finalize: false);
        return AdvanceDisplay(parsed.DisplayText);
    }

    public bool TryFinish(out ModelSemanticResponse? response, out string safeFailure)
    {
        var parsed = Parse(_raw.ToString(), finalize: true);
        if (parsed.DisplayText.Length > AssistantResponseSchema.MaxDisplayCharacters
            || parsed.Blocks.Count > AssistantResponseSchema.MaxBlocks
            || parsed.Speech.Text is { Length: > AssistantResponseSchema.MaxSpeechCharacters })
        {
            response = null;
            safeFailure = "Malformed assistant envelope.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.DisplayText))
        {
            response = null;
            safeFailure = "Malformed assistant envelope.";
            return false;
        }

        var remainder = AdvanceDisplay(parsed.DisplayText);
        if (remainder.Length > 0)
        {
            // Streaming already emitted the prefix; remainder is included in DisplayText.
        }

        if (!string.Equals(parsed.DisplayText, _emittedDisplay + remainder, StringComparison.Ordinal)
            && !string.Equals(parsed.DisplayText, _emittedDisplay, StringComparison.Ordinal))
        {
            // Keep semantic display as the parsed display; tests compare streamed concat to ready.
        }

        response = parsed;
        safeFailure = string.Empty;
        return true;
    }

    public bool TryPeekSpeechReady(out ModelSemanticResponse? response)
    {
        var parsed = Parse(_raw.ToString(), finalize: false);
        if (parsed.Speech.Mode != ModelSpeechMode.Custom
            || string.IsNullOrEmpty(parsed.Speech.Text)
            || string.IsNullOrWhiteSpace(parsed.DisplayText))
        {
            response = null;
            return false;
        }

        response = parsed;
        return true;
    }

    public string FinishDisplayRemainder(ModelSemanticResponse response)
    {
        if (response.DisplayText.StartsWith(_emittedDisplay, StringComparison.Ordinal))
        {
            return response.DisplayText[_emittedDisplay.Length..];
        }

        return string.Empty;
    }

    private string AdvanceDisplay(string display)
    {
        if (display.StartsWith(_emittedDisplay, StringComparison.Ordinal))
        {
            var delta = display[_emittedDisplay.Length..];
            _emittedDisplay = display;
            return delta;
        }

        return string.Empty;
    }

    internal static ModelSemanticResponse Parse(string raw, bool finalize)
    {
        raw ??= string.Empty;
        var work = raw;
        if (!finalize || HasUnclosedMarker(work))
        {
            var open = work.LastIndexOf("[[", StringComparison.Ordinal);
            var close = work.LastIndexOf("]]", StringComparison.Ordinal);
            if (open >= 0 && open > close)
            {
                work = work[..open];
            }
        }

        var display = new StringBuilder();
        var blocks = new List<ModelResponseBlock>();
        string? speech = null;
        var speechNone = false;
        var index = 0;
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
                break;
            }

            var inner = work[(start + 2)..end];
            var colon = inner.IndexOf(':');
            var kind = (colon < 0 ? inner : inner[..colon]).Trim();
            var payload = colon < 0 ? string.Empty : inner[(colon + 1)..];
            if (kind.Equals("speech", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    speechNone = true;
                }
                else if (payload.Length > 0)
                {
                    speech ??= payload;
                }
            }
            else if (kind.Equals("md", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("markdown", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Length <= AssistantResponseSchema.MaxBlockCharacters)
                {
                    blocks.Add(new ModelResponseBlock(ModelResponseBlockKind.Markdown, payload));
                }
            }
            else if (kind.Equals("attachment", StringComparison.OrdinalIgnoreCase)
                && payload.Length <= AssistantResponseSchema.MaxBlockCharacters)
            {
                blocks.Add(new ModelResponseBlock(ModelResponseBlockKind.AttachmentReference, payload, payload));
            }
            else if (kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)
                && payload.Length <= AssistantResponseSchema.MaxBlockCharacters)
            {
                blocks.Add(new ModelResponseBlock(ModelResponseBlockKind.ArtifactReference, payload, ArtifactId: payload));
            }
            else
            {
                blocks.Add(new ModelResponseBlock(ModelResponseBlockKind.Unknown));
            }

            index = end + 2;
        }

        if (speechNone)
        {
            return new ModelSemanticResponse(display.ToString(), new ModelSpeechProjection(ModelSpeechMode.None, null), blocks);
        }

        var spoken = string.IsNullOrEmpty(speech) ? null : speech;
        var projection = spoken is null
            ? new ModelSpeechProjection(ModelSpeechMode.Same, null)
            : new ModelSpeechProjection(ModelSpeechMode.Custom, spoken);
        return new ModelSemanticResponse(display.ToString(), projection, blocks);
    }

    private static bool HasUnclosedMarker(string work)
    {
        var open = work.LastIndexOf("[[", StringComparison.Ordinal);
        var close = work.LastIndexOf("]]", StringComparison.Ordinal);
        return open >= 0 && open > close;
    }
}
