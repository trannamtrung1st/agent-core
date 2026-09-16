using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Http;

public static class TrustedLocalCaller
{
    public static bool IsLoopback(HttpContext http)
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

        return IPAddress.IsLoopback(ip);
    }
}

public sealed class OwnerCapabilityFilter(IOwnerCapabilityService capabilities) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!TrustedLocalCaller.IsLoopback(http))
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
