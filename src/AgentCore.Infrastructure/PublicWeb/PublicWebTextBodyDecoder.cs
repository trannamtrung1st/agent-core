using System.Text;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicWebTextBodyDecoder
{
    private static readonly Encoding Utf8Strict = Encoding.GetEncoding(
        "utf-8",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    private static readonly HashSet<string> AllowedCharsets = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8",
        "utf8",
        "us-ascii",
        "ascii",
        "iso-8859-1",
        "latin1",
        "windows-1252"
    };

    public static string? TryDecode(byte[] bytes, string? contentType)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var charset = ReadCharset(contentType);
        Encoding encoding;
        if (charset is null)
        {
            encoding = Utf8Strict;
        }
        else if (!AllowedCharsets.Contains(charset))
        {
            return null;
        }
        else
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                encoding = Encoding.GetEncoding(
                    charset,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static bool ContainsBinaryNull(byte[] bytes, int sampleLimit = 8192)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var sample = Math.Min(bytes.Length, sampleLimit);
        for (var index = 0; index < sample; index++)
        {
            if (bytes[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadCharset(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        foreach (var part in contentType.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["charset=".Length..].Trim().Trim('"');
            }
        }

        return null;
    }
}
