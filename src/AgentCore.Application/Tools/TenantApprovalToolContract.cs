using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantAuthorizationIntent(AgentIdentity? Persona, PreparedConversationPreferences? Preferences)
{
    public bool Matches(PreparedTenantOperation operation) => Persona == operation.Persona && Preferences == operation.Preferences;
}

public static class TenantApprovalToolContract
{
    public const int MaxArgumentBytes = 16384;
    public static ToolDescriptor Descriptor() => new(new ModelToolDefinition(ToolCatalog.RequestAuthorization,
        "Request human authorization for an exact persona or conversation-preference update to this agent. Select update_persona with persona, or update_preferences with preferences containing set/clear. Core derives identity, target, permission and witnesses. Use as the sole call in a batch. Pending requests suspend execution until a durable decision. A request or an Allowed preparation decision is not an applied effect. Do not repeat a refused operation in this task.",
        Schema()),
        ToolEffect.Write, ToolOfferRule.TenantExecutionAuthority, ToolResourceScope.Session);

    private static string Schema()
    {
        var persona = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["update_persona"]},"persona":{"type":"object","additionalProperties":false,"properties":{"name":{"type":"string","minLength":1,"maxLength":80},"role":{"type":"string","minLength":1,"maxLength":256},"description":{"type":"string","minLength":1,"maxLength":1024},"tone":{"type":"string","minLength":1,"maxLength":256}},"required":["name","role","description","tone"]}},"required":["operation","persona"]}""")!;
        var preferences = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["update_preferences"]}},"required":["operation","preferences"]}""")!;
        preferences["properties"]!["preferences"] = JsonNode.Parse(TenantPreferenceToolContract.Descriptor().ModelDefinition.ParametersJson);
        return new JsonObject { ["type"] = "object", ["oneOf"] = new JsonArray(persona, preferences) }.ToJsonString();
    }

    public static TenantAuthorizationIntent Parse(ModelToolCall call)
    {
        if (call.Name != ToolCatalog.RequestAuthorization || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > MaxArgumentBytes)
            throw new ArgumentException("Invalid authorization request.");
        using var document = JsonDocument.Parse(call.ArgumentsJson); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("operation", out var operation)
            && operation.ValueKind == JsonValueKind.String && operation.GetString() == "update_preferences")
        {
            Require(root, ["operation", "preferences"]);
            return new(null, TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(root.GetProperty("preferences").GetRawText())));
        }
        Require(root, ["operation", "persona"]);
        if (root.GetProperty("operation").ValueKind != JsonValueKind.String || root.GetProperty("operation").GetString() != "update_persona")
            throw new ArgumentException("Unsupported authorization operation.");
        var persona = root.GetProperty("persona"); Require(persona, ["name", "role", "description", "tone"]);
        string Read(string key) => persona.GetProperty(key).ValueKind == JsonValueKind.String ? persona.GetProperty(key).GetString()! : throw new ArgumentException("Invalid persona.");
        var value = new AgentIdentity(Read("name"), Read("role"), Read("description"), Read("tone"));
        AgentDefinitionValidator.ValidateIdentity(value);
        if (value.Name.Length > AgentCore.Domain.Conversation.AgentRunLimits.MaxPersonaNameCharacters) throw new ArgumentException("Persona name exceeds the execution identity bound.");
        return new(value, null);
    }
    private static void Require(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an exact object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != keys.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !keys.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentException("Use only the exact operation fields.");
    }
}
