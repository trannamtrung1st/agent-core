using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

// Model supplies an exact command proposal, never execution identity, grants or mutable witnesses.
public sealed record TenantSandboxAuthorizationIntent(string ProfileId, SandboxCommandRequest Command)
{
    public bool Matches(PreparedTenantOperation operation)
    {
        if (operation.Kind != TenantOperationKind.SandboxCommand || operation.SandboxCommand is not { } saved || saved.ProfileId != ProfileId) return false;
        return TenantApprovalRules.CanonicalSandboxCommand(Command) == saved.CommandJson;
    }
}

public static class TenantSandboxAuthorizationContract
{
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.SandboxExec)
        && definition.SandboxPolicy is { } policy && definition.ExecutionSandboxProfiles?.Any(p => p.AdmittedForExecution == true
            && policy.AllowedProfileIds.Contains(p.Id, StringComparer.Ordinal)) == true;

    public static JsonNode Schema(IReadOnlyList<string> profileIds)
    {
        var node = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"operation":{"type":"string","enum":["run_sandbox_command"]},"profileId":{"type":"string"},"command":{"type":"object","additionalProperties":false,"properties":{"command":{"type":"string","minLength":1,"maxLength":16384},"argv":{"type":"array","minItems":1,"maxItems":128,"items":{"type":"string"}},"cwd":{"type":"string","minLength":1,"maxLength":4095},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":180},"background":{"type":"boolean"}},"required":["cwd","timeoutSeconds","background"],"oneOf":[{"required":["command"]},{"required":["argv"]}]}},"required":["operation","profileId","command"]}""")!;
        if (profileIds.Count > 0) node["properties"]!["profileId"]!["enum"] = new JsonArray(profileIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return node;
    }
    public static TenantSandboxAuthorizationIntent Parse(JsonElement root)
    {
        Require(root, ["operation", "profileId", "command"], ["operation", "profileId", "command"]);
        var id = root.GetProperty("profileId");
        if (id.ValueKind != JsonValueKind.String || !TenantSandboxProfileRules.ValidId(id.GetString())) throw new ArgumentException("Use an exact admitted profile.");
        var command = root.GetProperty("command");
        Require(command, ["command", "argv", "cwd", "timeoutSeconds", "background"], ["cwd", "timeoutSeconds", "background"]);
        string Read(string key) => command.GetProperty(key).ValueKind == JsonValueKind.String ? command.GetProperty(key).GetString()! : throw new ArgumentException("Invalid command field.");
        var timeout = command.GetProperty("timeoutSeconds"); var background = command.GetProperty("background");
        if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var seconds) || background.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Use explicit command units and flags.");
        string[]? argv = null;
        if (command.TryGetProperty("argv", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String)) throw new ArgumentException("Use a string argv array.");
            argv = list.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }
        var request = new SandboxCommandRequest(command.TryGetProperty("command", out _) ? Read("command") : null, argv, Read("cwd"), seconds, background.GetBoolean(), id.GetString());
        try { SandboxCommands.Validate(request); }
        catch (AgentCoreException) { throw new ArgumentException("Use one bounded command or argv and authorized canonical cwd."); }
        return new(id.GetString()!, request);
    }
    private static void Require(JsonElement value, string[] allowed, string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use an exact command object.");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !allowed.Contains(n, StringComparer.Ordinal))
            || required.Any(n => !names.Contains(n, StringComparer.Ordinal))) throw new ArgumentException("Use only exact command fields.");
    }
}
