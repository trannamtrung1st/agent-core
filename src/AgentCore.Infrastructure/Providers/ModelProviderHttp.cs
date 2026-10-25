using System.Net;
using System.Net.Sockets;
using AgentCore.Application.Models;

namespace AgentCore.Infrastructure.Providers;

// Shared native/compatible inference and metadata egress. No redirects, ambient proxy or POST retries.
internal static class ModelProviderHttp
{
    internal const string ClientName = "tenant-model-provider";
    internal static readonly HttpRequestOptionsKey<Func<CancellationToken, ValueTask>> DispatchAuthority = new("AgentCore.ModelDispatchAuthority");
    internal static readonly HttpRequestOptionsKey<bool> NoCredential = new("AgentCore.ModelNoCredential");
    internal static HttpMessageHandler CreateHandler(ModelEndpointPolicy endpoints) => new AuthorityHandler
    {
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 32, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var request = context.InitialRequestMessage; var uri = request.RequestUri ?? throw new HttpRequestException("Missing provider endpoint.");
                if (!request.Options.TryGetValue(DispatchAuthority, out var authorize)) throw new HttpRequestException("Missing provider dispatch authority.");
                await authorize(ct);
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var noCredential = request.Options.TryGetValue(NoCredential, out var mode) && mode;
                foreach (var address in addresses)
                {
                    if (!endpoints.AllowsAddress(uri, address) || noCredential && AgentCore.Application.Ports.PublicNetworkPolicy.IsAllowed(address)) continue;
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await authorize(ct); await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); await authorize(ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException) when (!ct.IsCancellationRequested) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("Provider endpoint has no permitted network address.");
            }
        }
    };
    private sealed class AuthorityHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Options.TryGetValue(DispatchAuthority, out var authorize)) throw new HttpRequestException("Missing provider dispatch authority.");
            await authorize(ct);
            return await base.SendAsync(request, ct);
        }
    }
}
