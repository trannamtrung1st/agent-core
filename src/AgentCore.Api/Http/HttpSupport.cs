using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Http;

public static class ProblemResults
{
    public static IResult From(AgentCoreException exception) => new ClassifiedProblemResult(exception);

    private sealed class ClassifiedProblemResult(AgentCoreException exception) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var problem = new ProblemDetails
            {
                Type = "about:blank",
                Title = exception.Code,
                Status = exception.StatusCode,
                Detail = exception.Message
            };
            problem.Extensions["code"] = exception.Code;
            if (exception.Code == "SessionPersistenceUnavailable")
            {
                var diagnosticId = exception.DiagnosticId;
                if (diagnosticId is null || diagnosticId == Guid.Empty)
                {
                    diagnosticId = httpContext.RequestServices.GetRequiredService<IDiagnosticIdSource>().NewId();
                    DiagnosticLog.Error(
                        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentCore.Api.Http"),
                        exception,
                        diagnosticId.Value,
                        "Persistent save failed.",
                        RouteContext(httpContext, "Session", exception.Code));
                }

                problem.Extensions["diagnosticId"] = diagnosticId.Value.ToString("D");
            }

            if (exception.DiagnosticId is Guid id && id != Guid.Empty)
                problem.Extensions["diagnosticId"] = id.ToString("D");
            httpContext.Response.StatusCode = exception.StatusCode;
            await httpContext.Response.WriteAsJsonAsync(problem).ConfigureAwait(false);
        }
    }

    internal static DiagnosticContext RouteContext(HttpContext httpContext, string category, string code)
    {
        return new DiagnosticContext(
            SessionId: RouteGuid(httpContext, "sessionId"),
            WorkItemId: RouteGuid(httpContext, "workItemId"),
            ErrorCategory: category,
            ErrorCode: code);
    }

    private static Guid? RouteGuid(HttpContext httpContext, string key)
    {
        if (httpContext.Request.RouteValues.TryGetValue(key, out var value)
            && Guid.TryParse(Convert.ToString(value), out var id)
            && id != Guid.Empty)
        {
            return id;
        }

        return null;
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
