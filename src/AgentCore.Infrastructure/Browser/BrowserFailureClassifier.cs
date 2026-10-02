namespace AgentCore.Infrastructure.Browser;

internal static class BrowserFailureClassifier
{
    internal readonly record struct Decision(string Code, string Reason);

    internal static Decision Classify(string? message)
    {
        var text = message ?? string.Empty;
        if (text.Contains("Timeout", StringComparison.Ordinal))
        {
            return new Decision("timeout", "timeout");
        }

        if (text.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Browser closed", StringComparison.Ordinal)
            || text.Contains("Connection closed", StringComparison.Ordinal)
            || text.Contains("WebSocket", StringComparison.Ordinal))
        {
            return new Decision("provider_unavailable", "browserDisconnected");
        }

        if (text.Contains("context closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Context closed", StringComparison.Ordinal))
        {
            return new Decision("stale_reference", "contextClosed");
        }

        if (text.Contains("has been closed", StringComparison.Ordinal)
            || text.Contains("Target closed", StringComparison.Ordinal))
        {
            return new Decision("stale_reference", "pageClosed");
        }

        if (text.Contains("not a <select>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not a select", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unable to select", StringComparison.Ordinal)
            || text.Contains("not a checkbox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Not a checkbox", StringComparison.Ordinal)
            || text.Contains("not an <input>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not an <textarea>", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Element is not an <input>", StringComparison.Ordinal)
            || text.Contains("Element is not a <select>", StringComparison.Ordinal))
        {
            return new Decision("unsupported_operation", "unsupportedOperation");
        }

        if (text.Contains("ERR_CONNECTION_REFUSED", StringComparison.Ordinal)
            || text.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.Ordinal)
            || text.Contains("ERR_CONNECTION_RESET", StringComparison.Ordinal))
        {
            return new Decision("target_unreachable", "connectionRefused");
        }

        if (text.Contains("Execution context was destroyed", StringComparison.Ordinal)
            || text.Contains("frame was detached", StringComparison.OrdinalIgnoreCase)
            || text.Contains("most likely because of a navigation", StringComparison.Ordinal))
        {
            return new Decision("stale_reference", "pageChanged");
        }

        if (text.Contains("not attached", StringComparison.OrdinalIgnoreCase)
            || text.Contains("detached", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not connected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stale element", StringComparison.OrdinalIgnoreCase))
        {
            return new Decision("stale_reference", "staleElement");
        }

        return new Decision("stale_reference", "pageChanged");
    }

    internal static bool IsTransientCapture(string? message)
    {
        var reason = Classify(message).Reason;
        return reason is "pageChanged" or "staleElement";
    }

    internal static bool IsInterruptedNavigation(string? message)
    {
        var text = message ?? string.Empty;
        return text.Contains("interrupted by another navigation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("most likely because of a navigation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ERR_ABORTED", StringComparison.Ordinal);
    }
}
