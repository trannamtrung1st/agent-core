using System.Text;
using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class OrderPlacedPayload
{
    public const int MaxRawBytes = 8192;

    public const int MaxFieldLength = 64;

    private static readonly string[] AllowedNames = ["sourceEventId", "orderReference", "occurredAtUtc"];

    public static string Build(string sourceEventId, string orderReference, DateTimeOffset? occurredAtUtc = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceEventId", sourceEventId);
            writer.WriteString("orderReference", orderReference);
            if (occurredAtUtc is DateTimeOffset at)
            {
                writer.WriteString("occurredAtUtc", at.ToUniversalTime().ToString("o"));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryNormalize(ReadOnlySpan<byte> utf8, out string evidence, out string sourceEventId, out string? error)
    {
        evidence = "";
        sourceEventId = "";
        error = null;
        if (utf8.Length == 0 || utf8.Length > MaxRawBytes)
        {
            error = utf8.Length > MaxRawBytes ? "payload_too_large" : "invalid_payload";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException)
        {
            error = "invalid_payload";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "invalid_payload";
                return false;
            }

            string? eventId = null;
            string? orderReference = null;
            string? occurredAt = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !AllowedNames.Contains(property.Name, StringComparer.Ordinal))
                {
                    error = "invalid_payload";
                    return false;
                }

                switch (property.Name)
                {
                    case "sourceEventId":
                        if (!TryEventId(property.Value, out eventId))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    case "orderReference":
                        if (!TryReference(property.Value, out orderReference))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    default:
                        if (!TryOccurredAt(property.Value, out occurredAt))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                }
            }

            if (eventId is null || orderReference is null)
            {
                error = "invalid_payload";
                return false;
            }

            evidence = Build(eventId, orderReference, occurredAt is null ? null : DateTimeOffset.Parse(occurredAt));
            if (Encoding.UTF8.GetByteCount(evidence) > TriggerLimits.MaxEvidenceBytes)
            {
                evidence = "";
                error = "invalid_payload";
                return false;
            }

            sourceEventId = eventId;
            return true;
        }
    }

    private static bool TryEventId(JsonElement value, out string? text)
    {
        text = null;
        if (!TryText(value, out var raw) || !IsPrintableAscii(raw))
        {
            return false;
        }

        text = raw;
        return true;
    }

    private static bool TryReference(JsonElement value, out string? text)
    {
        text = null;
        if (!TryText(value, out var raw))
        {
            return false;
        }

        text = raw;
        return true;
    }

    private static bool TryText(JsonElement value, out string text)
    {
        text = "";
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = value.GetString() ?? "";
        if (text.Length is < 1 or > MaxFieldLength || text != text.Trim())
        {
            return false;
        }

        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryOccurredAt(JsonElement value, out string? text)
    {
        text = null;
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)
            || !DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        text = parsed.ToUniversalTime().ToString("o");
        return true;
    }

    private static bool IsPrintableAscii(string value)
    {
        foreach (var character in value)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
