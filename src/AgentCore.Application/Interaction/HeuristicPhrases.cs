using System.Text;

namespace AgentCore.Application.Interaction;

public static class HeuristicPhrases
{
    private static readonly string[] InterruptPhrases =
    [
        "stop",
        "wait",
        "hang on",
        "hold on",
        "what do you mean",
        "no",
        "thats not what i asked"
    ];

    private static readonly string[] Backchannels =
    [
        "mhm",
        "yeah",
        "right",
        "okay",
        "ok",
        "uh huh",
        "uhhuh"
    ];

    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return string.Join(
            ' ',
            builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool IsExplicitInterrupt(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        return InterruptPhrases.Any(phrase =>
            normalized == phrase || normalized.StartsWith(phrase + " ", StringComparison.Ordinal));
    }

    public static bool IsBackchannel(string text)
    {
        var normalized = Normalize(text);
        return Backchannels.Contains(normalized);
    }
}
