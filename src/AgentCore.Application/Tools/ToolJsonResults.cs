using System.Text;

namespace AgentCore.Application.Tools;

internal static class ToolJsonResults
{
    public static string FitJsonWithContentField(int budget, string content, Func<string, bool, string> buildJson)
    {
        var maxBytes = Math.Max(0, budget);
        var truncated = false;
        var current = content;
        while (true)
        {
            var json = buildJson(current, truncated);
            if (Encoding.UTF8.GetByteCount(json) <= maxBytes)
            {
                return json;
            }

            truncated = true;
            if (current.Length == 0)
            {
                return json;
            }

            current = ClipUtf8Prefix(current, Math.Max(0, current.Length - Math.Max(1, current.Length / 8)));
        }
    }

    public static string ClipUtf8Prefix(string text, int maxBytes)
    {
        if (maxBytes <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        return Encoding.UTF8.GetString(bytes.AsSpan(0, ValidUtf8PrefixLength(bytes.AsSpan(0, maxBytes))));
    }

    internal static int ValidUtf8PrefixLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var length = bytes.Length;
        while (length > 0 && (bytes[length - 1] & 0xC0) == 0x80)
        {
            length--;
        }

        if (length == 0)
        {
            return 0;
        }

        var lead = bytes[length - 1];
        var expected = lead switch
        {
            < 0x80 => 1,
            _ when (lead & 0xE0) == 0xC0 => 2,
            _ when (lead & 0xF0) == 0xE0 => 3,
            _ when (lead & 0xF8) == 0xF0 => 4,
            _ => 1
        };

        var available = bytes.Length - (length - 1);
        return available >= expected ? bytes.Length : length - 1;
    }
}
