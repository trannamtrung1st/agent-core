using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

// Ordinary procedure activation is independent of self-management delegation.
public static class TenantSkillLoadContract
{
    public static bool Allows(AgentRun run) => TenantTaskExecutionContract.Allows(run);
    public static bool Allows(AgentContext? context) => context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution }
        && execution.SessionId == context.OwnedSessionId && TenantTaskExecutionContract.Allows(context);
    public static IReadOnlyList<string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!SkillLoadAdmission.TryParseIds(doc.RootElement, out var ids, out _) || ids.Any(k => !SkillKeys.IsValid(k))
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new ArgumentException("Use one to four distinct origin-qualified Skill keys.");
        return ids;
    }
    public static string Arguments(IEnumerable<string> ids) => JsonSerializer.Serialize(new { ids = ids.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
    public static ModelToolDefinition ForModel(ModelToolDefinition tool, AgentContext context)
    {
        if (!Allows(context)) return TenantCoreProcedureContract.ForModel(tool, context);
        var keys = TenantCoreProcedureContract.Visible(context).Select(s => (JsonNode?)JsonValue.Create(s.Key)).ToArray();
        JsonNode privateKey = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 100, ["pattern"] = "^(definition|instance):.+$" };
        var items = keys.Length == 0 ? privateKey : new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(keys) }, privateKey) };
        return tool with {
        Description = "Load exact procedures from this Run's pinned Skill catalog through the existing activation owner. Private procedures require current independent Agent/Skill reads. If requiresApproval is returned, request inspect_management with read.tool skills.load and identical read.arguments, then retry loading after exact read consent. Read consent does not activate a Skill or grant capabilities. Core management procedures additionally require current management delegation. New or changed content requires a fresh Run; never substitute another key.",
        ParametersJson = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["ids"] = new JsonObject { ["type"] = "array", ["minItems"] = 1,
                ["maxItems"] = SkillActivationLimits.MaxIdsPerLoad, ["uniqueItems"] = true,
                ["items"] = items } },
            ["required"] = new JsonArray("ids") }.ToJsonString()
        };
    }
}
