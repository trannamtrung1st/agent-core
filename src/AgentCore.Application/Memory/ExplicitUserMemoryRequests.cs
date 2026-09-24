using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Memory;

public static partial class ExplicitUserMemoryRequests
{
    public sealed record Parsed(MemoryKind Kind, string Subject, string Content);

    public static bool TryParse(string? text, out Parsed? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (RememberMySubject().IsMatch(trimmed))
        {
            var match = RememberMySubject().Match(trimmed);
            var subject = match.Groups["subject"].Value.Trim();
            var content = match.Groups["content"].Value.Trim();
            if (IsUsable(subject, content))
            {
                parsed = new Parsed(MemoryKind.Fact, subject, content);
                return true;
            }
        }

        if (RememberForLater().IsMatch(trimmed))
        {
            var match = RememberForLater().Match(trimmed);
            var content = match.Groups["content"].Value.Trim();
            if (content.Length > 0 && content.Length <= MemoryLimits.MaxContentCharacters)
            {
                var subject = SubjectForGenericFact(content);
                parsed = new Parsed(MemoryKind.Fact, subject, content);
                return true;
            }
        }

        return false;
    }

    public static string SubjectForGenericFact(string content)
    {
        var collapsed = StructuredMemoryItem.CollapseSubject(content);
        if (collapsed.Length == 0)
        {
            return "Remembered fact";
        }

        return collapsed.Length <= MemoryLimits.MaxSubjectCharacters
            ? collapsed
            : collapsed[..MemoryLimits.MaxSubjectCharacters];
    }

    private static bool IsUsable(string subject, string content) =>
        subject.Length > 0
        && content.Length > 0
        && subject.Length <= MemoryLimits.MaxSubjectCharacters
        && content.Length <= MemoryLimits.MaxContentCharacters;

    [GeneratedRegex(
        @"^\s*(?:please\s+)?remember\s+(?:that\s+)?(?:(?:my|the)\s+)(?<subject>.+?)\s+is\s+(?<content>.+?)(?:\s+for\s+later)?\s*[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RememberMySubject();

    [GeneratedRegex(
        @"^\s*(?:please\s+)?remember\s+(?:that\s+)?(?<content>.+?)\s+for\s+later\s*[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RememberForLater();
}
