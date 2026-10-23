using System.Text.RegularExpressions;

namespace AgentCore.Application.Tools;

public static class WorkspaceSearchLimits
{
    public const int DefaultMaxResults = 20;
    public const int MaxResultsCap = 50;
    public const int MaxQueryLength = 200;
    public const int MaxSnippetChars = 160;
    public const int MaxFileBytesToScan = 256 * 1024;
    public const int MaxFilesScanned = 200;
    public const int MaxDepth = 8;
    public const int MaxEntriesScanned = 4096;
}

internal static class WorkspaceGlob
{
    public static bool Matches(string relativePath, string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob) || glob is "*" or "**")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(relativePath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
