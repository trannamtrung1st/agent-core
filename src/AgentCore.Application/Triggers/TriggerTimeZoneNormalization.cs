using System.Text.RegularExpressions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static partial class TriggerTimeZoneNormalization
{
    public static string Resolve(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TriggerTimeZone.Require(timeZoneId);
        }

        var trimmed = timeZoneId.Trim();
        if (TryMapAlias(trimmed, out var mapped))
        {
            return TriggerTimeZone.Require(mapped);
        }

        return TriggerTimeZone.Require(trimmed);
    }

    private static bool TryMapAlias(string text, out string ianaId)
    {
        ianaId = "";
        var normalized = AliasKey(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (VietnamAlias().IsMatch(normalized))
        {
            ianaId = "Asia/Ho_Chi_Minh";
            return true;
        }

        return false;
    }

    private static string AliasKey(string text)
    {
        var lowered = text.ToLowerInvariant();
        lowered = Regex.Replace(lowered, @"\btime\b", "", RegexOptions.CultureInvariant);
        lowered = Regex.Replace(lowered, @"[^a-z0-9]+", " ", RegexOptions.CultureInvariant);
        return lowered.Trim();
    }

    [GeneratedRegex(@"\b(viet\s*nam|vietnam|vn|ho\s*chi\s*minh|saigon|hanoi)\b", RegexOptions.CultureInvariant)]
    private static partial Regex VietnamAlias();
}
