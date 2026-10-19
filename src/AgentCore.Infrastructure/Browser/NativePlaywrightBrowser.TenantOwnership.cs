using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser : ITenantBrowserBinding, ITenantBrowserStateReader
{
    private readonly object _bindingGate = new();
    // Retain immutable bindings after resource release; a Session cannot change owners.
    private readonly ConcurrentDictionary<Guid, TenantBrowserOwner> _tenantSessions = new();
    private readonly ConcurrentDictionary<Guid, TenantBrowserOwner> _tenantAgents = new();
    private readonly HashSet<Guid> _legacySessions = [];
    private readonly HashSet<Guid> _legacyAgents = [];

    public void BindTenantSession(TenantBrowserOwner owner)
    {
        owner.Validate();
        lock (_bindingGate)
        {
            if (_tenantSessions.TryGetValue(owner.SessionId, out var previous) && previous != owner
                || _tenantAgents.TryGetValue(owner.AgentInstanceId, out var agent) && !agent.SameAgent(owner))
                throw new AccessDeniedException("browser_owner_mismatch");
            if (_legacySessions.Contains(owner.SessionId) || _legacyAgents.Contains(owner.AgentInstanceId)
                || !_tenantSessions.ContainsKey(owner.SessionId) && (_sessionOwners.ContainsKey(owner.SessionId) || _sessions.ContainsKey(owner.SessionId))
                || !_tenantAgents.ContainsKey(owner.AgentInstanceId) && (_persistent.ContainsKey(owner.AgentInstanceId)
                    || _sessionOwners.Any(p => p.Value == owner.AgentInstanceId)))
                throw new AccessDeniedException("browser_owner_mismatch");
            _tenantAgents.TryAdd(owner.AgentInstanceId, owner);
            _tenantSessions.TryAdd(owner.SessionId, owner);
            _sessionOwners.TryAdd(owner.SessionId, owner.AgentInstanceId);
        }
    }

    private bool TenantRequestAllowed(BrowserRequest request)
    {
        lock (_bindingGate)
        {
            if (request.TenantOwner is { } owner)
                return owner.SessionId == request.SessionId && _tenantSessions.TryGetValue(request.SessionId, out var bound) && bound == owner;
            if (_tenantSessions.ContainsKey(request.SessionId)) return false;
            _legacySessions.Add(request.SessionId); return true;
        }
    }

    private void RequireLegacyAgent(Guid instance)
    {
        lock (_bindingGate)
        {
            if (_tenantAgents.ContainsKey(instance)) throw new AccessDeniedException("browser_owner_mismatch");
            _legacyAgents.Add(instance);
        }
    }

    public ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); BindTenantSession(owner);
        return BindIsolationCoreAsync(owner.SessionId, owner.AgentInstanceId, mode, ct, tenant: true);
    }

    public async ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode) || mode == BrowserProfileMode.PersistentAgent && _policy.ProfileMode != BrowserProfileMode.PersistentAgent) return "policy_denied";
        BindTenantSession(owner);
        var sessionUse = _sessionUse.GetOrAdd(owner.SessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await sessionUse.WaitAsync(0, ct).ConfigureAwait(false)) return "policy_transition_pending";
        try
        {
            if (ContextTransitionBlocked(owner.SessionId)
                || _sessions.TryGetValue(owner.SessionId, out var view) && view.Persistent != (mode == BrowserProfileMode.PersistentAgent)
                || _sessionModes.TryGetValue(owner.SessionId, out var existing) && existing != mode)
                return "policy_transition_pending";
            SelectSessionMode(owner.SessionId, mode); return null;
        }
        finally { sessionUse.Release(); }
    }

    public ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default)
    {
        owner.Validate();
        if (!_tenantSessions.TryGetValue(owner.SessionId, out var bound) || bound != owner)
            throw new AccessDeniedException("browser_owner_mismatch");
        return ReleaseCoreAsync(owner.SessionId, ct);
    }

    public ValueTask<ResourceStateObservation> ObserveTenantSessionAsync(TenantBrowserOwner owner, CancellationToken ct = default)
    {
        owner.Validate(); ct.ThrowIfCancellationRequested();
        if (_tenantSessions.TryGetValue(owner.SessionId, out var bound) && bound != owner
            || _tenantAgents.TryGetValue(owner.AgentInstanceId, out var agent) && !agent.SameAgent(owner))
            throw new AccessDeniedException("browser_owner_mismatch");
        if (!_tenantSessions.ContainsKey(owner.SessionId))
            return ValueTask.FromResult(new ResourceStateObservation("browser", IsAvailable ? "available" : "unavailable", "unknown", _time.GetUtcNow(),
                LiveContexts: IsAvailable ? 0 : null, OpenPages: IsAvailable ? 0 : null, Code: IsAvailable ? null : "provider_unavailable"));
        if (TenantRetirementBlocked(owner.SessionId)) return ValueTask.FromResult(new ResourceStateObservation("browser", "partial", SessionMode(owner.SessionId) == BrowserProfileMode.PersistentAgent ? "sharedCoordinated" : "isolated", _time.GetUtcNow(), CleanupPending: 1, Code: "cleanup_pending"));
        return ObserveCoreAsync(owner.AgentInstanceId, owner.SessionId, ct);
    }
}
