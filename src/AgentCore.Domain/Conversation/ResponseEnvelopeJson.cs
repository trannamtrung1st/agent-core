using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Conversation;

public static class ResponseEnvelopeJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(ResponseEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope = NormalizeForPersistence(envelope);
        var dto = new EnvelopeDto(
            envelope.DisplayText,
            new SpeechDto(ToWire(envelope.SpeechMode), WireSpeechText(envelope)),
            envelope.Blocks);
        return JsonSerializer.Serialize(dto, Json);
    }

    public static ResponseEnvelope? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var display = root.TryGetProperty("displayText", out var displayEl)
            ? displayEl.GetString() ?? string.Empty
            : string.Empty;
        var blocks = root.TryGetProperty("blocks", out var blocksEl)
            ? JsonSerializer.Deserialize<ResponseBlock[]>(blocksEl.GetRawText(), Json) ?? []
            : [];

        if (root.TryGetProperty("speech", out var speechEl) && speechEl.ValueKind == JsonValueKind.Object)
        {
            var mode = ParseMode(
                speechEl.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() : null);
            string? text = null;
            if (speechEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                text = textEl.GetString();
            }

            return Reconstruct(display, mode, text, blocks);
        }

        string? legacy = null;
        if (root.TryGetProperty("speechText", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.String)
        {
            legacy = legacyEl.GetString();
        }

        if (!string.IsNullOrEmpty(legacy))
        {
            return new ResponseEnvelope(display, legacy, blocks, ResponseSpeechMode.Custom);
        }

        return new ResponseEnvelope(display, null, blocks, ResponseSpeechMode.Same);
    }

    private static ResponseEnvelope Reconstruct(
        string display,
        ResponseSpeechMode mode,
        string? text,
        IReadOnlyList<ResponseBlock> blocks)
    {
        try
        {
            return ResponseEnvelope.Create(display, new ResponseSpeech(mode, text), blocks);
        }
        catch (ArgumentException)
        {
            return new ResponseEnvelope(display, RestoreSpeechText(mode, text), blocks, mode);
        }
    }

    private static ResponseEnvelope NormalizeForPersistence(ResponseEnvelope envelope)
    {
        if (envelope.SpeechMode == ResponseSpeechMode.Custom
            && string.Equals(envelope.SpeechText?.Trim(), envelope.DisplayText.Trim(), StringComparison.Ordinal))
        {
            return envelope with { SpeechMode = ResponseSpeechMode.Same, SpeechText = null };
        }

        return envelope;
    }

    private static string? WireSpeechText(ResponseEnvelope envelope) => envelope.SpeechMode switch
    {
        ResponseSpeechMode.Custom => envelope.SpeechText,
        ResponseSpeechMode.Same when HasDerivedPlaybackCoordinate(envelope) => envelope.SpeechText,
        _ => null
    };

    private static bool HasDerivedPlaybackCoordinate(ResponseEnvelope envelope) =>
        !string.IsNullOrEmpty(envelope.SpeechText)
        && !string.Equals(envelope.SpeechText, envelope.DisplayText, StringComparison.Ordinal);

    private static string? RestoreSpeechText(ResponseSpeechMode mode, string? text) => mode switch
    {
        ResponseSpeechMode.Custom => text,
        ResponseSpeechMode.Same when !string.IsNullOrEmpty(text) => text,
        _ => null
    };

    private static string ToWire(ResponseSpeechMode mode) => mode switch
    {
        ResponseSpeechMode.Custom => "custom",
        ResponseSpeechMode.None => "none",
        _ => "same"
    };

    private static ResponseSpeechMode ParseMode(string? value) => value switch
    {
        "custom" => ResponseSpeechMode.Custom,
        "none" => ResponseSpeechMode.None,
        _ => ResponseSpeechMode.Same
    };

    private sealed record EnvelopeDto(string DisplayText, SpeechDto Speech, IReadOnlyList<ResponseBlock> Blocks);

    private sealed record SpeechDto(string Mode, string? Text);
}
