using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AgentCore.Api.Http;

public sealed class DiagnosticExceptionHandler(
    IDiagnosticIdSource diagnostics,
    ILogger<DiagnosticExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException || httpContext.Response.HasStarted)
        {
            return false;
        }

        var diagnosticId = diagnostics.NewId();
        DiagnosticLog.Error(
            logger,
            exception,
            diagnosticId,
            "HTTP request failed.",
            ProblemResults.RouteContext(httpContext, "http", "InternalError"));
        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = "InternalError",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "The request could not be completed."
        };
        problem.Extensions["code"] = "InternalError";
        problem.Extensions["diagnosticId"] = diagnosticId.ToString("D");
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
