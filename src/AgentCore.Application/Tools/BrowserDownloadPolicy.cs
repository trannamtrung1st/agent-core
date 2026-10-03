using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class BrowserDownloadPolicy
{
    public static BrowserDownload Classify(string? fileName, ReadOnlySpan<byte> content)
    {
        var safe = SanitizeFileName(fileName);
        if (!TryAccept(safe, content, out var contentType, out var error))
        {
            return new BrowserDownload(error, safe, null, null);
        }

        return new BrowserDownload(null, safe, contentType, content.ToArray());
    }

    public static bool TryAccept(string? fileName, ReadOnlySpan<byte> content, out string contentType, out string errorCode)
    {
        contentType = "";
        var safe = SanitizeFileName(fileName);
        if (content.Length == 0)
        {
            errorCode = "download_rejected";
            return false;
        }

        if (content.Length > BrowserToolLimits.MaxDownloadBytes)
        {
            errorCode = "download_too_large";
            return false;
        }

        var extension = ExtensionOf(safe);
        var pdf = IsPdf(content);
        var png = IsPng(content);
        if (pdf && extension is ".pdf" or "")
        {
            contentType = "application/pdf";
            errorCode = "";
            return true;
        }

        if (png && extension is ".png" or "")
        {
            contentType = "image/png";
            errorCode = "";
            return true;
        }

        if (extension == ".csv" && IsText(content) && !pdf && !png)
        {
            contentType = "text/csv";
            errorCode = "";
            return true;
        }

        if (extension == ".txt" && IsText(content) && !pdf && !png)
        {
            contentType = "text/plain";
            errorCode = "";
            return true;
        }

        errorCode = "download_rejected";
        return false;
    }

    public static string SanitizeFileName(string? fileName)
    {
        var leaf = (fileName ?? "").Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..")
        {
            return "download";
        }

        var builder = new System.Text.StringBuilder(Math.Min(leaf.Length, 80));
        foreach (var ch in leaf)
        {
            if (builder.Length == 80)
            {
                break;
            }

            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_');
        }

        var sanitized = builder.ToString().Trim('.');
        return sanitized.Length == 0 ? "download" : sanitized;
    }

    private static string ExtensionOf(string sanitized)
    {
        var dot = sanitized.LastIndexOf('.');
        if (dot <= 0 || dot == sanitized.Length - 1)
        {
            return "";
        }

        return sanitized[dot..].ToLowerInvariant();
    }

    private static bool IsPdf(ReadOnlySpan<byte> content) =>
        content.Length >= 5
        && content[0] == (byte)'%'
        && content[1] == (byte)'P'
        && content[2] == (byte)'D'
        && content[3] == (byte)'F'
        && content[4] == (byte)'-';

    private static bool IsPng(ReadOnlySpan<byte> content) =>
        content.Length >= 8
        && content[0] == 0x89
        && content[1] == 0x50
        && content[2] == 0x4E
        && content[3] == 0x47
        && content[4] == 0x0D
        && content[5] == 0x0A
        && content[6] == 0x1A
        && content[7] == 0x0A;

    private static bool IsText(ReadOnlySpan<byte> content)
    {
        foreach (var value in content)
        {
            if (value < 32 && value is not (9 or 10 or 13))
            {
                return false;
            }
        }

        return true;
    }
}
