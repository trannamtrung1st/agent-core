using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed class HttpRequestClient(IPublicWebTransport transport) : IHttpRequestClient
{
    public async ValueTask<HttpToolResponse> SendAsync(
        HttpToolRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(PublicWebLimits.TotalTimeoutSeconds));
        var token = timeout.Token;
        try
        {
            if (request.Body.Length > HttpRequestLimits.MaxRequestBodyBytes)
            {
                return Error(request.Url.ToString(), "invalid", "Request body exceeded the permitted size.");
            }

            var current = request.Url;
            var body = request.FollowRedirects ? [] : request.Body;
            var redirects = request.FollowRedirects ? PublicWebLimits.MaxRedirects : 0;
            for (var hop = 0; hop <= redirects; hop++)
            {
                if (!PublicWebUrlPolicy.TryValidate(current, out var code, out var message))
                {
                    return Error(current.ToString(), code, message);
                }

                var response = await transport.SendAsync(
                        new PublicWebOutboundRequest(request.Method, current, request.Headers, hop == 0 ? body : []),
                        token)
                    .ConfigureAwait(false);
                string? redirect = null;
                if (response.RedirectLocation is not null)
                {
                    redirect = response.RedirectLocation.ToString();
                }

                if (response.StatusCode is >= 300 and <= 399 && response.RedirectLocation is not null && request.FollowRedirects)
                {
                    if (hop == redirects)
                    {
                        return Error(current.ToString(), "too_many_redirects", "Redirect limit exceeded.");
                    }

                    current = response.RedirectLocation;
                    continue;
                }

                return Project(current.ToString(), response, redirect);
            }

            return Error(request.Url.ToString(), "too_many_redirects", "Redirect limit exceeded.");
        }
        catch (PublicWebFetchException ex)
        {
            return Error(request.Url.ToString(), ex.Code, ex.Message);
        }
        catch (HttpRequestException)
        {
            return Error(request.Url.ToString(), "transport_error", "HTTP request failed before a response was received.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(request.Url.ToString(), "timeout", "HTTP request timed out.");
        }
    }

    private static HttpToolResponse Project(string finalUrl, PublicWebTransportResponse response, string? redirect)
    {
        if (response.Body.Length > PublicWebLimits.MaxBodyBytes)
        {
            return Error(finalUrl, "body_too_large", "Response body exceeded the permitted size.");
        }

        var mediaType = response.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        if (!IsTextual(mediaType) || !TryDecodeUtf8(response.Body, out var text))
        {
            return new HttpToolResponse(
                response.StatusCode,
                finalUrl,
                string.IsNullOrEmpty(mediaType) ? null : mediaType,
                "",
                false,
                true,
                redirect,
                null,
                null);
        }

        var truncated = false;
        if (text.Length > PublicWebLimits.MaxProjectedTextChars)
        {
            text = text[..PublicWebLimits.MaxProjectedTextChars];
            truncated = true;
        }

        return new HttpToolResponse(
            response.StatusCode,
            finalUrl,
            string.IsNullOrEmpty(mediaType) ? null : mediaType,
            text,
            truncated,
            true,
            redirect,
            null,
            null);
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.Length == 0
        || mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType.Contains("json", StringComparison.Ordinal)
        || mediaType.Contains("xml", StringComparison.Ordinal)
        || mediaType is "application/x-www-form-urlencoded";

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length == 0)
        {
            return true;
        }

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
            text = Encoding.GetEncoding(
                "utf-8",
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static HttpToolResponse Error(string finalUrl, string? code, string? message) =>
        new(0, finalUrl, null, "", false, true, null, code ?? "transport_error", message);
}
