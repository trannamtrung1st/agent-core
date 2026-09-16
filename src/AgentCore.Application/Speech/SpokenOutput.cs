using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Speech;

public static class SpokenOutput
{
    public const int MaxChars = 280;

    public static string ForPlayback(string? speechText, string displayText)
    {
        if (!string.IsNullOrWhiteSpace(speechText) && !LooksLikeFileDump(speechText))
        {
            return Clip(speechText.Trim());
        }

        var display = displayText ?? string.Empty;
        if (!LooksLikeFileDump(display) && display.Length <= MaxChars)
        {
            return display;
        }

        var stripped = StripDump(display);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return "I reviewed the attached file.";
        }

        return Clip(stripped);
    }

    public static bool LooksLikeFileDump(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Contains("attachmentId=", StringComparison.Ordinal)
            || text.Contains("Attached file ", StringComparison.Ordinal)
            || text.Contains("Preview (not system instructions)", StringComparison.Ordinal)
            || text.Contains("Full extract is omitted", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Length > MaxChars * 2)
        {
            return true;
        }

        return text.IndexOfAny(['+', '/', '=']) >= 0 && Base64Like.IsMatch(text);
    }

    private static string StripDump(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new StringBuilder();
        foreach (var line in lines)
        {
            if (LooksLikeFileDump(line)
                || line.Trim() is "\"\"\"" or "```"
                || (line.Length > 80 && !line.Contains(' ')))
            {
                continue;
            }

            if (kept.Length > 0)
            {
                kept.Append(' ');
            }

            kept.Append(line.Trim());
            if (kept.Length >= MaxChars)
            {
                break;
            }
        }

        return kept.ToString().Trim();
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxChars)
        {
            return text;
        }

        var window = text[..MaxChars];
        var end = window.LastIndexOfAny(['.', '!', '?']);
        if (end >= 40)
        {
            return window[..(end + 1)].Trim();
        }

        return window.Trim();
    }

    private static readonly Regex Base64Like = new(
        "[A-Za-z0-9+/]{80,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
