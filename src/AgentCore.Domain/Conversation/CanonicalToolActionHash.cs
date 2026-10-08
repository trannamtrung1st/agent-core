using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore.Domain.Conversation;

public static class CanonicalToolActionHash
{
    public static string Compute(string toolName, JsonElement args)
    {
        var payload = $"{toolName}\n{Normalize(args)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Normalize(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeObject(element),
            JsonValueKind.Array => NormalizeArray(element),
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
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
