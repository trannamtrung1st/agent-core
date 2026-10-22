using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

// Core procedures are immutable public instructions. Loading one does not read a
// tenant Skill or authorize any of the management operations mentioned in its text.
public static class TenantCoreProcedureContract
{
    private static bool Shape(TrustedExecutionContext? execution, AgentRunOutputContract output, AgentRunTaskIntent? intent) =>
        execution is { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
        && output == AgentRunOutputContract.ConversationResponse && intent?.Source != AgentRunTaskSource.Automation;
    public static bool Allows(AgentRun run) => Shape(run.Admission.ExecutionContext, run.Admission.OutputContract, run.Admission.TaskIntent)
        && run.Admission.Configuration!.Definition.SelfManagement is { } policy && ManagementTools.Enabled(policy);
    public static IReadOnlyList<EffectiveSkill> Visible(AgentContext context)
    {
        if (!Shape(context.ExecutionContext, context.OutputContract, context.RunTaskIntent)
            || context is not { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, Harness: { } live }
            || context.ExecutionContext!.SessionId != context.OwnedSessionId) return [];
        var restrictive = live with { Policy = ManagementPolicyResolver.Restrict(context.Definition.SelfManagement, live.Policy) };
        return ManagementBuiltInSkills.Visible(context.PinnedSkillCatalog ?? [], restrictive, initial: false)
            .Where(s => ManagementBuiltInSkills.IsBuiltIn(s) && ManagementBuiltInSkills.Catalog.Any(c => c.Key == s.Key)).ToArray();
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool, AgentContext context) => tool with {
        Description = "Load only an offered immutable Core management procedure from this Run's pinned catalog. Current delegation and Built-in choices may remove it immediately. Procedures are instructions, not IAM, source-read authority or consent. Use only currently offered tools; unavailable management operations require owner setup. Tenant and Definition Skill loading is unavailable through this operation.",
        ParametersJson = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject { ["ids"] = new JsonObject { ["type"] = "array", ["minItems"] = 1,
                ["maxItems"] = 2, ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string",
                    ["enum"] = new JsonArray(Visible(context).Select(s => (JsonNode?)JsonValue.Create(s.Key)).ToArray()) } } },
            ["required"] = new JsonArray("ids") }.ToJsonString()
    };
}
