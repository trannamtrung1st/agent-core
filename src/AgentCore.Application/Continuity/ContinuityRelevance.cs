using System.Text.RegularExpressions;

namespace AgentCore.Application.Continuity;

/// <summary>Lexical relevance for canonical source projections; no private source access.</summary>
public static class ContinuityRelevance
{
    public static int Score(string text, string? query) => string.IsNullOrWhiteSpace(query) ? 0 :
        Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
            .Select(m => m.Value).Distinct(StringComparer.Ordinal)
            .Count(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
