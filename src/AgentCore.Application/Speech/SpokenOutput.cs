using System.Text.RegularExpressions;

namespace AgentCore.Application.Speech;

public static class SpokenOutput
{
    /// <summary>
    /// Pathological runtime safety bound only; conversational length is not clipped here.
    /// </summary>
    public const int SafetyMaxChars = 32_000;

    public const string StructuredLeadIn = "I've put the detailed answer on screen.";

    public static string ForPlayback(string? speechText, string displayText)
    {
        if (!string.IsNullOrWhiteSpace(speechText))
        {
            var explicitSpeech = speechText.Trim();
            if (LooksLikeFileDump(explicitSpeech))
            {
                return StructuredLeadIn;
            }

            return ApplySafetyCap(explicitSpeech);
        }

        var display = displayText ?? string.Empty;
        if (LooksLikeStructuredDisplay(display) || LooksLikeFileDump(display))
        {
            return StructuredLeadIn;
        }

        var prose = StripMarkdown(display);
        if (string.IsNullOrWhiteSpace(prose))
        {
            return string.Empty;
        }

        return ApplySafetyCap(prose);
    }

    /// <summary>
    /// True when the runtime should persist a derived <c>SpeechText</c> on the envelope
    /// (model omitted <c>[[speech:]]</c> but the TTS coordinate string differs from display).
    /// </summary>
    public static bool ShouldPersistDerivedSpeechText(string spoken, string displayText)
    {
        return spoken.Length > 0
            && !string.Equals(spoken, displayText, StringComparison.Ordinal);
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
        var nonEmptyLines = lines.Count(line => !string.IsNullOrWhiteSpace(line));
        if (nonEmptyLines == 0)
        {
            return false;
        }

        var listLines = lines.Count(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)
            || Regex.IsMatch(line.TrimStart(), @"^\d+\.\s"));
        if (listLines >= 3 && listLines == nonEmptyLines)
        {
            return true;
        }

        if (listLines >= 4 && listLines * 2 >= nonEmptyLines)
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

    private static string ApplySafetyCap(string text)
    {
        if (text.Length <= SafetyMaxChars)
        {
            return text;
        }

        return text[..SafetyMaxChars];
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
