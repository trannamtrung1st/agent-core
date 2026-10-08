using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore.Domain.Conversation;

public static class AgentRunActionHash
{
    public static string Compute(string toolName, JsonElement args)
    {
        var payload = $"{toolName}\n{Normalize(args)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool MatchesBrowserAct(string? payloadJson, string? actionHash)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || string.IsNullOrWhiteSpace(actionHash))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("Messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var message in messages.EnumerateArray())
            {
                if (!message.TryGetProperty("ToolCalls", out var calls) || calls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var call in calls.EnumerateArray())
                {
                    if (!call.TryGetProperty("Name", out var name)
                        || !string.Equals(name.GetString(), "browser.act", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var arguments = call.TryGetProperty("ArgumentsJson", out var raw) ? raw.GetString() : null;
                    var parsed = JsonSerializer.Deserialize<JsonElement>(
                        string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
                    if (string.Equals(Compute("browser.act", parsed), actionHash, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    public static string MarkObservationRequired(string payloadJson, string actionHash)
    {
        if (JsonNode.Parse(payloadJson) is not JsonObject node)
        {
            return payloadJson;
        }

        node["ObservationRequired"] = true;
        node["BlockedActionHash"] = actionHash;
        return node.ToJsonString();
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
