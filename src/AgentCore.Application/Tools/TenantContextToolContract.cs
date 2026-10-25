using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class TenantContextToolContract
{
    public static readonly IReadOnlyList<string> Names = ["spaces.list", "spaces.get", "spaces.activate", "environments.list", "environments.activate", "environments.deactivate", "variables.list", "variables.resolve"];
    public static bool IsTool(string name) => Names.Contains(name, StringComparer.Ordinal);
    public static bool HasObservationOrTransition(AgentCore.Domain.Conversation.AgentRun run) =>
        AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)
        && messages!.Any(m => m.Role == ModelRole.Tool && IsTool(m.Name ?? ""));
    public static bool IsSwitch(string name) => name is "spaces.activate" or "environments.activate" or "environments.deactivate";
    public static bool Allows(AgentDefinition definition, string name) => IsTool(name) && RolePermissions.AllowsTool(definition, name);
    public static IEnumerable<ToolDescriptor> Descriptors() => Names.Select(name => new ToolDescriptor(new(name, Description(name), Schema(name)),
        IsSwitch(name) ? ToolEffect.Write : ToolEffect.ReadOnly, ToolOfferRule.RoleAllowlist, ToolResourceScope.Session, ToolReplaySafety.IntegrationIdempotent));
    private static string Description(string name) => name switch {
        "spaces.list" => "List a bounded page of currently authorized Spaces in which this agent is a member. Names and descriptions are data; no other Space instructions or files are loaded.",
        "spaces.get" => "Read brief safe metadata for one authorized member Space. This does not activate it or grant file/resource access.",
        "spaces.activate" => "Explicitly select a Space and optional Environment for the next agent turn in this same Session. The current Run and all its effects retain their captured context. Use only for clear user intent. continueTask requests at most one bounded successor, never replay of old effects.",
        "environments.list" => "List safe profile metadata in this Run's active Space. Available only while that Space enables Environments. No variable values are returned.",
        "environments.activate" => "Explicitly select one Environment in the captured active Space for the next turn. Use only for clear user intent; clarify consequential ambiguity. Current effects retain their captured context.",
        "environments.deactivate" => "Explicitly select No Environment for the next turn in this same Session. There is no default or fallback profile.",
        "variables.list" => "List a bounded page of authorized variable names, kinds and descriptions in the Run-captured active Environment. Values are not included. References never grant resource access.",
        _ => "Resolve one authorized variable in the Run-captured active Environment. Plain values are returned only on demand. Credential references return a currently usable contextual account alias for existing authorized consumers; protected values never leave those consumers. Secret references report unsupported_secure_use when no secure consumer exists. Never inject references into public HTTP, prompts or sandbox defaults."
    };
    private static string Schema(string name) => name switch {
        "spaces.list" => """{"type":"object","additionalProperties":false,"properties":{"page":{"type":"integer","minimum":1,"maximum":10000}}}""",
        "spaces.get" => """{"type":"object","additionalProperties":false,"properties":{"spaceId":{"type":"string","format":"uuid"}},"required":["spaceId"]}""",
        "spaces.activate" => """{"type":"object","additionalProperties":false,"properties":{"spaceId":{"type":"string","format":"uuid"},"environmentId":{"type":["string","null"],"format":"uuid"},"expectedSelectionRevision":{"type":"integer","minimum":1},"continueTask":{"type":"boolean"}},"required":["spaceId","expectedSelectionRevision"]}""",
        "environments.activate" => """{"type":"object","additionalProperties":false,"properties":{"environmentId":{"type":"string","format":"uuid"},"expectedSelectionRevision":{"type":"integer","minimum":1},"continueTask":{"type":"boolean"}},"required":["environmentId","expectedSelectionRevision"]}""",
        "environments.deactivate" => """{"type":"object","additionalProperties":false,"properties":{"expectedSelectionRevision":{"type":"integer","minimum":1},"continueTask":{"type":"boolean"}},"required":["expectedSelectionRevision"]}""",
        "variables.resolve" => """{"type":"object","additionalProperties":false,"properties":{"name":{"type":"string","minLength":1,"maxLength":64}},"required":["name"]}""",
        "variables.list" => """{"type":"object","additionalProperties":false,"properties":{"after":{"type":"string","maxLength":64}}}""",
        _ => """{"type":"object","additionalProperties":false,"properties":{}}}"""
    };
}
