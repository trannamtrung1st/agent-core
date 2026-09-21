using System.Text;
using AngleSharp.Html.Parser;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicWebHtmlTextExtractor
{
    private static readonly HtmlParser Parser = new();

    public static string Extract(string html)
    {
        var document = Parser.ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script,style,noscript"))
        {
            element.Remove();
        }

        var text = document.Body?.TextContent ?? document.DocumentElement.TextContent;
        return NormalizeWhitespace(text);
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }

                continue;
            }

            previousWhitespace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
