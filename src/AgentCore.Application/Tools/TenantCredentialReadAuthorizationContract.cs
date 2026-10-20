using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantCredentialReadAuthorizationContract
{
    public const string Operation = "inspect_credential_metadata";
    public static bool Allows(AgentDefinition definition, AgentContext? context) => context?.Configuration?.CredentialBindings.Count > 0
        && (RolePermissions.AllowsTool(definition, ToolCatalog.BrowserFillCredential) || RolePermissions.AllowsTool(definition, ToolCatalog.EmailAccounts)
            || RolePermissions.AllowsTool(definition, ToolCatalog.DriveAccounts));
    public static JsonNode Schema(IReadOnlyList<string>? families = null)
    {
        var schema = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["inspect_credential_metadata"]},"arguments":{"type":"object","additionalProperties":false,"properties":{"cursor":{"type":["string","null"],"maxLength":64},"limit":{"type":"integer","minimum":1,"maximum":100},"family":{"type":"string","enum":["email","drive"]}}}},"required":["operation","arguments"]}""")!;
        if (families is not null)
        {
            var properties = schema["properties"]!["arguments"]!["properties"]!.AsObject();
            if (families.Count == 0) properties.Remove("family");
            else properties["family"]!["enum"] = new JsonArray(families.Select(f => JsonValue.Create(f)).ToArray());
        }
        return schema;
    }
    public static string Parse(JsonElement root)
    {
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2 || names.Any(n => n is not ("operation" or "arguments")))
            throw new ArgumentException("Use only the metadata read operation and discovery arguments.");
        return TenantApprovalRules.CredentialReadArguments(root.GetProperty("arguments").GetRawText());
    }
}
