using System.Text;
using System.Text.Json;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Events;

public static class ExternalEventEnvelope
{
    public const int MaxRawBytes = 8192;

    public const int MaxFieldLength = 64;
    public const int MaxTriggerDepth = 4;

    public static string Build(string eventId, string orderReference, DateTimeOffset? occurredAt = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", eventId);
            writer.WriteString("type", ExternalEventTypes.OrderPlaced);
            if (occurredAt is DateTimeOffset at)
            {
                writer.WriteString("occurredAt", at.ToUniversalTime().ToString("o"));
            }

            writer.WriteStartObject("data");
            writer.WriteString("orderReference", orderReference);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryNormalize(
        ReadOnlySpan<byte> utf8,
        out string evidence,
        out string sourceEventId,
        out string eventType,
        out DateTimeOffset occurredAtUtc,
        out string? error)
    {
        evidence = "";
        sourceEventId = "";
        eventType = "";
        occurredAtUtc = default;
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
            string? type = null;
            string? occurredAt = null;
            string? orderReference = null;
            Guid? rootWorkItemId = null;
            int? triggerDepth = null;
            var sawData = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    error = "invalid_payload";
                    return false;
                }

                switch (property.Name)
                {
                    case "rootWorkItemId":
                        if (property.Value.ValueKind != JsonValueKind.String || !Guid.TryParse(property.Value.GetString(), out var root) || root == Guid.Empty)
                        { error = "invalid_correlation"; return false; }
                        rootWorkItemId = root;
                        break;
                    case "triggerDepth":
                        if (!property.Value.TryGetInt32(out var depth) || depth < 1)
                        { error = "invalid_correlation"; return false; }
                        if (depth > MaxTriggerDepth) { error = "trigger_depth_exceeded"; return false; }
                        triggerDepth = depth;
                        break;
                    case "eventId":
                        if (!TryToken(property.Value, out eventId))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    case "type":
                        if (!TryToken(property.Value, out type))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    case "occurredAt":
                        if (!TryOccurredAt(property.Value, out occurredAt))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    case "data":
                        sawData = true;
                        if (!TryOrderReference(property.Value, out orderReference))
                        {
                            error = "invalid_payload";
                            return false;
                        }

                        break;
                    default:
                        error = "invalid_payload";
                        return false;
                }
            }

            if (eventId is null || type is null || !sawData || orderReference is null)
            {
                error = "invalid_payload";
                return false;
            }

            if (rootWorkItemId.HasValue != triggerDepth.HasValue)
            { error = "invalid_correlation"; return false; }

            if (!ExternalEventTypes.IsAllowed(type))
            {
                error = "unsupported_event";
                return false;
            }

            occurredAtUtc = occurredAt is null
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.Parse(occurredAt);
            evidence = Evidence(eventId, orderReference, occurredAt is null ? null : occurredAtUtc, rootWorkItemId, triggerDepth ?? 0);
            if (Encoding.UTF8.GetByteCount(evidence) > TriggerLimits.MaxEvidenceBytes)
            {
                evidence = "";
                error = "invalid_payload";
                return false;
            }

            sourceEventId = eventId;
            eventType = type;
            return true;
        }
    }

    public static string Evidence(string eventId, string orderReference, DateTimeOffset? occurredAtUtc, Guid? rootWorkItemId = null, int triggerDepth = 0)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceEventId", eventId);
            writer.WriteString("orderReference", orderReference);
            if (rootWorkItemId is Guid root) writer.WriteString("rootWorkItemId", root);
            writer.WriteNumber("triggerDepth", triggerDepth);
            if (occurredAtUtc is DateTimeOffset at)
            {
                writer.WriteString("occurredAtUtc", at.ToUniversalTime().ToString("o"));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryOrderReference(JsonElement value, out string? orderReference)
    {
        orderReference = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = false;
        foreach (var property in value.EnumerateObject())
        {
            if (seen || !property.NameEquals("orderReference") || !TryText(property.Value, out var text))
            {
                return false;
            }

            seen = true;
            orderReference = text;
        }

        return seen;
    }

    private static bool TryToken(JsonElement value, out string? text)
    {
        text = null;
        if (!TryText(value, out var raw))
        {
            return false;
        }

        foreach (var character in raw)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
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
            || !DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        text = parsed.ToUniversalTime().ToString("o");
        return true;
    }
}
