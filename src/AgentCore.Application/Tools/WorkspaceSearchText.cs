using System.Text;

namespace AgentCore.Application.Tools;

internal static class WorkspaceSearchText
{
    public static bool TryReadText(byte[] bytes, out string text)
    {
        text = "";
        var sample = Math.Min(bytes.Length, 8192);
        for (var index = 0; index < sample; index++)
        {
            if (bytes[index] == 0)
            {
                return false;
            }
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static string Snippet(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var start = Math.Max(0, index - 40);
        var length = Math.Min(text.Length - start, WorkspaceSearchLimits.MaxSnippetChars);
        var snippet = text.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ');
        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < text.Length)
        {
            snippet += "…";
        }

        return snippet;
    }
}
