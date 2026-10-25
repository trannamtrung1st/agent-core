using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ConfigurationTools
{
    public static bool IsTool(string name) => Names.Contains(name, StringComparer.Ordinal);
    private static readonly string[] Names = ["configuration.inspect", "configuration.propose", "management.inspect", "management.policy.update", "management.freeze", "settings.list", "settings.inspect", "settings.patch", "resources.list", "resources.inspect", "resources.upsert", "resources.set_enabled", "resources.delete", "resources.copy", "credentials.binding.propose", "credentials.binding.remove", "automation.preferences.inspect", "automation.preferences.patch"];
    public static IEnumerable<ToolDescriptor> Descriptors()
    {
        foreach (var name in Names)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            void Field(string key, JsonObject schema, bool needed = true) { properties[key] = schema; if (needed) required.Add(key); }
            JsonObject Text(int max) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max };
            JsonObject Integer() => new() { ["type"] = "integer", ["minimum"] = 1 };
            JsonObject Choice(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
            var read = name.EndsWith(".list", StringComparison.Ordinal) || name.EndsWith(".inspect", StringComparison.Ordinal);
            if (!read) Field("expectedInstanceRevision", Integer());
            if (name == "configuration.inspect") { Field("goal", Text(200), false); Field("target", Text(64), false); }
            if (name is "settings.inspect" or "settings.patch" or "configuration.propose") Field("section", Choice(InstanceSettingsResolver.Sections));
            if (name is "settings.patch" or "configuration.propose")
            {
                if (name == "configuration.propose") { Field("option", Text(64)); Field("target", Text(64)); Field("goal", Text(200)); }
                // Field names are strictly validated by the existing typed settings owner. Cross-section fields fail closed.
                var settingFields = new JsonObject();
                foreach (var field in InstanceSettingsResolver.Sections.SelectMany(section => section == "sandboxPolicy" ? new[] { "profileId" } : InstanceSettingsResolver.Fields(section)).Distinct())
                    settingFields[field] = SettingSchema(field);
                Field("set", new() { ["type"] = "object", ["maxProperties"] = 24, ["additionalProperties"] = false, ["properties"] = settingFields });
                Field("clear", new() { ["type"] = "array", ["maxItems"] = 24, ["uniqueItems"] = true, ["items"] = Text(64) });
            }
            if (name == "management.policy.update")
            {
                Field("mode", Choice(Enum.GetNames<HarnessManagementMode>()));
                Field("scopes", new() { ["type"] = "array", ["maxItems"] = 9, ["uniqueItems"] = true, ["items"] = Choice(Enum.GetNames<HarnessManagementScope>()) });
                Field("managedAutoApply", new() { ["type"] = "array", ["maxItems"] = 3, ["uniqueItems"] = true, ["items"] = Choice(Enum.GetNames<ManagementAutoApply>()) });
            }
            if (name is "resources.inspect" or "resources.set_enabled" or "resources.delete" or "resources.copy") Field("key", Text(75));
            if (name is "resources.set_enabled" or "resources.delete") Field("expectedResourceRevision", new() { ["type"] = "integer", ["minimum"] = 0 });
            if (name == "resources.set_enabled") Field("enabled", new() { ["type"] = new JsonArray("boolean", "null"), ["description"] = "null resets an inherited Definition resource to its default." });
            if (name is "resources.upsert" or "resources.copy") Field("logicalPath", Text(240));
            if (name == "resources.upsert")
            {
                Field("key", Text(75), false);
                Field("expectedResourceRevision", Integer(), false);
                Field("kind", Choice(["Knowledge", "Reference", "Template"]));
                Field("content", Text(32000));
                Field("source", Text(2048));
                Field("mediaType", Choice(["text/plain", "text/markdown", "application/json"]));
            }
            if (name == "credentials.binding.propose") { Field("credentialId", Text(36)); Field("reference", Text(80)); }
            if (name == "credentials.binding.remove") { Field("bindingId", Text(36)); Field("expectedBindingRevision", Integer()); }
            if (name == "automation.preferences.patch")
            {
                Field("offeringId", new() { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 128 });
                Field("reasoningEffort", new() { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 64 });
            }
            var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required };
            yield return new(new(name, Help(name), schema.ToJsonString()), read ? ToolEffect.ReadOnly : ToolEffect.SensitiveWrite,
                ToolOfferRule.RoleAllowlist, ToolResourceScope.Owner, read ? ToolReplaySafety.ReplaySafe : ToolReplaySafety.NonReplayable);
        }
    }
    private static string Help(string name) => name switch {
        "configuration.inspect" => "Inspect only this Instance's enabled management areas, effective settings and actual tool blocker. Returns current revision, exact safe remediation options or an owner-only Admin destination. Never exposes secrets or host configuration.",
        "configuration.propose" => "Apply an inspection-returned settings.patch option to this Instance using exact section/set/clear and revision. Core derives risk. Authority, privacy, autonomy and material changes need owner approval. New values apply to the next Run; no task effect is replayed.",
        "management.freeze" => "Freeze this Instance's self-management after exact owner confirmation. Revokes management immediately, preserves normal tasks and retained content. Only Admin can unfreeze. Inspect the current Instance revision first.",
        "management.policy.update" => "Propose exact mode/scopes/Managed allowances for this Instance. Always needs owner approval; cannot enable from Off or unfreeze. Inspect current revision first. New grants apply to a fresh Run.",
        "resources.upsert" => "Save bounded owner-provided or successfully read text to an Instance resource through the same Admin owner. source must be conversation:user or a successful current-Run source receipt. Inherited bytes are read-only. Supply revision guards for replacement; new content applies next Run.",
        "credentials.binding.propose" => "Propose binding an existing owner-eligible System Credential identified by configuration.inspect to this Instance alias. Requires exact owner approval; never returns or writes a secret. No OAuth, MFA, host or task authorization is granted.",
        _ => $"{name}: inspect or change only this Instance through the existing Admin owner. Inspect revision/source/constraints before changes. Writes apply next Run; current Runs remain pinned. Core approvals and scope checks cannot be bypassed."
    };

    private static JsonObject SettingSchema(string field)
    {
        var choices = field switch {
            "conversationContextScope" => Enum.GetNames<ConversationContextScope>(), "homeAccess" => Enum.GetNames<HomeAccess>(),
            "workingCleanup" => Enum.GetNames<WorkingCleanup>(), "browserIsolation" => Enum.GetNames<BrowserIsolation>(),
            "agentStateVisibility" => Enum.GetNames<AgentStateVisibility>(), _ => null };
        if (choices is not null) return new() { ["type"] = "string", ["enum"] = new JsonArray(choices.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
        if (field == "profileId") return new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 };
        if (field is "triggers" or "selected" or "always" or "allowedSourceKinds")
            return new() { ["type"] = "array", ["maxItems"] = 256, ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 } };
        if (field is "speakingRate") return new() { ["type"] = "number", ["minimum"] = 0.25, ["maximum"] = 4 };
        if (field.StartsWith("max", StringComparison.Ordinal) || field.EndsWith("Ms", StringComparison.Ordinal) || field.StartsWith("min", StringComparison.Ordinal) || field == "oneShotHorizonDays")
            return new() { ["type"] = field is "maxConsecutiveProactiveTurns" or "maxSilentEvaluations" or "maxInactivityMs" ? new JsonArray("integer", "null") : JsonValue.Create("integer") };
        if (field is "agentInstructions" or "responseLength" or "language" or "interruptionStyle" or "voiceId" or "interruptionClassifier")
            return new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = field == "agentInstructions" ? 8000 : 80 };
        if (field is "offeringId" or "reasoningEffort" or "speechRecognizer" or "speechSynthesizer")
            return new() { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 80 };
        return new() { ["type"] = "boolean" };
    }

    public static void Validate(string name, JsonElement args)
    {
        using var schema = JsonDocument.Parse(ToolRegistry.Get(name).ModelDefinition.ParametersJson);
        ValidateValue(args, schema.RootElement);
    }

    private static void ValidateValue(JsonElement value, JsonElement rule)
    {
        var type = value.ValueKind switch { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null => "null", JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number", _ => "invalid" };
        if (rule.TryGetProperty("type", out var types))
        {
            var allowed = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().Select(t => t.GetString()).ToArray() : [types.GetString()];
            if (!allowed.Contains(type) && !(type == "integer" && allowed.Contains("number"))) throw new ArgumentException("Invalid management argument type.");
        }
        if (rule.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => v.GetRawText() == value.GetRawText())) throw new ArgumentException("Unknown management value.");
        if (type == "string" && (rule.TryGetProperty("maxLength", out var max) && value.GetString()!.Length > max.GetInt32()
            || rule.TryGetProperty("minLength", out var min) && value.GetString()!.Length < min.GetInt32())) throw new ArgumentException("Management text exceeds its bounds.");
        if (type is "number" or "integer" && (rule.TryGetProperty("minimum", out var lower) && value.GetDouble() < lower.GetDouble()
            || rule.TryGetProperty("maximum", out var upper) && value.GetDouble() > upper.GetDouble())) throw new ArgumentException("Management number exceeds its bounds.");
        if (type == "array")
        {
            if (rule.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32()
                || rule.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean() && value.EnumerateArray().Select(v => v.GetRawText()).Distinct().Count() != value.GetArrayLength()) throw new ArgumentException("Invalid management array.");
            if (rule.TryGetProperty("items", out var items)) foreach (var item in value.EnumerateArray()) ValidateValue(item, items);
        }
        if (type == "object")
        {
            var fields = value.EnumerateObject().ToArray();
            if (fields.Select(p => p.Name).Distinct().Count() != fields.Length || rule.TryGetProperty("maxProperties", out var count) && fields.Length > count.GetInt32()) throw new ArgumentException("Invalid management fields.");
            if (rule.TryGetProperty("required", out var required)) foreach (var key in required.EnumerateArray()) if (!value.TryGetProperty(key.GetString()!, out _)) throw new ArgumentException("Missing management field.");
            if (rule.TryGetProperty("properties", out var properties)) foreach (var field in fields)
            {
                if (!properties.TryGetProperty(field.Name, out var child)) throw new ArgumentException("Unknown management field.");
                ValidateValue(field.Value, child);
            }
        }
    }
}
