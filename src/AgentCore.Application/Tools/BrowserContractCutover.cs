using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Tools;

/// <summary>Reject retired executable instructions; never translate or rewrite owner publications.</summary>
public static class BrowserContractCutover
{
    public const int Version = 1;
    public const string Message = "The pinned browser instructions use the retired element-ref contract. Publish or adopt current direct-target instructions and start a new Session; historical data is retained.";

    public static bool Retired(string? instructions)
    {
        if (instructions is null) return false;
        // Only executable directives or direct call syntax, not historical prose or
        // warnings about removed parameters. This is validation, never translation.
        foreach (var line in instructions.Split('\n'))
        {
            var directive = Regex.Replace(line.Trim(), @"^(?:[-*]|\d+[.)])\s*", "");
            if (!Regex.IsMatch(directive, @"^(?:use|call|invoke|execute|pass|provide|set|click|fill|select|upload)\b|^browser\.\w+\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var positive = Regex.Replace(directive, @"\b(?:not|never|without|omit|avoid|instead of)\s+(?:using\s+)?(?:scopeRef|targetRef|opaque ref|password ref|checkbox ref|file-input ref)\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (Regex.IsMatch(positive, @"\b(?:scopeRef|targetRef|opaque ref|password ref|checkbox ref|file-input ref)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
            if (Regex.IsMatch(directive, "browser\\.\\w+.*[\"']ref[\"']\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    public static string? Diagnostic(AgentDefinition definition, IReadOnlyList<EffectiveSkill>? catalog = null, IReadOnlyList<string>? activeKeys = null)
    {
        if (Retired(definition.SystemInstructions)) return "Definition systemInstructions: " + Message;
        if (catalog is not null)
        {
            foreach (var skill in catalog.Where(skill => (activeKeys ?? []).Contains(skill.Key, StringComparer.Ordinal)))
                if (Retired(skill.Procedure)) return $"Active Skill {skill.Key} ({skill.Name}): {Message}";
        }
        else
            foreach (var skill in definition.SkillList.Where(skill => skill.DefaultEnabled && skill.Projection == SkillProjection.Always))
                if (Retired(skill.Procedure)) return $"Active Definition Skill {skill.Id} ({skill.Name}): {Message}";
        return null;
    }

    public static void EnsureCurrent(AgentDefinition definition, IReadOnlyList<EffectiveSkill>? catalog = null, IReadOnlyList<string>? activeKeys = null)
    { if (Diagnostic(definition, catalog, activeKeys) is { } message) throw AgentCoreErrors.Conflict(message); }
}
