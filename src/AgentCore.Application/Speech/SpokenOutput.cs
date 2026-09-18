using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Speech;

public static class SpokenOutput
{
    public const int MaxChars = 280;
    public const string StructuredLeadIn = "I've put the detailed answer on screen.";

    public static string ForPlayback(string? speechText, string displayText)
    {
        if (!string.IsNullOrWhiteSpace(speechText) && !LooksLikeFileDump(speechText))
        {
            return Clip(speechText.Trim());
        }

        var display = displayText ?? string.Empty;
        if (LooksLikeStructuredDisplay(display))
        {
            return StructuredLeadIn;
        }

        if (LooksLikeFileDump(display))
        {
            return Clip(StripDump(display));
        }

        if (display.Length <= MaxChars)
        {
            return Clip(StripMarkdown(display));
        }

        var stripped = StripMarkdown(StripDump(display));
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return StructuredLeadIn;
        }

        return Clip(stripped);
    }

    public static bool LooksLikeStructuredDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("```", StringComparison.Ordinal))
        {
            return true;
        }

        if (MarkdownTable.IsMatch(text))
        {
            return true;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var listLines = lines.Count(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)
            || Regex.IsMatch(line.TrimStart(), @"^\d+\.\s"));
        if (listLines >= 3)
        {
            return true;
        }

        return false;
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

    private static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var stripped = FencedCodeBlock.Replace(text, " ");
        stripped = MarkdownImage.Replace(stripped, " ");
        stripped = MarkdownLink.Replace(stripped, "$1");
        stripped = MarkdownHeading.Replace(stripped, "");
        stripped = MarkdownFence.Replace(stripped, " ");
        stripped = MarkdownEmphasis.Replace(stripped, "");
        return stripped.Trim();
    }

    private static readonly Regex FencedCodeBlock = new("```[\\s\\S]*?```", RegexOptions.Compiled);
    private static readonly Regex MarkdownTable = new(@"^\|.+\|\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeading = new(@"^#{1,6}\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownFence = new("```+", RegexOptions.Compiled);
    private static readonly Regex MarkdownEmphasis = new(@"[*_`]{1,3}", RegexOptions.Compiled);

    private static readonly Regex Base64Like = new(
        "[A-Za-z0-9+/]{80,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
