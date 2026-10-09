using System.Text.Json.Nodes;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

/// <summary>Serializes the current function schema without contract translation.</summary>
internal static class OpenAiCompatibleToolSchema
{
    public static JsonNode? Normalize(string json)
    {
        var schema = JsonNode.Parse(json);
        RemoveEmptyRequired(schema);
        return schema;
    }

    private static void RemoveEmptyRequired(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["required"] is JsonArray { Count: 0 }) obj.Remove("required");
            foreach (var child in obj) RemoveEmptyRequired(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemoveEmptyRequired(child);
    }
}
