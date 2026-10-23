using System.Net.Sockets;

namespace AgentCore.Application.Observability;

/// <summary>Content-free causal evidence for every diagnostic logging sink.</summary>
internal static class DiagnosticExceptionSnapshot
{
    private const int MaxDepth = 8;
    private const int MaxCauses = 8;
    private const int MaxNodes = 32;

    public static Exception Capture(Exception source)
    {
        var remaining = MaxNodes;
        return Snapshot(source, 0, ref remaining);
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
            return new SafeAggregateException(message, new System.Diagnostics.StackTrace(source, fNeedFileInfo: true).ToString(), source.HResult, causes);
        }
        return new SafeException(message, new System.Diagnostics.StackTrace(source, fNeedFileInfo: true).ToString(), source.HResult,
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
