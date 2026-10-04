using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Domain.Definitions;

public static class SkillIds
{
    public const string Pattern = "^[a-z][a-z0-9._-]{0,63}$";
    private static readonly Regex Valid = new(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? id) => !string.IsNullOrWhiteSpace(id) && Valid.IsMatch(id);

    public static string FromName(string name, IEnumerable<string> existing)
    {
        var slug = new StringBuilder();
        foreach (var character in name.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        var stem = slug.ToString().TrimEnd('-');
        if (stem.Length == 0 || stem[0] is < 'a' or > 'z') stem = "skill-" + stem;
        stem = stem[..Math.Min(stem.Length, 64)].TrimEnd('-');
        var occupied = existing.ToHashSet(StringComparer.Ordinal);
        if (!occupied.Contains(stem)) return stem;
        for (var suffix = 2; suffix <= 999; suffix++)
        {
            var tail = "-" + suffix;
            var candidate = stem[..Math.Min(stem.Length, 64 - tail.Length)].TrimEnd('-') + tail;
            if (!occupied.Contains(candidate)) return candidate;
        }
        throw new ArgumentException("No available Skill identity remains for this name.");
    }
}
