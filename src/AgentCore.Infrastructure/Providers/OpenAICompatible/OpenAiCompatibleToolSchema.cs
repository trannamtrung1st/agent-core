using System.Text.Json.Nodes;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

/// <summary>Normalizes equivalent JSON Schema forms for compatible providers.</summary>
internal static class OpenAiCompatibleToolSchema
{
    public static JsonNode? Normalize(string json)
    {
        var schema = JsonNode.Parse(json);
        NormalizeNodes(schema);
        return schema;
    }

    // Anthropic's documented structured-output conversion retains these constraints as
    // instructions; Core still validates the original semantic/tool schema before effects.
    public static JsonNode? ForNative(string json, bool anthropic)
    {
        var node = Normalize(json);
        if (anthropic) SimplifyAnthropic(node);
        return node;
    }
    private static void SimplifyAnthropic(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "minItems", "maxItems" })
                if (obj.Remove(name, out var value)) obj["description"] = (obj["description"]?.GetValue<string>() ?? "") + $" Constraint: {name}={value?.ToJsonString()}.";
            foreach (var value in obj.Select(p => p.Value).ToArray()) SimplifyAnthropic(value);
        }
        else if (node is JsonArray array) foreach (var item in array) SimplifyAnthropic(item);
    }

    private static void NormalizeNodes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["required"] is JsonArray { Count: 0 }) obj.Remove("required");
            // Nullable type arrays are valid JSON Schema, but some compatible providers
            // reject enum members against an array-valued type. anyOf is equivalent.
            if (obj["type"] is JsonArray types && types.Count == 2
                && types.Any(type => type?.GetValue<string>() == "null"))
            {
                var branches = new JsonArray();
                foreach (var type in types)
                {
                    var name = type!.GetValue<string>();
                    var branch = new JsonObject { ["type"] = name };
                    if (obj["enum"] is JsonArray values)
                    {
                        var matching = new JsonArray(values.Where(value => name == "null" ? value is null : value is not null)
                            .Select(value => value?.DeepClone()).ToArray());
                        if (matching.Count == 0) continue;
                        branch["enum"] = matching;
                    }
                    branches.Add(branch);
                }
                obj.Remove("type"); obj.Remove("enum");
                if (obj.ContainsKey("anyOf"))
                {
                    var constraints = obj["allOf"] as JsonArray ?? new JsonArray();
                    if (obj["allOf"] is null) obj["allOf"] = constraints;
                    constraints.Add(new JsonObject { ["anyOf"] = branches });
                }
                else obj["anyOf"] = branches;
            }
            foreach (var child in obj.ToArray()) NormalizeNodes(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) NormalizeNodes(child);
    }
}
