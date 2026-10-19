using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantBrowserAuthorizationIntent(string Tool, string ArgumentsJson, bool DownloadArtifacts = false)
{
    public bool Matches(PreparedTenantOperation operation) => operation.BrowserCommand is { } saved && saved.ToolName == Tool && (saved.Artifacts?.Downloads == true) == DownloadArtifacts
        && TenantApprovalRules.BrowserIntent(saved.SessionId, Tool, ArgumentsJson).Arguments == saved.ArgumentsJson;
}

public static class TenantBrowserAuthorizationContract
{
    public static IReadOnlyList<string> Allowed(AgentDefinition definition) => BrowserToolCatalog.Tools.Values
        .Where(t => Supported(t.Name) && RolePermissions.AllowsTool(definition, t.Name)).Select(t => t.Name).ToArray();
    private static bool Supported(string name) => name is not (ToolCatalog.BrowserVisionMouse or ToolCatalog.BrowserGeolocation) && BrowserToolCatalog.TryGet(name, out _);
    public static JsonNode Schema(IReadOnlyList<string> tools)
    {
        var commands = new JsonArray();
        foreach (var name in tools)
        {
            if (!Supported(name) || !BrowserToolCatalog.TryGet(name, out var tool)) continue;
            var arguments = JsonNode.Parse(tool.ParametersJson)!;
            if (name == ToolCatalog.BrowserUpload)
            {
                var ids = arguments["properties"]!["artifactIds"]!;
                ids["uniqueItems"] = true;
                ids["items"]!["maxLength"] = 36;
                ids["items"]!["pattern"] = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
            }
            if (name == "browser.drop") arguments["properties"]!.AsObject().Remove("artifactId");
            commands.Add(new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(name) },
                    ["arguments"] = arguments, ["downloadArtifacts"] = new JsonObject { ["type"] = "boolean", ["description"] = "Explicitly publish accepted downloads as Session Artifacts under independent creation authority; no file read is granted." } }, ["required"] = new JsonArray("tool", "arguments") });
        }
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("run_browser_command") },
                ["command"] = new JsonObject { ["type"] = "object", ["oneOf"] = commands } }, ["required"] = new JsonArray("operation", "command") };
    }
    public static TenantBrowserAuthorizationIntent Parse(JsonElement root)
    {
        Exact(root, "operation", "command"); var command = root.GetProperty("command");
        if (command.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an exact Browser command object.");
        Exact(command, command.TryGetProperty("downloadArtifacts", out var downloads) ? ["tool", "arguments", "downloadArtifacts"] : ["tool", "arguments"]);
        if (downloads.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Use a boolean download publication choice.");
        if (command.GetProperty("tool").ValueKind != JsonValueKind.String || command.GetProperty("tool").GetString() is not { } tool || !Supported(tool))
            throw new ArgumentException("Use a supported exact Browser command.");
        // Identity is supplied by Core at preparation; this validates the closed typed argument schema only.
        var parsed = TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"), tool, command.GetProperty("arguments").GetRawText());
        if (downloads.ValueKind == JsonValueKind.True && parsed.ReadOnly) throw new ArgumentException("Download publication requires a state-changing Browser command.");
        return new(tool, parsed.Arguments, downloads.ValueKind == JsonValueKind.True);
    }
    private static void Exact(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an exact Browser command object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != keys.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !keys.Contains(n, StringComparer.Ordinal)))
            throw new ArgumentException("Use only exact Browser command fields.");
    }
}
