using System.Net;
using System.Net.NetworkInformation;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Http;

public static class TrustedLocalCaller
{
    public static bool IsLoopback(HttpContext http) =>
        IsTrustedLocal(http, trustPublishedPortGateway: false);

    public static bool IsTrustedLocal(HttpContext http, bool trustPublishedPortGateway)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null)
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (!trustPublishedPortGateway)
        {
            return false;
        }

        var gateway = DockerPublishedPortGateway.TryResolve();
        return gateway is not null && ip.Equals(gateway);
    }
}

internal static class DockerPublishedPortGateway
{
    private static readonly AsyncLocal<Func<IPAddress?>?> ResolveOverrideLocal = new();

    internal static Func<IPAddress?>? ResolveOverride
    {
        get => ResolveOverrideLocal.Value;
        set => ResolveOverrideLocal.Value = value;
    }

    public static IPAddress? TryResolve()
    {
        if (ResolveOverride is not null)
        {
            return ResolveOverride();
        }

        return TryReadProcNetRoute() ?? TryReadNetworkInterface();
    }

    private static IPAddress? TryReadProcNetRoute()
    {
        const string path = "/proc/net/route";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !string.Equals(parts[1], "00000000", StringComparison.Ordinal))
                {
                    continue;
                }

                var hex = parts[2];
                if (hex.Length != 8)
                {
                    continue;
                }

                var value = Convert.ToUInt32(hex, 16);
                var bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }

                var gateway = new IPAddress(bytes);
                return IPAddress.IsLoopback(gateway) ? null : gateway;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        return null;
    }

    private static IPAddress? TryReadNetworkInterface()
    {
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var gateway in network.GetIPProperties().GatewayAddresses)
                {
                    var address = gateway.Address;
                    if (address is null)
                    {
                        continue;
                    }

                    if (address.IsIPv4MappedToIPv6)
                    {
                        address = address.MapToIPv4();
                    }

                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address))
                    {
                        return address;
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        return null;
    }
}

public sealed class OwnerCapabilityFilter(
    IOwnerCapabilityService capabilities,
    IOptions<HostingOptions> hosting) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!TrustedLocalCaller.IsTrustedLocal(http, hosting.Value.TrustPublishedPortGateway))
        {
            return ProblemResults.From(AgentCoreErrors.Forbidden("Owner APIs are limited to the local host."));
        }

        var token = http.Request.Headers[OwnerCapabilityHeaders.Name].ToString();
        if (!await capabilities.ValidateAsync(token, http.RequestAborted).ConfigureAwait(false))
        {
            return ProblemResults.From(AgentCoreErrors.Unauthorized());
        }

        return await next(context).ConfigureAwait(false);
    }
}
