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
    public static readonly IReadOnlyList<string> Tools = Array.AsReadOnly(new[] { "harness.inspect", "configuration.inspect", "management.inspect", "settings.list", "settings.inspect", "automation.preferences.inspect" });
    public static bool IsTool(string tool) => Tools.Contains(tool);
    public static bool Allows(AgentDefinition definition, string tool) => IsTool(tool) && (tool == HarnessChatTools.Inspect || RolePermissions.AllowsTool(definition, tool))
        && definition.SelfManagement is { } policy && ManagementTools.Enabled(policy)
        && (ManagementTools.Area(tool) is not { } scope || policy.Allows(scope));
    public static bool Shape(TrustedExecutionContext? execution, AgentRunOutputContract output, AgentRunTaskIntent? intent) =>
        execution is { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
        && output == AgentRunOutputContract.ConversationResponse && intent?.Source != AgentRunTaskSource.Automation;
    public static bool Allows(AgentRun run) => Shape(run.Admission.ExecutionContext, run.Admission.OutputContract, run.Admission.TaskIntent);
    public static IReadOnlyList<string> Allowed(AgentDefinition definition, AgentContext? context) => context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution, Harness: { } live }
        && execution.SessionId == context.OwnedSessionId && Shape(execution, context.OutputContract, context.RunTaskIntent)
        ? Tools.Where(t => Allows(definition, t) && ManagementTools.Allows(t, definition, live)).ToArray() : [];
    public static TenantManagementReadArguments Parse(string tool, string json)
    {
        if (!IsTool(tool) || Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Use an offered bounded management observation.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (tool == "harness.inspect") TenantApprovalToolContract.Require(root, []);
        else ConfigurationTools.Validate(tool, root);
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
