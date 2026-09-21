using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class ToolActionHash
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false
    };

    public static string Compute(string toolName, JsonElement args)
    {
        var normalized = Normalize(args);
        var payload = $"{toolName}\n{normalized}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Normalize(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeObject(element),
            JsonValueKind.Array => NormalizeArray(element),
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString(), CanonicalOptions),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };

    private static string NormalizeObject(JsonElement element)
    {
        var properties = element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"\"{property.Name}\":{Normalize(property.Value)}");
        return "{" + string.Join(',', properties) + "}";
    }

    private static string NormalizeArray(JsonElement element) =>
        "[" + string.Join(',', element.EnumerateArray().Select(Normalize)) + "]";
}
