using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : IBrowserStateReader
{
    public ValueTask<ResourceStateObservation> ObserveAsync(Guid trustedInstanceId, CancellationToken ct = default) => ObserveCoreAsync(trustedInstanceId, null, ct);
    public ValueTask<ResourceStateObservation> ObserveSessionAsync(Guid trustedInstanceId, Guid trustedSessionId, CancellationToken ct = default) => ObserveCoreAsync(trustedInstanceId, trustedSessionId, ct);
    private async ValueTask<ResourceStateObservation> ObserveCoreAsync(Guid trustedInstanceId, Guid? sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var observedAt = _time.GetUtcNow();
        BrowserProfileMode? KnownMode(Guid id) => _sessions.TryGetValue(id, out var view)
            ? view.Persistent ? BrowserProfileMode.PersistentAgent : BrowserProfileMode.EphemeralSession
            : _sessionModes.TryGetValue(id, out var selected) ? selected : null;
        var modes = _sessionOwners.Where(p => p.Value == trustedInstanceId && (sessionId == null || p.Key == sessionId))
            .Select(p => KnownMode(p.Key)).Where(m => m.HasValue).Select(m => m!.Value).ToArray();
        var sharing = modes.Length > 0 ? modes.Contains(BrowserProfileMode.PersistentAgent) ? "sharedCoordinated" : "isolated"
            : sessionId is null && _persistent.TryGetValue(trustedInstanceId, out var root) && Volatile.Read(ref root.RuntimeClosed) == 0
                ? "sharedCoordinated" : "unknown";
        if (!IsAvailable) return new("browser", "unavailable", sharing, observedAt, Code: "provider_unavailable");
        if ((sessionId is null || ContextTransitionBlocked(sessionId.Value))
            && _persistent.TryGetValue(trustedInstanceId, out var retained) && retained.PendingContextMutation is { Completion.IsCompletedSuccessfully: false })
            return new("browser", "partial", sharing, observedAt, Code: "policy_transition_pending");
        // Read only existing handles. Never Ensure/launch, inspect a page or touch profile bytes.
        var contexts = _sessions.Where(p => (sessionId == null || p.Key == sessionId) && _sessionOwners.TryGetValue(p.Key, out var owner) && owner == trustedInstanceId)
            .Select(p => p.Value).Concat(_persistent.Where(p => sessionId == null && p.Key == trustedInstanceId).Select(p => p.Value))
            .Distinct().ToArray();
        var pages = 0;
        var liveContexts = new HashSet<Microsoft.Playwright.IBrowserContext>();
        foreach (var context in contexts)
        {
            if (!await context.Gate.WaitAsync(0, ct))
                return new("browser", "partial", sharing, observedAt, Code: "resource_busy");
            try
            {
                if (Volatile.Read(ref context.RuntimeClosed) == 1) continue;
                liveContexts.Add(context.Context);
                lock (context.PopupGate) pages += context.Pages.Count;
            }
            finally { context.Gate.Release(); }
        }
        return new("browser", "available", sharing, observedAt, LiveContexts: liveContexts.Count, OpenPages: pages);
    }
}
