using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

internal static class ToolArgumentPayload
{
    // Preserve explicit values and raw call identity. Only an offered zero-property
    // object schema can interpret a missing transport payload as an empty object.
    internal static string Normalize(string? payload, ModelToolDefinition? offered)
    {
        if (!string.IsNullOrWhiteSpace(payload) || offered is null) return payload ?? "";
        try
        {
            using var json = JsonDocument.Parse(offered.ParametersJson);
            var schema = json.RootElement;
            if (schema.ValueKind == JsonValueKind.Object
                && schema.EnumerateObject().All(p => p.Name is "type" or "additionalProperties" or "properties" or "required" or "title" or "description")
                && schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object"
                && schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False
                && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && !properties.EnumerateObject().Any()
                && (!schema.TryGetProperty("required", out var required) || required.ValueKind == JsonValueKind.Array && required.GetArrayLength() == 0))
                return "{}";
        }
        catch (JsonException) { }
        return payload ?? "";
    }
}
