using System.Text;
using System.Text.Json;

namespace AgentCore.Application.Events;

/// <summary>Bounded, untrusted webhook evidence. The URL identifies the Event; payloads cannot select behavior.</summary>
public static class ExternalEventEnvelope
{
    public const int MaxRawBytes = 8192;
    public const int MaxFieldLength = 64;
    public const int MaxTriggerDepth = 4;
    public const int MaxJsonDepth = 8;
    public const int MaxDataBytes = 4096;

    public static string Build(string eventId, string orderReference, DateTimeOffset? occurredAt = null) =>
        JsonSerializer.Serialize(new { eventId, occurredAt, data = new { orderReference } });

    public static bool TryNormalize(ReadOnlySpan<byte> utf8, out string evidence, out string sourceEventId,
        out DateTimeOffset occurredAtUtc, out string? error)
    {
        evidence = ""; sourceEventId = ""; occurredAtUtc = default; error = "invalid_payload";
        if (utf8.Length == 0 || utf8.Length > MaxRawBytes)
        { if (utf8.Length > MaxRawBytes) error = "payload_too_large"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root)) return false;
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("eventId" or "occurredAt" or "data" or "rootAgentRunId" or "triggerDepth")) return false;
            if (!root.TryGetProperty("eventId", out var id) || id.ValueKind != JsonValueKind.String) return false;
            var text = id.GetString()!;
            if (text.Length is < 1 or > MaxFieldLength || text.Any(c => c is < '!' or > '~')) return false;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(data.GetRawText()) > MaxDataBytes) return false;
            occurredAtUtc = DateTimeOffset.UnixEpoch;
            if (root.TryGetProperty("occurredAt", out var at) && at.ValueKind != JsonValueKind.Null)
            {
                if (at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var parsed) || parsed.Offset != TimeSpan.Zero) return false;
                occurredAtUtc = parsed;
            }
            Guid? rootId = null; var depth = 0;
            var hasRoot = root.TryGetProperty("rootAgentRunId", out var correlation);
            var hasDepth = root.TryGetProperty("triggerDepth", out var recursion);
            if (hasRoot != hasDepth) { error = "invalid_correlation"; return false; }
            if (hasRoot)
            {
                if (correlation.ValueKind != JsonValueKind.String || !correlation.TryGetGuid(out var value) || value == Guid.Empty
                    || recursion.ValueKind != JsonValueKind.Number || !recursion.TryGetInt32(out depth) || depth < 1)
                { error = "invalid_correlation"; return false; }
                if (depth > MaxTriggerDepth) { error = "trigger_depth_exceeded"; return false; }
                rootId = value;
            }
            evidence = JsonSerializer.Serialize(new { sourceEventId = text, data, occurredAtUtc = occurredAtUtc == DateTimeOffset.UnixEpoch ? (DateTimeOffset?)null : occurredAtUtc, rootAgentRunId = rootId, triggerDepth = depth });
            if (Encoding.UTF8.GetByteCount(evidence) > 4096) return false;
            sourceEventId = text; error = null; return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(p => names.Add(p.Name) && UniqueProperties(p.Value));
        }
        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(UniqueProperties);
    }
}
