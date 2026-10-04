using System.Text.Json.Nodes;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

/// <summary>Projects object unions into the function-schema subset accepted by OpenAI endpoints.</summary>
internal static class OpenAiCompatibleToolSchema
{
    public static JsonNode? Normalize(string json)
    {
        var schema = JsonNode.Parse(json);
        if (schema is JsonObject root && root["type"]?.GetValue<string>() == "object"
            && root["properties"] is null && (root["oneOf"] ?? root["anyOf"]) is JsonArray branches
            && branches.Count > 0 && branches.All(branch => branch is JsonObject obj
                && obj["type"]?.GetValue<string>() == "object" && obj["properties"] is JsonObject))
        {
            var properties = new JsonObject();
            HashSet<string>? required = null;
            foreach (var branch in branches.Cast<JsonObject>())
            {
                foreach (var (name, field) in (JsonObject)branch["properties"]!)
                {
                    if (properties[name] is not { } previous)
                        properties[name] = field?.DeepClone();
                    else if (!JsonNode.DeepEquals(previous, field))
                    {
                        var variants = previous is JsonObject union && union["anyOf"] is JsonArray choices
                            ? choices : new JsonArray(previous.DeepClone());
                        if (!variants.Any(choice => JsonNode.DeepEquals(choice, field)))
                            variants.Add(field?.DeepClone());
                        properties[name] = new JsonObject { ["anyOf"] = variants.DeepClone() };
                    }
                }
                var branchRequired = ((JsonArray?)branch["required"] ?? [])
                    .Select(field => field!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                if (required is null) required = branchRequired;
                else required.IntersectWith(branchRequired);
            }
            root.Remove("oneOf");
            root.Remove("anyOf");
            root["properties"] = properties;
            if (required is { Count: > 0 })
                root["required"] = new JsonArray(required.Order(StringComparer.Ordinal)
                    .Select(field => (JsonNode?)JsonValue.Create(field)).ToArray());
            else root.Remove("required");
            if (branches.All(branch => branch!["additionalProperties"]?.GetValue<bool>() == false))
                root["additionalProperties"] = false;
            root["description"] = "Core validates the operation-specific required fields and allowed argument combinations.";
        }
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
