using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed class SocketsPublicWebTransport(IPublicWebDnsResolver dns) : IPublicWebTransport
{
    public async ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await dns.ResolveAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                foreach (var address in addresses)
                {
                    if (!PublicAddressPolicy.IsAllowed(address))
                    {
                        continue;
                    }

                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token)
                            .ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                throw new PublicWebFetchException(
                    "forbidden_host",
                    "No permitted public address was available for the host.");
            }
        };

        try
        {
            using var client = new HttpMessageInvoker(handler, disposeHandler: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html, text/plain, application/json, application/xml, */*;q=0.1");
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
                response.Content.Headers.ContentType?.MediaType,
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
