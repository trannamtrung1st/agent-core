using System.Net.Http;
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
        catch (HttpRequestException)
        {
            return Error(
                request.Url.ToString(),
                "transport_error",
                "Public web fetch failed before a response was received.");
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

        var decoded = DecodeText(body, contentType);
        if (decoded is null)
        {
            return Error(finalUrl, "invalid_text", "Response body could not be decoded with the declared charset.");
        }

        var text = mediaType.Contains("html", StringComparison.Ordinal)
            ? PublicWebHtmlTextExtractor.Extract(decoded)
            : decoded;

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

    private static string? DecodeText(byte[] bytes, string? contentType)
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

    private static PublicWebFetchResult Error(string finalUrl, string? code, string? message) =>
        new(finalUrl, null, "", false, code, message);
}
