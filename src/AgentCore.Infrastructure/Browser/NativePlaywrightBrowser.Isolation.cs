using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : IBrowserIsolationBinding
{
    private bool ContextTransitionBlocked(Guid sessionId) => _sessionOwners.TryGetValue(sessionId, out var owner)
        && owner is { } id && _persistent.TryGetValue(id, out var root)
        && root.PendingContextMutation is { Completion.IsCompletedSuccessfully: false } pending
        && (pending.SessionId == sessionId || pending.SessionId == Guid.Empty
            || (_sessions.TryGetValue(sessionId, out var view) ? view.Persistent : SessionMode(sessionId) == BrowserProfileMode.PersistentAgent));
    private BrowserProfileMode SessionMode(Guid sessionId) => _sessionModes.GetValueOrDefault(sessionId, _policy.ProfileMode);

    private void ActivateView(SessionBrowser view)
    {
        if (view.Persistent && view.AgentInstanceId is { } owner && _persistent.TryGetValue(owner, out var root))
            root.ActiveView = view;
    }

    public ValueTask<string?> BindIsolationAsync(Guid sessionId, Guid instanceId, BrowserProfileMode mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_bindingGate)
            if (_tenantSessions.ContainsKey(sessionId) || _tenantAgents.ContainsKey(instanceId)) return ValueTask.FromResult<string?>("forbidden");
        return BindIsolationCoreAsync(sessionId, instanceId, mode, ct);
    }

    private async ValueTask<string?> BindIsolationCoreAsync(Guid sessionId, Guid instanceId, BrowserProfileMode mode, CancellationToken ct, bool tenant = false)
    {
        ct.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || instanceId == Guid.Empty || !Enum.IsDefined(mode)) return "forbidden";
        if (mode == BrowserProfileMode.PersistentAgent && _policy.ProfileMode != BrowserProfileMode.PersistentAgent) return "policy_denied";
        if (_sessionOwners.TryGetValue(sessionId, out var owner) && owner != instanceId) return "forbidden";
        if (!tenant) BindSession(sessionId, instanceId);
        var sessionUse = _sessionUse.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await sessionUse.WaitAsync(0, ct)) return "policy_transition_pending";
        try
        {
            // The same Session gate covers provisioning and metadata adoption. A fresh
            // ephemeral view does not depend on a sibling's shared profile or its owner gate.
            _sessions.TryGetValue(sessionId, out var existing);
            if (existing is null && mode == BrowserProfileMode.EphemeralSession)
            {
                // Cancellation may leave this Session's native page unpublished. Its
                // original retirement fence still applies even though no view is in the map.
                if (_persistent.TryGetValue(instanceId, out var retained)
                    && retained.PendingContextMutation is { Completion.IsCompletedSuccessfully: false } pending
                    && (pending.SessionId == sessionId || pending.SessionId == Guid.Empty)) return "policy_transition_pending";
                _sessionModes[sessionId] = mode; return null;
            }
            if (ContextTransitionBlocked(sessionId) || mode == BrowserProfileMode.PersistentAgent
                && _persistent.TryGetValue(instanceId, out var root) && root.PendingContextMutation is { Completion.IsCompletedSuccessfully: false })
                return "policy_transition_pending";
            if (SessionMode(sessionId) == mode && (existing is null || existing.Persistent == (mode == BrowserProfileMode.PersistentAgent)))
            { _sessionModes[sessionId] = mode; return null; }
            // Metadata may tighten while native work is in flight. Never force-close that operation or reuse its context.
            var use = _contextUse.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
            if (!await use.WaitAsync(0, ct)) return "policy_transition_pending";
            try
            {
            if (existing is null) { _sessionModes[sessionId] = mode; return null; }
            if (!await existing.Gate.WaitAsync(0, ct)) return "policy_transition_pending";
            try
            {
                if (existing.OperationActive || existing.PendingAction is { IsCompleted: false }
                    || existing.TimedOut || existing.PopupCloses.Any(t => !t.IsCompleted)) return "policy_transition_pending";
                if (existing.Persistent)
                {
                    foreach (var binding in OpenPages(existing))
                    {
                        try { await binding.Page.CloseAsync().WaitAsync(ct); }
                        catch (Microsoft.Playwright.PlaywrightException) { return "policy_transition_pending"; }
                        ForgetPage(existing, binding.Page);
                    }
                    if (!_sessions.Any(p => p.Key != sessionId && ReferenceEquals(p.Value.Context, existing.Context))
                        && _persistent.TryGetValue(instanceId, out var retained))
                    {
                        var retired = await CloseExplicitlyAsync(retained, ct);
                        if (retired.Status is not ("closed" or "already_closed")) return "policy_transition_pending";
                    }
                }
                else
                {
                    var closed = await CloseExplicitlyAsync(existing, ct);
                    if (closed.Status is not ("closed" or "already_closed")) return "policy_transition_pending";
                }
                _sessions.TryRemove(new KeyValuePair<Guid, SessionBrowser>(sessionId, existing));
                _sessionModes[sessionId] = mode;
                return null;
            }
            finally { existing.Gate.Release(); }
            }
            finally { use.Release(); }
        }
        finally { sessionUse.Release(); }
    }

    private async ValueTask<BrowserCloseResult> CloseViewAsync(Guid sessionId, SessionBrowser view, CancellationToken ct)
    {
        await view.Gate.WaitAsync(ct);
        try
        {
            if (view.PendingAction is { IsCompleted: false }) return new("close_uncertain");
            if (!_sessions.Any(p => p.Key != sessionId && ReferenceEquals(p.Value.Context, view.Context))
                && view.AgentInstanceId is { } owner && _persistent.TryGetValue(owner, out var root))
                return await CloseExplicitlyAsync(root, ct);
            foreach (var binding in OpenPages(view))
            {
                try { await binding.Page.CloseAsync().WaitAsync(ct); }
                catch (Microsoft.Playwright.PlaywrightException) { return new("close_uncertain"); }
                ForgetPage(view, binding.Page);
            }
            _sessions.TryRemove(new KeyValuePair<Guid, SessionBrowser>(sessionId, view));
            return new("closed");
        }
        finally { view.Gate.Release(); }
    }
}
