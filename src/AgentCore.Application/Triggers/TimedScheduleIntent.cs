using System.Text.RegularExpressions;

namespace AgentCore.Application.Triggers;

// Recognize only a single current-turn imperative with a concrete timing phrase.
// Action vocabulary is shared between timing-first and action-first forms.
internal static class TimedScheduleIntent
{
    private static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        "schedule", "greet", "say", "tell", "send", "message", "wake", "notify", "ping", "alert", "nudge", "remind",
        "review", "capture", "consolidate", "update", "check", "run", "write", "overwrite", "refresh",
        "sync", "set", "save", "post", "fetch", "execute", "perform", "create", "modify", "edit",
        "append", "replace", "delete", "remove"
    };

    private static readonly Regex Timing = new(
        @"\b(?<mode>in|after|every)\s+(?:(?<value>\d+)\s*(?<unit>s|secs?|seconds?|m|mins?|minutes?|h|hrs?|hours?|days?|weeks?)|(?<singular>second|minute|hour|day|week))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Qualification = new(
        @"\b(if|unless|maybe|perhaps|whether|hypothetically)\b|[?\r\n""“”]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool HasTiming(string text) => Timing.IsMatch(text);

    internal static bool Matches(string text)
    {
        if (text.Length > 512 || Qualification.IsMatch(text)) return false;
        var command = text.Trim().TrimEnd('.', '!').Trim();
        foreach (var suffix in new[] { " please", " for me", " to me" })
            if (command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                command = command[..^suffix.Length].TrimEnd(',', ' ');
                break;
            }
        foreach (var prefix in new[] { "please ", "i want you to " })
            if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                command = command[prefix.Length..].TrimStart();
                break;
            }

        var matches = Timing.Matches(command);
        if (matches.Count != 1) return false;
        var timing = matches[0];
        if (timing.Groups["value"].Success
            && (!int.TryParse(timing.Groups["value"].Value, out var value) || value <= 0)) return false;
        if (timing.Groups["singular"].Success
            && !timing.Groups["mode"].Value.Equals("every", StringComparison.OrdinalIgnoreCase)) return false;

        // Timing must be at an edge; do not authorize an action embedded in a narrative.
        string action;
        if (timing.Index == 0) action = command[timing.Length..].TrimStart(' ', ',', ':');
        else if (timing.Index + timing.Length == command.Length) action = command[..timing.Index].TrimEnd(' ', ',');
        else return false;
        if (action.StartsWith("please ", StringComparison.OrdinalIgnoreCase)) action = action[7..];
        var separator = action.IndexOf(' ');
        return separator > 0 && Actions.Contains(action[..separator]) && !string.IsNullOrWhiteSpace(action[(separator + 1)..]);
    }
}
