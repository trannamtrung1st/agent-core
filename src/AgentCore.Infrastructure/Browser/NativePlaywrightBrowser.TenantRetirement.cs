using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : ITenantBrowserRetirement
{
    internal Func<IPage, Task>? TenantRetirementPageCloseProbe { get; set; }
    private sealed record TenantRetirement(IBrowserContext? Context);
    private readonly ConcurrentDictionary<Guid, TenantRetirement> _tenantRetirements = new();
    private bool HasTenantResources(TenantBrowserOwner owner) => _sessions.ContainsKey(owner.SessionId) || _tenantRetirements.ContainsKey(owner.SessionId)
        || _persistent.TryGetValue(owner.AgentInstanceId, out var root) && !_sessions.Values.Any(v => ReferenceEquals(v.Context, root.Context));

    public ValueTask<TenantBrowserRetirementPage> ListTenantRetirementCandidatesAsync(Guid? afterSession, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var owners = _tenantSessions.Values.Where(o => (afterSession is null || o.SessionId.CompareTo(afterSession.Value) > 0) && HasTenantResources(o))
            .OrderBy(o => o.SessionId).Take(limit + 1).ToArray();
        return ValueTask.FromResult(new TenantBrowserRetirementPage(owners.Take(limit).ToArray(), owners.Length > limit ? owners[limit - 1].SessionId : null));
    }
    private bool TenantRetirementBlocked(Guid session)
    {
        if (_tenantRetirements.ContainsKey(session)) return true;
        return _sessions.TryGetValue(session, out var view) && _tenantRetirements.Values.Any(r => r.Context is not null && ReferenceEquals(r.Context, view.Context))
            || SessionMode(session) == BrowserProfileMode.PersistentAgent && _sessionOwners.TryGetValue(session, out var agent)
                && agent is { } id && _persistent.TryGetValue(id, out var root)
                && _tenantRetirements.Values.Any(r => r.Context is not null && ReferenceEquals(r.Context, root.Context));
    }
    private void ClearTenantRetirements(IBrowserContext context)
    {
        foreach (var item in _tenantRetirements.Where(p => ReferenceEquals(p.Value.Context, context))) _tenantRetirements.TryRemove(item);
    }

    public async ValueTask<string> RetireTenantAsync(TenantBrowserOwner owner, Func<CancellationToken, ValueTask<bool>> stillRetired, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested();
        if (!_tenantSessions.TryGetValue(owner.SessionId, out var bound) || bound != owner) throw new AccessDeniedException("browser_owner_mismatch");
        if (!await stillRetired(ct).ConfigureAwait(false) && _tenantRetirements.GetValueOrDefault(owner.SessionId)?.Context is null)
        { _tenantRetirements.TryRemove(owner.SessionId, out _); return "active"; }
        var shared = _sessions.TryGetValue(owner.SessionId, out var known) ? known.Persistent
            : SessionMode(owner.SessionId) == BrowserProfileMode.PersistentAgent || _persistent.ContainsKey(owner.AgentInstanceId);
        var ownerGate = shared ? _contextUse.GetOrAdd(owner.AgentInstanceId, static _ => new SemaphoreSlim(1, 1)) : null;
        if (ownerGate is not null && !await ownerGate.WaitAsync(0, ct).ConfigureAwait(false)) return Pending();
        try
        {
            var sessionGate = _sessionUse.GetOrAdd(owner.SessionId, static _ => new SemaphoreSlim(1, 1));
            if (!await sessionGate.WaitAsync(0, ct).ConfigureAwait(false)) return Pending();
            try
            {
                _sessions.TryGetValue(owner.SessionId, out var view);
                if (view is null && _persistent.TryGetValue(owner.AgentInstanceId, out var orphan)
                    && !_sessions.Values.Any(v => ReferenceEquals(v.Context, orphan.Context))) view = orphan;
                if (view is null) { _tenantRetirements.TryRemove(owner.SessionId, out _); return "already_closed"; }
                if (!await view.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return Pending();
                try
                {
                    // Busy native work is not force-closed. A failed close retains only this exact old context for recovery.
                    if (view.OperationActive || view.PendingAction is { IsCompleted: false } || view.PendingContextMutation is { Completion.IsCompletedSuccessfully: false }) return Pending();
                    var previous = _tenantRetirements.GetValueOrDefault(owner.SessionId);
                    if (!await stillRetired(ct).ConfigureAwait(false) && !ReferenceEquals(previous?.Context, view.Context))
                    { _tenantRetirements.TryRemove(owner.SessionId, out _); return "active"; }
                    _tenantRetirements[owner.SessionId] = new(view.Context);
                    var last = !view.Persistent || !_sessions.Any(p => p.Key != owner.SessionId && ReferenceEquals(p.Value.Context, view.Context));
                    if (last)
                    {
                        var closeTarget = view.Persistent && _persistent.TryGetValue(owner.AgentInstanceId, out var root) ? root : view;
                        var separateGate = !ReferenceEquals(closeTarget.Gate, view.Gate);
                        if (separateGate && !await closeTarget.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return "cleanup_pending";
                        try
                        {
                            if (closeTarget.PendingContextMutation is { Completion.IsCompletedSuccessfully: false }) return "cleanup_pending";
                            var result = await CloseExplicitlyAsync(closeTarget, ct).ConfigureAwait(false);
                            if (result.Status is not ("closed" or "already_closed")) return "cleanup_pending";
                        }
                        finally { if (separateGate) closeTarget.Gate.Release(); }
                        ClearTenantRetirements(view.Context);
                    }
                    else
                    {
                        foreach (var page in OpenPages(view))
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            deadline.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs()));
                            var closing = TenantRetirementPageCloseProbe is { } close ? close(page.Page) : page.Page.CloseAsync();
                            _ = closing.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            try { await closing.WaitAsync(deadline.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "cleanup_pending"; }
                            catch (PlaywrightException) { return "cleanup_pending"; }
                            if (!page.Page.IsClosed) return "cleanup_pending";
                            ForgetPage(view, page.Page);
                        }
                        _sessions.TryRemove(new KeyValuePair<Guid, SessionBrowser>(owner.SessionId, view));
                    }
                    _tenantRetirements.TryRemove(owner.SessionId, out _);
                    _tenantCommands.AddOrUpdate(TenantCommandKey(owner), 1, static (_, revision) => checked(revision + 1));
                    return "closed";
                }
                finally { view.Gate.Release(); }
            }
            finally { sessionGate.Release(); }
        }
        finally { ownerGate?.Release(); }
        string Pending() { _tenantRetirements.TryAdd(owner.SessionId, new(null)); return "cleanup_pending"; }
    }
}
