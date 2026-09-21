using System.Net;

namespace AgentCore.Infrastructure.PublicWeb;

internal interface IPublicWebDnsResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemPublicWebDnsResolver : IPublicWebDnsResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var entries = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return entries;
    }
}
