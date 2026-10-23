using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Composer;

internal static class ComposerInputRules
{

    public static IReadOnlyList<string> ActiveSkills(AgentDefinition definition, IReadOnlyList<EffectiveSkill> catalog, IEnumerable<string> explicitKeys)
    {
        var requested = explicitKeys.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in requested)
        {
            if (!catalog.Any(s => s.Key == key)) throw AgentCoreErrors.Validation($"Selected Skill {key} is unavailable. Remove or select it again before retrying.");
        }
        var active = catalog.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).Concat(requested).Distinct(StringComparer.Ordinal).ToArray();
        try { SkillPolicy.ValidateActive(catalog, active); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Selected Skills and Always Skills exceed the 8000-character procedure budget. Remove a Skill and retry."); }
        if (requested.Length > 0) BrowserContractCutover.EnsureCurrent(definition, catalog, active);
        return active;
    }
}
