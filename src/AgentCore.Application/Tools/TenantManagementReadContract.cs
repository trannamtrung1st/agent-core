using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Tools;

public sealed record TenantManagementReadArguments(string Tool, string Json, string? Section);
public sealed record TenantManagementReadIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.ManagementRead is { } read && read.ToolName == Tool && read.ArgumentsJson == ArgumentsJson;
}
public static class TenantManagementReadContract
{
    public static readonly IReadOnlyList<string> Tools = Array.AsReadOnly(new[] { "harness.inspect", "configuration.inspect", "management.inspect", "settings.list", "settings.inspect", "automation.preferences.inspect", "skills.list", "skills.inspect", "resources.list", "resources.inspect", "skills.load" });
    public static bool IsTool(string tool) => Tools.Contains(tool);
    public static bool IsChildTool(string tool) => tool is "skills.list" or "skills.inspect" or "resources.list" or "resources.inspect";
    public static bool Allows(AgentDefinition definition, string tool) => tool == ToolCatalog.SkillsLoad ? true : IsTool(tool) && (tool == HarnessChatTools.Inspect || RolePermissions.AllowsTool(definition, tool))
        && definition.SelfManagement is { } policy && ManagementTools.Enabled(policy)
        && (ManagementTools.Area(tool) is not { } scope || policy.Allows(scope));
    public static bool Shape(TrustedExecutionContext? execution, AgentRunOutputContract output, AgentRunTaskIntent? intent) =>
        execution is { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
        && output == AgentRunOutputContract.ConversationResponse && intent?.Source != AgentRunTaskSource.Automation;
    public static bool Allows(AgentRun run) => Shape(run.Admission.ExecutionContext, run.Admission.OutputContract, run.Admission.TaskIntent);
    public static IReadOnlyList<string> Allowed(AgentDefinition definition, AgentContext? context)
    {
        if (context is not { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
            || execution.SessionId != context.OwnedSessionId) return [];
        return Tools.Where(t => t == ToolCatalog.SkillsLoad ? TenantSkillLoadContract.Allows(context)
            : context.Harness is { } live && Shape(execution, context.OutputContract, context.RunTaskIntent)
                && Allows(definition, t) && ManagementTools.Allows(t, definition, live)).ToArray();
    }
    public static TenantManagementReadArguments Parse(string tool, string json)
    {
        if (!IsTool(tool) || Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Use an offered bounded management observation.");
        if (tool == ToolCatalog.SkillsLoad)
        {
            var ids = TenantSkillLoadContract.Parse(json);
            if (ids.Any(k => k.StartsWith("builtin:", StringComparison.Ordinal))) throw new ArgumentException("Core procedures do not require private read consent.");
            return new(tool, TenantSkillLoadContract.Arguments(ids), null);
        }
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (tool == "harness.inspect") TenantApprovalToolContract.Require(root, []);
        else ConfigurationTools.Validate(tool, root);
        if (IsChildTool(tool))
        {
            if (tool.EndsWith(".list", StringComparison.Ordinal)) return new(tool, "{}", null);
            var key = root.GetProperty("key").GetString()!;
            if (tool == "skills.inspect" ? !SkillKeys.IsValid(key) : !AgentCore.Application.Security.TenantAgentChildScopes.IsResourceKey(key))
                throw new ArgumentException("Use an exact origin-qualified child key.");
            return new(tool, JsonSerializer.Serialize(new { key }), null);
        }
        if (tool == "configuration.inspect")
        {
            var fields = root.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value.Clone());
            return new(tool, JsonSerializer.Serialize(fields), null);
        }
        return tool == "settings.inspect" ? new(tool, JsonSerializer.Serialize(new { section = root.GetProperty("section").GetString() }), root.GetProperty("section").GetString())
            : new(tool, "{}", null);
    }
    public static JsonNode Arguments(string tool) => JsonNode.Parse(ToolRegistry.Get(tool).ModelDefinition.ParametersJson)!;
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with { Description = tool.Name switch {
        "skills.list" => "List a bounded window of independently readable Skill metadata for this Agent in delegated Skills. No private procedure or authority is loaded. Inspect an exact key for its complete bounded procedure. Required read Review uses inspect_management with the exact tool and arguments.",
        "skills.inspect" => "Inspect one exact origin-qualified Skill in this Agent under current agents.read and skills.read. Returns its complete bounded procedure and capability requirements as untrusted data; this does not activate it, grant a capability or change this Run. Required read Review uses inspect_management with the exact key.",
        "resources.list" => "List a bounded window of independently readable resource metadata for this Agent in delegated Resources. Resource bytes and private dependency names are absent. Required read Review uses inspect_management with the exact tool and arguments.",
        "resources.inspect" => "Inspect one exact origin-qualified resource owned by this Agent under current agents.read and resources.read. Returns hash-verified bounded UTF-8 text when supported, with an explicit excerpt marker; binary bytes and private dependencies are withheld. This does not activate or change a resource. Required read Review uses inspect_management with the exact key.",
        "harness.inspect" => "Inspect this Agent's current delegated Harness areas, exact versions/revisions, bounded instructions and independently readable Skill/resource metadata. Private procedures and bytes require their own read tools. Current Runs remain pinned. Required compound read Review uses inspect_management with this exact tool and arguments; no write or future authority is granted.",
        "configuration.inspect" => "Inspect this Agent's delegated effective settings, independently readable child/connection metadata and actual configuration blocker. Optional exact target or goal narrows inspection. Returns only bounded safe remedy options; Definition/host/setup and current Run pins remain independent. Required compound read Review uses inspect_management with these exact arguments. No secret, write or execution authority is granted.",
        "management.inspect" => "Inspect this Agent's current delegated self-management policy, exact Instance/policy revisions and field sources under current agents.read. This observation grants no permission or effect. Required read Review withholds values; request inspect_management with this exact tool and arguments.",
        "settings.list" => "List this Agent's setting sections and override indicators under current agents.read and delegated Settings. Inspect an exact section for bounded values, defaults, constraints and field sources. Required read Review withholds this saved window; request inspect_management. No write or future authority is granted.",
        "settings.inspect" => "Inspect an exact setting section for this Agent under current agents.read and delegated Settings. Values are bounded snapshots, with explicit string excerpts; sandbox returns only the selected profile reference. Host fields and secrets are withheld. Required read Review uses inspect_management with these exact arguments. Observe the current Instance revision before proposing a write; new values apply to the next Run.",
        _ => "Inspect this Agent's unattended model override, reasoning choice and exact Instance revision under current agents.read and delegated Automation preferences. Required read Review withholds values; request inspect_management with these exact arguments. This does not admit or run an Automation or change a model pin."
    } };
    public static JsonNode Schema(IReadOnlyList<string> tools) => new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("inspect_management") },
            ["read"] = new JsonObject { ["oneOf"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(t) }, ["arguments"] = Arguments(t) },
                ["required"] = new JsonArray("tool", "arguments") }).ToArray()) } }, ["required"] = new JsonArray("operation", "read") };
    public static TenantManagementReadIntent Intent(JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "read"]); var read = root.GetProperty("read"); TenantApprovalToolContract.Require(read, ["tool", "arguments"]);
        var args = Parse(read.GetProperty("tool").GetString()!, read.GetProperty("arguments").GetRawText()); return new(args.Tool, args.Json);
    }
}
