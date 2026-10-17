using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantAuthorizationIntent(AgentIdentity? Persona, PreparedConversationPreferences? Preferences, string? CredentialReference = null)
{
    public bool Matches(PreparedTenantOperation operation) => (Persona, Preferences, CredentialReference) switch {
        (not null, null, null) => operation.Kind == TenantOperationKind.Persona && Persona == operation.Persona,
        (null, not null, null) => operation.Kind == TenantOperationKind.ConversationPreferences && Preferences == operation.Preferences,
        (null, null, not null) => operation.Kind == TenantOperationKind.CredentialConnectionCheck && CredentialReference == operation.CredentialCheck!.Reference,
        _ => false };
}

public static class TenantApprovalToolContract
{
    public const int MaxArgumentBytes = 16384;
    public static bool AllowsCredentialCheck(AgentDefinition definition, AgentContext? context) => context?.Configuration?.CredentialBindings.Count > 0
        && (AgentCore.Application.Agents.RolePermissions.AllowsTool(definition, ToolCatalog.EmailAccounts)
            || AgentCore.Application.Agents.RolePermissions.AllowsTool(definition, ToolCatalog.DriveAccounts));
    public static bool Allows(AgentDefinition definition, AgentContext? context) => definition.SelfManagement?.Allows(HarnessManagementScope.Settings) == true || AllowsCredentialCheck(definition, context);
    public static ToolDescriptor Descriptor(bool settings = true, bool credentials = true) => new(new ModelToolDefinition(ToolCatalog.RequestAuthorization,
        "Request human authorization for one exact action. Select update_persona with persona, update_preferences with preferences containing set/clear, or check_credential_connection with a bound credential reference, as allowed by this schema. Core derives identity, target, permission and witnesses. Use as the sole call in a batch. Pending requests suspend execution until a durable decision. Approval is not an applied effect. Do not repeat a refused operation in this task.",
        Schema(settings, credentials)),
        ToolEffect.Write, ToolOfferRule.TenantExecutionAuthority, ToolResourceScope.Session);

    private static string Schema(bool settings, bool credentials)
    {
        var persona = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["update_persona"]},"persona":{"type":"object","additionalProperties":false,"properties":{"name":{"type":"string","minLength":1,"maxLength":80},"role":{"type":"string","minLength":1,"maxLength":256},"description":{"type":"string","minLength":1,"maxLength":1024},"tone":{"type":"string","minLength":1,"maxLength":256}},"required":["name","role","description","tone"]}},"required":["operation","persona"]}""")!;
        var preferences = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["update_preferences"]}},"required":["operation","preferences"]}""")!;
        preferences["properties"]!["preferences"] = JsonNode.Parse(TenantPreferenceToolContract.Descriptor().ModelDefinition.ParametersJson);
        var branches = new JsonArray();
        if (settings) { branches.Add(persona); branches.Add(preferences); }
        if (credentials) branches.Add(JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["check_credential_connection"]},"reference":{"type":"string","minLength":1,"maxLength":64}},"required":["operation","reference"]}"""));
        return new JsonObject { ["type"] = "object", ["oneOf"] = branches }.ToJsonString();
    }

    public static TenantAuthorizationIntent Parse(ModelToolCall call)
    {
        if (call.Name != ToolCatalog.RequestAuthorization || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > MaxArgumentBytes)
            throw new ArgumentException("Invalid authorization request.");
        using var document = JsonDocument.Parse(call.ArgumentsJson); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("operation", out var check)
            && check.ValueKind == JsonValueKind.String && check.GetString() == "check_credential_connection")
        {
            Require(root, ["operation", "reference"]);
            if (root.GetProperty("reference").ValueKind != JsonValueKind.String) throw new ArgumentException("A bound credential reference is required.");
            return new(null, null, AgentCore.Domain.Credentials.CredentialRules.Reference(root.GetProperty("reference").GetString()!));
        }
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
