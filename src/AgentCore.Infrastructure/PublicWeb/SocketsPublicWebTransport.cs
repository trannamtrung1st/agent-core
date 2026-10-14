using System.Net;
using System.Net.Http.Headers;
using AgentCore.Infrastructure.Http;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed class SocketsPublicWebTransport(IPublicWebDnsResolver dns, HttpErrorDiagnostics? diagnostics = null,
    Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null) : IPublicWebTransport
{
    public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        SendAsync(
            new PublicWebOutboundRequest(
                HttpMethod.Get.Method,
                uri,
                [new AgentCore.Application.Ports.HttpRequestHeader("Accept", "text/html, text/plain, application/json, application/xml, */*;q=0.1")],
                []),
            cancellationToken);

    public async ValueTask<PublicWebTransportResponse> SendAsync(
        PublicWebOutboundRequest outbound,
        CancellationToken cancellationToken)
    {
        using var handler = PublicWebSocketsHttpFactory.CreateHandler(dns, connect);

        try
        {
            using var client = new HttpMessageInvoker(handler, disposeHandler: true);
            using var request = CreateRequest(outbound);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedBodyAsync(response.Content, outbound.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            diagnostics?.Log((int)response.StatusCode, "public-web", body.AsMemory(0, Math.Min(body.Length, HttpErrorDiagnostics.MaxBodyBytes + 1)));
            Uri? redirect = null;
            if (response.Headers.Location is { } location)
            {
                redirect = location.IsAbsoluteUri ? location : new Uri(outbound.Uri, location);
            }

            return new PublicWebTransportResponse(
                (int)response.StatusCode,
                redirect,
                response.Content.Headers.ContentType?.ToString(),
                body,
                response.Content.Headers.ContentDisposition?.ToString(),
                string.Join(",", response.Content.Headers.ContentEncoding));
        }
        catch (HttpRequestException ex)
        {
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is PublicWebFetchException fetchException)
                {
                    throw fetchException;
                }
            }

            throw;
        }
    }

    private static HttpRequestMessage CreateRequest(PublicWebOutboundRequest outbound)
    {
        var request = new HttpRequestMessage(new HttpMethod(outbound.Method), outbound.Uri);
        string? contentType = null;
        foreach (var header in outbound.Headers)
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header.Value;
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        if (outbound.Body.Length > 0)
        {
            request.Content = new ByteArrayContent(outbound.Body);
            MediaTypeHeaderValue mediaType;
            if (contentType is null)
            {
                mediaType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
            }
            else
            {
                if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed is null)
                {
                    throw new PublicWebFetchException("invalid", "Content-Type is not a permitted media type.");
                }

                mediaType = parsed;
            }

            request.Content.Headers.ContentType = mediaType;
        }

        return request;
    }

    internal static async Task<byte[]> ReadBoundedBodyAsync(HttpContent? content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return [];
        }

        if (maxBytes < 1 || maxBytes > 50 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (content.Headers.ContentLength > maxBytes)
            throw new PublicWebFetchException("body_too_large", "Response body exceeded the permitted size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new PublicWebFetchException("body_too_large", "Response body exceeded the permitted size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

internal sealed class PublicWebFetchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
