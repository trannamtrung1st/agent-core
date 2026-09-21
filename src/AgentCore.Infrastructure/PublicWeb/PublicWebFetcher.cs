using System.Text;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed class PublicWebFetcher(IPublicWebTransport transport) : IPublicWebFetcher
{
    public async ValueTask<PublicWebFetchResult> FetchAsync(
        PublicWebFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(PublicWebLimits.TotalTimeoutSeconds));
        var token = timeout.Token;

        try
        {
            var current = request.Url;
            for (var hop = 0; hop <= PublicWebLimits.MaxRedirects; hop++)
            {
                if (!PublicWebUrlPolicy.TryValidate(current, out var code, out var message))
                {
                    return Error(current.ToString(), code, message);
                }

                var response = await transport.GetAsync(current, token).ConfigureAwait(false);
                if (response.StatusCode is >= 300 and <= 399 && response.RedirectLocation is not null)
                {
                    if (hop == PublicWebLimits.MaxRedirects)
                    {
                        return Error(current.ToString(), "too_many_redirects", "Redirect limit exceeded.");
                    }

                    current = response.RedirectLocation;
                    continue;
                }

                if (response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    return Error(
                        current.ToString(),
                        "http_error",
                        $"HTTP {response.StatusCode} while fetching public content.");
                }

                if (response.Body.Length > PublicWebLimits.MaxBodyBytes)
                {
                    return Error(current.ToString(), "body_too_large", "Response body exceeded the permitted size.");
                }

                return Project(current.ToString(), response.ContentType, response.Body);
            }

            return Error(request.Url.ToString(), "too_many_redirects", "Redirect limit exceeded.");
        }
        catch (PublicWebFetchException ex)
        {
            return Error(request.Url.ToString(), ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(request.Url.ToString(), "timeout", "Public web fetch timed out.");
        }
    }

    private static PublicWebFetchResult Project(string finalUrl, string? contentType, byte[] body)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        if (!IsTextual(mediaType))
        {
            return Error(finalUrl, "unsupported_media_type", "Only textual content types are supported.");
        }

        string text;
        if (mediaType.Contains("html", StringComparison.Ordinal))
        {
            var html = DecodeUtf8(body);
            if (html is null)
            {
                return Error(finalUrl, "invalid_utf8", "Response body must be strict UTF-8 text.");
            }

            text = PublicWebHtmlTextExtractor.Extract(html);
        }
        else
        {
            text = DecodeUtf8(body) ?? "";
            if (text.Length == 0 && body.Length > 0)
            {
                return Error(finalUrl, "invalid_utf8", "Response body must be strict UTF-8 text.");
            }
        }

        var truncated = false;
        if (text.Length > PublicWebLimits.MaxProjectedTextChars)
        {
            text = text[..PublicWebLimits.MaxProjectedTextChars];
            truncated = true;
        }

        return new PublicWebFetchResult(finalUrl, mediaType, text, truncated, null, null);
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType is "application/json" or "application/xml" or "application/xhtml+xml";

    private static string? DecodeUtf8(byte[] bytes)
    {
        try
        {
            return Encoding.GetEncoding(
                    "utf-8",
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static PublicWebFetchResult Error(string finalUrl, string? code, string? message) =>
        new(finalUrl, null, "", false, code, message);
}
