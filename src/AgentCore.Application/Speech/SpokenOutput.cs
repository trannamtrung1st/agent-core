using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Speech;

public static class SpokenOutput
{
    /// <summary>
    /// Pathological runtime safety bound only; conversational length is not clipped here.
    /// </summary>
    public const int SafetyMaxChars = 32_000;

    private static readonly HashSet<string> FenceLanguageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "http", "https", "json", "yaml", "yml", "xml", "bash", "sh", "shell", "powershell", "ps1",
        "csharp", "cs", "javascript", "js", "typescript", "ts", "python", "py", "java", "go", "rust", "sql",
        "curl", "docker", "dockerfile", "mermaid", "plantuml", "ascii", "log", "trace", "diff"
    };

    public static string ForPlayback(string? speechText, string displayText)
    {
        if (!string.IsNullOrWhiteSpace(speechText))
        {
            var explicitSpeech = speechText.Trim();
            if (LooksLikeFileDump(explicitSpeech))
            {
                return string.Empty;
            }

            return ApplySafetyCap(explicitSpeech);
        }

        var display = displayText ?? string.Empty;
        if (LooksLikeFileDump(display))
        {
            return string.Empty;
        }

        var prose = DeriveSpeakableProseFromDisplay(display);
        if (string.IsNullOrWhiteSpace(prose) || LooksLikeFileDump(prose))
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

    /// <summary>
    /// Heuristic for tests and diagnostics only; playback selection does not branch on this.
    /// </summary>
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

    /// <summary>
    /// True when display text includes materially rich structure (fences, tables, indented code).
    /// Compatibility fallback must not stitch prose fragments around such regions.
    /// </summary>
    public static bool HasMaterialStructuredContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("```", StringComparison.Ordinal)
            || text.Contains("~~~", StringComparison.Ordinal))
        {
            return true;
        }

        if (MarkdownTable.IsMatch(text))
        {
            return true;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0
                && trimmed.StartsWith('|')
                && trimmed.EndsWith('|'))
            {
                return true;
            }

            if (line.Length > 0 && (line[0] == '\t' || line.StartsWith("    ", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string DeriveSpeakableProseFromDisplay(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (HasMaterialStructuredContent(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized = RemoveFencedRegions(normalized);
        normalized = RemoveIndentedCodeBlocks(normalized);
        normalized = RemoveMarkdownTableLines(normalized);
        normalized = FilterTechnicalLines(normalized);
        normalized = LightMarkdownProse(normalized);
        return HasUsefulProse(normalized) ? normalized.Trim() : string.Empty;
    }

    private static string RemoveFencedRegions(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var backtickOpen = text.IndexOf("```", index, StringComparison.Ordinal);
            var tildeOpen = text.IndexOf("~~~", index, StringComparison.Ordinal);
            if (backtickOpen < 0 && tildeOpen < 0)
            {
                builder.Append(text.AsSpan(index));
                break;
            }

            string marker;
            int open;
            if (backtickOpen < 0 || (tildeOpen >= 0 && tildeOpen < backtickOpen))
            {
                marker = "~~~";
                open = tildeOpen;
            }
            else
            {
                marker = "```";
                open = backtickOpen;
            }

            builder.Append(text.AsSpan(index, open - index));
            var close = text.IndexOf(marker, open + marker.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            index = close + marker.Length;
        }

        return builder.ToString();
    }

    private static string RemoveIndentedCodeBlocks(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] == '\t')
            {
                continue;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static string RemoveMarkdownTableLines(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static string FilterTechnicalLines(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsTechnicalNoiseLine(line))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    private static bool IsTechnicalNoiseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (FenceLanguageLabels.Contains(trimmed))
        {
            return true;
        }

        if (HttpRequestLine.IsMatch(trimmed)
            || HttpStatusLine.IsMatch(trimmed)
            || trimmed.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("curl ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("wget ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (JsonHeavyLine.IsMatch(trimmed))
        {
            return true;
        }

        if (DiagramLine.IsMatch(trimmed))
        {
            return true;
        }

        var structural = trimmed.Count(static ch => ch is '|' or '+' or '-' or '{' or '}' or '[' or ']' or ':' or '"' or '\\');
        if (structural * 3 >= trimmed.Length && trimmed.Length >= 12)
        {
            return true;
        }

        return false;
    }

    private static bool HasUsefulProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        var letters = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                letters++;
            }
        }

        return letters >= 2;
    }

    private static string LightMarkdownProse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var stripped = MarkdownImage.Replace(text, " ");
        stripped = MarkdownLink.Replace(stripped, "$1");
        stripped = MarkdownHeading.Replace(stripped, "$1. ");
        stripped = InlineCode.Replace(stripped, " ");
        stripped = MarkdownEmphasis.Replace(stripped, "");
        stripped = ListMarker.Replace(stripped, "");
        stripped = NumberedListMarker.Replace(stripped, "");
        stripped = HorizontalRule.Replace(stripped, " ");
        stripped = CollapseWhitespace.Replace(stripped, " ");
        return stripped.Trim();
    }

    private static string ApplySafetyCap(string text)
    {
        if (text.Length <= SafetyMaxChars)
        {
            return text;
        }

        return text[..SafetyMaxChars];
    }

    private static readonly Regex MarkdownTable = new(@"^\|.+\|\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeading = new(@"^#{1,6}\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex InlineCode = new(@"`[^`\n]+`", RegexOptions.Compiled);
    private static readonly Regex MarkdownEmphasis = new(@"[*_]{1,3}", RegexOptions.Compiled);
    private static readonly Regex ListMarker = new(@"^\s*[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex NumberedListMarker = new(@"^\s*\d+\.\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex HorizontalRule = new(@"^[-*_]{3,}\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CollapseWhitespace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex HttpRequestLine = new(
        @"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+\S+(\s+HTTP/\d(?:\.\d)?)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HttpStatusLine = new(
        @"^HTTP/\d(?:\.\d)?\s+\d{3}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JsonHeavyLine = new(
        @"^\s*[\{\[].*[\}\]]\s*$|""[^""]+""\s*:\s*",
        RegexOptions.Compiled);
    private static readonly Regex DiagramLine = new(
        @"^[\s\|+\-\\/\[\]<>]{8,}$",
        RegexOptions.Compiled);

    private static readonly Regex Base64Like = new(
        "[A-Za-z0-9+/]{80,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
