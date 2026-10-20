using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantCredentialDiscoveryArguments(CredentialDiscoveryRequest Page, string? Family, string Json);
public static class TenantCredentialDiscoveryContract
{
    public static bool IsFamilyTool(string tool) => tool is ToolCatalog.EmailAccounts or ToolCatalog.DriveAccounts;
    public static bool IsTool(string tool) => tool == ToolCatalog.CredentialsList || IsFamilyTool(tool);
    public static IReadOnlyList<string> Families(AgentDefinition definition) => new[] { ToolCatalog.EmailAccounts, ToolCatalog.DriveAccounts }.Where(t => RolePermissions.AllowsTool(definition, t)).Select(t => Family(t)!).ToArray();
    public static string? Family(string tool) => tool switch { ToolCatalog.EmailAccounts => "email", ToolCatalog.DriveAccounts => "drive", _ => null };
    public static string? Tool(string? family) => family switch { "email" => ToolCatalog.EmailAccounts, "drive" => ToolCatalog.DriveAccounts, _ => null };
    public static bool Matches(string? family, CredentialPurpose purpose) => family is null || (family == "email" ? purpose == CredentialPurpose.EmailAccount : purpose == CredentialPurpose.GoogleDriveAccount);
    public static bool Allows(AgentDefinition definition, string? family) => family is null || RolePermissions.AllowsTool(definition, Tool(family)!);
    public static bool Allows(AgentDefinition definition, AgentContext? context, string tool) => IsFamilyTool(tool)
        && context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, Configuration.CredentialBindings.Count: > 0 }
        && TenantTaskExecutionContract.Allows(context) && RolePermissions.AllowsTool(definition, tool);
    public static TenantCredentialDiscoveryArguments Parse(string arguments, string? tool = null)
    {
        using var document = JsonDocument.Parse(arguments); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
            || root.EnumerateObject().Any(p => p.Name is not ("cursor" or "limit" or "family")) || tool is not null && root.TryGetProperty("family", out _))
            throw new ArgumentException("Use only exact bounded account discovery fields.");
        string? family = Family(tool ?? "");
        if (root.TryGetProperty("family", out var value))
            family = value.ValueKind == JsonValueKind.String && value.GetString() is "email" or "drive" ? value.GetString() : throw new ArgumentException("Use an offered account family.");
        var node = JsonNode.Parse(arguments)!.AsObject(); node.Remove("family");
        using var page = JsonDocument.Parse(node.ToJsonString()); var input = CredentialDiscovery.Parse(page.RootElement);
        var json = family is null ? JsonSerializer.Serialize(new { cursor = input.Cursor, limit = input.Limit })
            : JsonSerializer.Serialize(new { cursor = input.Cursor, limit = input.Limit, family });
        return new(input, family, json);
    }
    public static ModelToolDefinition Descriptor(string tool) => new(tool,
        "Discover one bounded page of currently readable bound " + (tool == ToolCatalog.EmailAccounts ? "Email" : "Drive") + " account metadata. No account use, provider I/O, send or write. If policy requires Review, request inspect_credential_metadata with identical cursor/limit and family=" + Family(tool) + ". Connection status and authEpoch are saved metadata, not authority or live availability.",
        """{"type":"object","additionalProperties":false,"properties":{"cursor":{"type":["string","null"],"maxLength":64},"limit":{"type":"integer","minimum":1,"maximum":100}}}""");
}
