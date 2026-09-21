using System.Net;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicAddressPolicy
{
    public static bool IsAllowed(IPAddress address) => PublicNetworkPolicy.IsAllowed(address);

    public static bool IsAllowedHostName(string host) => PublicNetworkPolicy.IsAllowedHostName(host);
}
