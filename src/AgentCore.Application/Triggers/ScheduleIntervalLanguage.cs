using System.Text.RegularExpressions;

namespace AgentCore.Application.Triggers;

public static partial class ScheduleIntervalLanguage
{
    public static int? TryParseIntervalSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Contains("every minute", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"\bevery\s+1\s*m(in|inute|inutes)?\b"))
        {
            return 60;
        }

        if (normalized.Contains("every hour", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"\bevery\s+1\s*h(our|ours)?\b"))
        {
            return 3600;
        }

        var match = EveryInterval().Match(normalized);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["value"].Value, out var value) || value <= 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        return unit switch
        {
            "s" or "sec" or "second" or "seconds" => value,
            "m" or "min" or "minute" or "minutes" => value * 60,
            "h" or "hour" or "hours" => value * 3600,
            _ => null
        };
    }

    public static bool LooksLikeIntervalCorrection(string text) =>
        TryParseIntervalSeconds(text) is not null
        || text.Contains("every minute", StringComparison.OrdinalIgnoreCase)
        || text.Contains("every hour", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bevery\s+(?<value>\d+)\s*(?<unit>s|sec|second|seconds|m|min|minute|minutes|h|hour|hours)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EveryInterval();
}
