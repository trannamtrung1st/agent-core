using System.Net.Sockets;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Observability;

/// <summary>
/// Stateless logging façade. Callers supply their logger and diagnostic ID source;
/// no logger, service provider or mutable execution state is retained here.
/// </summary>
public static class HandledFailureLog
{
    private const int MaxDepth = 8;
    private const int MaxCauses = 8;
    private const int MaxNodes = 32;

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
        var remaining = MaxNodes;
        DiagnosticLog.Warning(logger, Snapshot(exception, 0, ref remaining), id, "Handled operation failed.", context);
        return id;
    }

    private static Exception Snapshot(Exception source, int depth, ref int remaining)
    {
        remaining--;
        // Exception messages can contain credentials, URLs, mail addresses, DOM or provider bodies.
        // Keep the causal chain, original stack and machine codes instead of trying to redact content.
        var codes = source is SocketException socket ? $" SocketErrorCode={socket.SocketErrorCode}" :
            source is HttpRequestException http ? $" HttpRequestError={http.HttpRequestError} StatusCode={(int?)http.StatusCode}" : "";
        var message = $"{source.GetType().FullName} HResult={source.HResult}{codes}";
        if (source is AggregateException aggregate && depth < MaxDepth && remaining > 0)
        {
            var causes = new List<Exception>();
            foreach (var cause in aggregate.InnerExceptions.Take(MaxCauses))
            {
                if (remaining == 0) break;
                causes.Add(Snapshot(cause, depth + 1, ref remaining));
            }
            return new SafeAggregateException(message, source.StackTrace, source.HResult, causes);
        }
        return new SafeException(message, source.StackTrace, source.HResult,
            depth < MaxDepth && remaining > 0 && source.InnerException is { } inner ? Snapshot(inner, depth + 1, ref remaining) : null);
    }

    private sealed class SafeAggregateException : AggregateException
    {
        private readonly string? _stack;
        public SafeAggregateException(string message, string? stack, int hresult, IEnumerable<Exception> inner) : base(message, inner)
        {
            _stack = stack;
            HResult = hresult;
        }
        public override string? StackTrace => _stack;
    }

    private sealed class SafeException : Exception
    {
        private readonly string? _stack;
        public SafeException(string message, string? stack, int hresult, Exception? inner) : base(message, inner)
        {
            _stack = stack;
            HResult = hresult;
        }
        public override string? StackTrace => _stack;
    }
}
