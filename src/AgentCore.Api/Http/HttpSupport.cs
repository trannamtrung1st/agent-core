using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Http;

public static class ProblemResults
{
    public static IResult From(AgentCoreException exception)
    {
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = exception.Code,
            Status = exception.StatusCode,
            Detail = exception.Message
        };
        problem.Extensions["code"] = exception.Code;
        return Results.Json(problem, statusCode: exception.StatusCode);
    }
}

public sealed class OutboundHttpProbe
{
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    public void Record() => Interlocked.Increment(ref _attempts);
}

public sealed class ForbiddenOutboundHandler(OutboundHttpProbe probe) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        probe.Record();
        throw new InvalidOperationException(
            $"Synthetic profile forbids outbound HTTP to '{request.RequestUri}'.");
    }
}
