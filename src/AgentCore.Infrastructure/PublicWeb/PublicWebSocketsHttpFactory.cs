using System.Net;
using System.Net.Sockets;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicWebSocketsHttpFactory
{
    internal static SocketsHttpHandler CreateHandler(IPublicWebDnsResolver dns,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null,
        Func<CancellationToken,ValueTask>? authorize = null) =>
        new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await dns.ResolveAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                if (authorize is not null) await authorize(token).ConfigureAwait(false);
                async ValueTask<Stream> Connect(IPAddress address, int port, CancellationToken ct)
                {
                    if (authorize is not null) await authorize(ct).ConfigureAwait(false);
                    var stream = await (connect ?? ConnectSocketAsync)(address, port, ct).ConfigureAwait(false);
                    try { if (authorize is not null) await authorize(ct).ConfigureAwait(false); return stream; }
                    catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
                }
                return await ConnectFirstPermittedAsync(
                        addresses,
                        context.DnsEndPoint.Port,
                        Connect,
                        token)
                    .ConfigureAwait(false);
            }
        };

    internal static async Task<Stream> ConnectFirstPermittedAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect,
        CancellationToken cancellationToken)
    {
        var sawPermitted = false;
        foreach (var address in addresses)
        {
            if (!PublicAddressPolicy.IsAllowed(address))
            {
                continue;
            }

            sawPermitted = true;
            try
            {
                return await connect(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when(e is AgentCore.Application.Security.AccessDeniedException or AgentCore.Application.Security.SecurityConflictException) { throw; }
            catch (PublicWebFetchException)
            {
                throw;
            }
            catch
            {
                // Try the next permitted address. Dual-stack hosts often publish an unreachable family first.
            }
        }

        if (!sawPermitted)
        {
            throw new PublicWebFetchException(
                "forbidden_host",
                "No permitted public address was available for the host.");
        }

        throw new PublicWebFetchException(
            "transport_error",
            "Public web connection failed for every permitted address.");
    }

    private static async ValueTask<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static HttpRequestMessage CreateGetRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("text/html, text/plain, application/json, application/xml, */*;q=0.1");
        return request;
    }
}
