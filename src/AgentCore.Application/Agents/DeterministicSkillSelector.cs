using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public interface ISkillSelector
{
    IReadOnlyList<string> Select(AgentDefinition definition, string? triggerText);
}

public sealed class DeterministicSkillSelector : ISkillSelector
{
    public const int MaxActiveSkills = 3;

    public IReadOnlyList<string> Select(AgentDefinition definition, string? triggerText) =>
        SelectActiveIds(definition, triggerText);

    public static IReadOnlyList<string> SelectActiveIds(AgentDefinition definition, string? triggerText)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SkillList.Count == 0 || string.IsNullOrWhiteSpace(triggerText))
        {
            return [];
        }

        var selected = new List<string>();
        foreach (var skill in definition.SkillList)
        {
            if (selected.Count == MaxActiveSkills)
            {
                break;
            }

            if (skill.ActivationKeywords.Any(keyword =>
                    triggerText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(skill.Id);
            }
        }

        return selected;
    }
}
