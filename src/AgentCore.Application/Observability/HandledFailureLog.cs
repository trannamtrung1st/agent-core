using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Observability;

/// <summary>
/// Stateless logging façade. Callers supply their logger and diagnostic ID source;
/// no logger, service provider or mutable execution state is retained here.
/// </summary>
public static class HandledFailureLog
{
    /// <returns>The logged reference, or null when warnings are disabled. Trace failure status is always recorded.</returns>
    public static Guid? Warning(ILogger? logger, Exception exception, string operation, string code,
        DiagnosticContext context = default, Guid? diagnosticId = null, IDiagnosticIdSource? diagnostics = null)
    {
        context = context with { ErrorCategory = context.ErrorCategory ?? "operation", ErrorCode = code, Operation = operation };
        if (logger?.IsEnabled(LogLevel.Warning) != true)
        {
            // Trace failure state is independent of log filtering. No log reference is
            // created when no warning sink can receive it, and snapshot work is skipped.
            DiagnosticLog.MarkActivity(context);
            return null;
        }
        var id = diagnosticId is Guid supplied && supplied != Guid.Empty ? supplied
            : (diagnostics ?? FallbackDiagnosticIdSource.Instance).NewId();
        DiagnosticLog.Warning(logger, exception, id, "Handled operation failed.", context);
        return id;
    }

}
