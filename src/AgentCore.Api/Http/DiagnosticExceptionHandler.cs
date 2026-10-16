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

        if (exception is BadHttpRequestException { StatusCode: >= 400 and < 500 } rejected)
        {
            // Binding failures can contain submitted values in their exception text.
            // Return the client status without logging or echoing the exception/body.
            var invalid = new ProblemDetails { Type = "about:blank", Title = "InvalidRequest", Status = rejected.StatusCode,
                Detail = rejected.StatusCode switch { 413 => "The request exceeds the allowed size.", 415 => "Use a supported request content type.", _ => "The request is invalid." } };
            invalid.Extensions["code"] = "InvalidRequest";
            httpContext.Response.StatusCode = rejected.StatusCode;
            await httpContext.Response.WriteAsJsonAsync(invalid, cancellationToken).ConfigureAwait(false);
            return true;
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
