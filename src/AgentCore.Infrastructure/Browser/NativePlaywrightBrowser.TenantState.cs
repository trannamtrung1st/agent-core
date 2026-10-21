using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    public async ValueTask<ResourceStateObservation> ObserveTenantAgentAsync(TenantBrowserAgentOwner owner, IReadOnlyList<Guid>? readableSessions, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested(); var at = _time.GetUtcNow();
        SessionBrowser[] contexts; string sharing;
        lock (_bindingGate)
        {
            if (_tenantAgents.TryGetValue(owner.AgentInstanceId, out var actual) && actual != owner) throw new AccessDeniedException("browser_owner_mismatch");
            if (!IsAvailable) return new("browser", "unavailable", "unknown", at, Code: "provider_unavailable");
            if (actual is null) return new("browser", "available", "unknown", at, LiveContexts: 0, OpenPages: 0);
            var sessions = _tenantSessions.Where(p => owner.SameAgent(p.Value) && (readableSessions is null || readableSessions.Contains(p.Key))).Select(p => p.Key).ToHashSet();
            var modes = sessions.Select(id => _sessions.TryGetValue(id, out var view)
                ? view.Persistent ? BrowserProfileMode.PersistentAgent : BrowserProfileMode.EphemeralSession
                : _sessionModes.TryGetValue(id, out var mode) ? mode : (BrowserProfileMode?)null).OfType<BrowserProfileMode>().ToArray();
            var retainedShared = readableSessions is null && _persistent.TryGetValue(owner.AgentInstanceId, out var root) && Volatile.Read(ref root.RuntimeClosed) == 0;
            sharing = modes.Contains(BrowserProfileMode.PersistentAgent) || retainedShared ? "sharedCoordinated" : modes.Length > 0 ? "isolated" : "unknown";
            if (sessions.Any(TenantRetirementBlocked)) return new("browser", "partial", sharing, at, Code: "cleanup_pending");
            if (_persistent.TryGetValue(owner.AgentInstanceId, out var retained) && retained.PendingContextMutation is { Completion.IsCompletedSuccessfully: false }
                && (readableSessions is null || sessions.Any(ContextTransitionBlocked))) return new("browser", "partial", sharing, at, Code: "policy_transition_pending");
            contexts = _sessions.Where(p => sessions.Contains(p.Key)).Select(p => p.Value)
                .Concat(_persistent.Where(p => readableSessions is null && p.Key == owner.AgentInstanceId).Select(p => p.Value)).Distinct().ToArray();
        }
        var pages = new HashSet<Microsoft.Playwright.IPage>(); var live = new HashSet<Microsoft.Playwright.IBrowserContext>();
        foreach (var context in contexts)
        {
            if (!await context.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return new("browser", "partial", sharing, at, Code: "resource_busy");
            try
            {
                if (Volatile.Read(ref context.RuntimeClosed) == 0)
                {
                    live.Add(context.Context);
                    lock (context.PopupGate) foreach (var page in context.Pages) if (!PageClosed(page.Page)) pages.Add(page.Page);
                }
            }
            finally { context.Gate.Release(); }
        }
        return new("browser", "available", sharing, at, LiveContexts: live.Count, OpenPages: pages.Count);
    }
}
