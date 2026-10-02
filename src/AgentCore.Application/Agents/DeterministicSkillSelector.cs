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

            if (skill.ActivationKeywords.Any(keyword => MatchesKeyword(triggerText, keyword)))
            {
                selected.Add(skill.Id);
            }
        }

        return selected;
    }

    private static bool MatchesKeyword(string triggerText, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return false;
        }

        var start = 0;
        while (start <= triggerText.Length - keyword.Length)
        {
            var found = triggerText.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var end = found + keyword.Length;
            var before = found == 0 || !char.IsLetterOrDigit(triggerText[found - 1]);
            var after = end >= triggerText.Length || !char.IsLetterOrDigit(triggerText[end]);
            if (!after && (triggerText[end] is 's' or 'S') && (end + 1 >= triggerText.Length || !char.IsLetterOrDigit(triggerText[end + 1])))
            {
                after = true;
            }

            if (before && after)
            {
                return true;
            }

            start = found + 1;
        }

        return false;
    }
}
