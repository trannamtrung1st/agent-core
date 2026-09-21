namespace AgentCore.Infrastructure.PublicWeb;

internal sealed class SocketsPublicWebTransport(IPublicWebDnsResolver dns) : IPublicWebTransport
{
    public async ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var handler = PublicWebSocketsHttpFactory.CreateHandler(dns);

        try
        {
            using var client = new HttpMessageInvoker(handler, disposeHandler: true);
            using var request = PublicWebSocketsHttpFactory.CreateGetRequest(uri);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadBoundedBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Uri? redirect = null;
            if (response.Headers.Location is { } location)
            {
                redirect = location.IsAbsoluteUri ? location : new Uri(uri, location);
            }

            return new PublicWebTransportResponse(
                (int)response.StatusCode,
                redirect,
                response.Content.Headers.ContentType?.ToString(),
                body);
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

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return [];
        }

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

            if (buffer.Length + read > PublicWebLimits.MaxBodyBytes)
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
