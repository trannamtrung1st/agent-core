using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

// Keep original exceptions inside the owned invocation; SignalR's own logger and
// client error rendering receive only a safe reference, never provider/private data.
public sealed class SessionHubDiagnosticFilter(ILogger<SessionHubDiagnosticFilter> logger, IDiagnosticIdSource diagnostics) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var before = Context(invocation.Hub);
        try { return await next(invocation).ConfigureAwait(false); }
        catch (Exception exception) { throw Failure(invocation.Hub, exception, "hub.command", before); }
    }

    public async Task OnConnectedAsync(HubLifetimeContext connection, Func<HubLifetimeContext, Task> next)
    {
        var before = Context(connection.Hub);
        try { await next(connection).ConfigureAwait(false); }
        catch (Exception exception) { throw Failure(connection.Hub, exception, "hub.connect", before); }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext connection, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        var before = Context(connection.Hub);
        try { await next(connection, exception).ConfigureAwait(false); }
        catch (Exception failure) { throw Failure(connection.Hub, failure, "hub.disconnect", before); }
    }

    private HubException Failure(Hub hub, Exception exception, string operation, DiagnosticContext before)
    {
        if (exception is OperationCanceledException && hub.Context.ConnectionAborted.IsCancellationRequested)
            return new HubException("The Session connection was closed.");
        var context = (hub as SessionHub)?.ValidatedAttachDiagnostics ?? before;
        var id = diagnostics.NewId();
        DiagnosticLog.Error(logger, exception, id, "Session transport operation failed.",
            context with { Operation = operation, ErrorCategory = "transport", ErrorCode = "InternalError" });
        return new HubException($"The Session operation could not be completed. DiagnosticId={id:D}");
    }

    private static DiagnosticContext Context(Hub hub) => (hub as SessionHub)?.ConnectionDiagnostics ?? default;
}
