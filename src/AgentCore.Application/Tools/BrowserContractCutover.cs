using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>Reject retired executable instructions; never translate or rewrite owner publications.</summary>
public static class BrowserContractCutover
{
    public const int Version = 1;
    public const string Message = "The pinned browser instructions use the retired element-ref contract. Publish or adopt current direct-target instructions and start a new Session; historical data is retained.";

    public static bool Retired(string? instructions) => instructions is not null
        && new[] { "scopeRef", "targetRef", "opaque ref", "password ref", "checkbox ref", "file-input ref" }
            .Any(token => instructions.Contains(token, StringComparison.OrdinalIgnoreCase));

    public static void EnsureCurrent(AgentDefinition definition, IReadOnlyList<EffectiveSkill>? catalog = null)
    {
        if (Retired(definition.SystemInstructions) || definition.SkillList.Any(skill => Retired(skill.Procedure))
            || (catalog ?? []).Any(skill => Retired(skill.Procedure)))
            throw AgentCoreErrors.Conflict(Message);
    }
}
