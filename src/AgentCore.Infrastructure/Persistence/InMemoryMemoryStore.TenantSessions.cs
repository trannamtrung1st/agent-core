using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    private TimeProvider tenantClock = TimeProvider.System;
    internal InMemoryMemoryStore BindTenantClock(TimeProvider clock) { tenantClock = clock; return this; }
    private bool RetentionAvailable(SessionSnapshot snapshot) => !SessionRetention.IsDue(snapshot, tenantClock.GetUtcNow());
    private DateTimeOffset? EffectiveRetentionDue(Guid tenant, SessionSnapshot root)
    {
        DateTimeOffset? due = null; var current = root; var seen = new HashSet<Guid>();
        void Include(DateTimeOffset? value) { if (value is { } next && (due is null || next < due)) due = next; }
        for (var depth = 0; depth <= 64; depth++)
        {
            Include(current.RetentionDueAtUtc); Include(current.DurablyDeletedAt);
            if (!seen.Add(current.SessionId)) { Include(root.CreatedAt); return due; }
            if (current.Origin.Kind is not (SessionOriginKind.DelegatedTask or SessionOriginKind.DirectAgentMessage)) return due;
            if (current.Origin.Kind == SessionOriginKind.DelegatedTask && current.Origin.WorkflowExecutionId is not null && current.Origin.OriginatingSessionId is null) return due;
            if (depth == 64 || current.Origin.OriginatingSessionId is not { } parent
                || !tenantSessions.TryGetValue(parent, out var owner) || owner.TenantId != tenant || !_sessions.TryGetValue(parent, out current!))
            { Include(root.CreatedAt); return due; }
        }
        return due;
    }
    internal TenantSessionRetentionTarget? FindDueTenantSession(Guid tenant, Guid id)
    {
        lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant
            && _sessions.TryGetValue(id, out var session) && session.DurablyDeletedAt is null
            && EffectiveRetentionDue(tenant, session) is { } due && due <= tenantClock.GetUtcNow()
            ? new(owner, session.Revision, due) : null;
    }
    internal IReadOnlyList<TenantSessionRetentionTarget> DueTenantSessions(TenantSessionRetentionTarget? after, int limit)
    {
        lock (_gate) return tenantSessions.Values.Select(owner => FindDueTenantSession(owner.TenantId, owner.SessionId))
            .OfType<TenantSessionRetentionTarget>()
            .Where(item => after is null || item.DueAtUtc > after.DueAtUtc || item.DueAtUtc == after.DueAtUtc
                && string.CompareOrdinal(item.Owner.SessionId.ToString("D"), after.Owner.SessionId.ToString("D")) > 0)
            .OrderBy(item => item.DueAtUtc).ThenBy(item => item.Owner.SessionId.ToString("D"), StringComparer.Ordinal).Take(limit).ToArray();
    }
    internal SessionSnapshot? DueTenantSession(TenantSessionRetentionTarget target)
    {
        lock (_gate) return FindDueTenantSession(target.Owner.TenantId, target.Owner.SessionId) == target
            ? _sessions[target.Owner.SessionId] : null;
    }
    internal void DeleteDueTenantSession(TenantSessionRetentionTarget target, DateTimeOffset at)
    {
        lock (_gate)
        {
            var current = DueTenantSession(target) ?? throw new SecurityConflictException("stale_revision");
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == current.SessionId && !r.IsTerminal)) throw new SecurityConflictException("session_in_use");
            var next = SessionRetention.IsDue(current, at) ? TenantSessionMutations.RetireDue(current, at) : TenantSessionMutations.RetireInheritedSource(current, at);
            if (_entries.TryGetValue(current.SessionId, out var entries)) _entries[current.SessionId] = entries.Select(TenantSessionMutations.RedactEntry).ToList();
            Store(next);
        }
    }
    internal void ExpireDueTenantSession(TenantSessionRetentionTarget target, DateTimeOffset at)
    {
        lock (_gate)
        {
            var current = DueTenantSession(target) ?? throw new SecurityConflictException("stale_revision");
            if (!SessionLifecycle.IsTerminal(current.LifecycleStatus))
                Store((SessionRetention.IsDue(current, at) ? TenantSessionMutations.ExpireDue(current, at) : TenantSessionMutations.ExpireInheritedSource(current, at)) with { Revision = checked(current.Revision + 1) });
        }
    }
    private InMemoryAgentInstanceStore? tenantInstances = instances;
    internal InMemoryMemoryStore BindTenantInstances(InMemoryAgentInstanceStore backing)
    {
        lock (_gate)
        {
            if (tenantInstances is not null && !ReferenceEquals(tenantInstances, backing)) throw new InvalidOperationException("Tenant Sessions must share the actual Instance backing.");
            tenantInstances = backing; CoreEvents.BindTenantInstances(backing); backing.CoreEvents.BindTenantInstances(backing); return this;
        }
    }
    private readonly Dictionary<Guid, TenantSessionRoot> tenantSessions = [];
    private System.Collections.Immutable.ImmutableDictionary<Guid, TenantSessionRoot> tenantSessionOwnership = System.Collections.Immutable.ImmutableDictionary<Guid, TenantSessionRoot>.Empty;
    // Retained immutable ownership only, used by legacy exclusion without reversing backing-store locks.
    internal TenantSessionRoot? RetainedTenantSessionOwnerSnapshot(Guid session) => Volatile.Read(ref tenantSessionOwnership).GetValueOrDefault(session);
    internal TenantArtifactCleanupPage DeletedArtifactOwners(Guid? after, int limit)
    {
        lock (_gate)
        {
            var rows = tenantSessions.Values.Where(o => _sessions[o.SessionId].DurablyDeletedAt is not null
                && (after is null || string.CompareOrdinal(o.SessionId.ToString("D"), after.Value.ToString("D")) > 0))
                .OrderBy(o => o.SessionId.ToString("D"), StringComparer.Ordinal).Take(limit + 1).ToArray();
            var items = rows.Take(limit).Select(o => new TenantArtifactCleanupTarget(o, _sessions[o.SessionId].DurablyDeletedAt!.Value)).ToArray();
            return new(items, rows.Length > limit ? items[^1].Owner.SessionId : null);
        }
    }
    internal bool CanCleanupDeletedArtifacts(TenantArtifactCleanupTarget target)
    {
        lock (_gate) return tenantSessions.GetValueOrDefault(target.Owner.SessionId) == target.Owner
            && _sessions.TryGetValue(target.Owner.SessionId, out var session) && session.DurablyDeletedAt == target.DeletedAt
            && !AgentRuns.Runs.Values.Any(r => r.SessionId == target.Owner.SessionId && !r.IsTerminal);
    }
    private IEnumerable<AgentCore.Domain.Conversation.SessionSnapshot> LegacySessions => _sessions.Values.Where(s => !tenantSessions.ContainsKey(s.SessionId));
    internal bool TenantSessionDeleted(Guid tenant, Guid id)
    { lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot) && snapshot.DurablyDeletedAt is not null; }
    // Derived work cannot extend the privacy lifetime of the input it copied.
    // This is a read cutoff only; retained Run facts stay available to reconciliation.
    private bool CooperationRetentionAvailable(Guid tenant, SessionSnapshot snapshot)
    {
        var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(snapshot.SessionId) || snapshot.DurablyDeletedAt is not null || !RetentionAvailable(snapshot)) return false;
            if (snapshot.Origin.Kind is not (SessionOriginKind.DelegatedTask or SessionOriginKind.DirectAgentMessage)) return true;
            if (snapshot.Origin.Kind == SessionOriginKind.DelegatedTask && snapshot.Origin.WorkflowExecutionId is not null && snapshot.Origin.OriginatingSessionId is null) return true;
            if (snapshot.Origin.OriginatingSessionId is not { } source || !tenantSessions.TryGetValue(source, out var owner)
                || owner.TenantId != tenant || !_sessions.TryGetValue(source, out snapshot!)) return false;
        }
        return false;
    }
    internal bool OwnsTenantSession(Guid tenant, Guid id)
    { lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot) && snapshot.DurablyDeletedAt is null && CooperationRetentionAvailable(tenant, snapshot); }
    internal bool RequiresTenantContext(Guid session, Guid instance)
    { lock (_gate) return tenantSessions.ContainsKey(session) || tenantInstances?.IsTenantOwned(instance) == true; }
    internal TenantSession? ReadTenantSession(Guid tenant, Guid id)
    {
        lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot)
            && snapshot.DurablyDeletedAt is null && CooperationRetentionAvailable(tenant, snapshot) ? new(owner, snapshot with { Definition = AgentDefinitionAdminSnapshots.Freeze(snapshot.Definition), Entries = [] }) : null;
    }
    internal TenantSessionPage ListTenantSessions(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false)
    {
        lock (_gate)
        {
            var roots = tenantSessions.Values.Where(s => s.TenantId == tenant && (instanceId is null || s.AgentInstanceId == instanceId) && (allowed is null || allowed.Contains(s.SessionId))
                && _sessions.TryGetValue(s.SessionId, out var snapshot) && snapshot.DurablyDeletedAt is null && (includeArchived || snapshot.ArchivedAt is null)
                && CooperationRetentionAvailable(tenant, snapshot) && (surface != SessionSurface.ChatList || snapshot.RetentionKind != SessionRetentionKind.Temporary)
                && (surface is null || snapshot.Surfaces.HasFlag(surface.Value))).OrderBy(s => s.SessionId).ToArray();
            if (recentFirst) roots = roots.OrderByDescending(s => _sessions[s.SessionId].UpdatedAt).ThenBy(s => s.SessionId).ToArray();
            return new(roots.Skip(page * size).Take(size).Select(s => ReadTenantSession(tenant, s.SessionId)!).ToArray(), roots.Length, page, size);
        }
    }
    internal TenantSession? ReadRetainedTenantBackgroundSession(Guid tenant, Guid id)
    {
        lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot)
            ? new(owner, snapshot with { Entries = [] }) : null;
    }
    internal bool HasTenantBackgroundIdentityConflict(SessionSnapshot snapshot)
    {
        lock (_gate) return _sessions.ContainsKey(snapshot.SessionId) || tenantSessions.ContainsKey(snapshot.SessionId)
            || _entries.Values.Any(entries => entries.Any(entry => snapshot.Entries.Any(candidate => candidate.EntryId == entry.EntryId)));
    }
    internal bool CreateTenantSession(TenantSessionRoot owner, AgentCore.Domain.Conversation.SessionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (tenantSessions.ContainsKey(owner.SessionId) || _sessions.ContainsKey(owner.SessionId)) return false;
            snapshot = snapshot with { Definition = AgentDefinitionAdminSnapshots.Freeze(snapshot.Definition) };
            Store(snapshot); tenantSessions.Add(owner.SessionId, owner);
            Volatile.Write(ref tenantSessionOwnership, tenantSessionOwnership.Add(owner.SessionId, owner)); return true;
        }
    }
    internal void ContinueTenantQuiescent(TenantSessionForeground request, DateTimeOffset at)
    {
        lock (_gate)
        {
            var current = _sessions[request.Owner.SessionId];
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == current.SessionId && r.Status == AgentRunStatus.Running))
                throw new SecurityConflictException("session_in_use");
            if (current.Surfaces.HasFlag(SessionSurface.ChatList)) return;
            Store(current with { Surfaces = SessionOrigin.ContinueInChat(current.Surfaces), Revision = checked(current.Revision + 1), UpdatedAt = at });
        }
    }
    internal TenantSession? ChangeTenantSession(Guid tenant, TenantSessionChange change, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!OwnsTenantSession(tenant, change.SessionId)) return null;
            var current = _sessions[change.SessionId];
            if (current.Revision != change.ExpectedSessionRevision) return null;
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == change.SessionId && !r.IsTerminal)) throw new SecurityConflictException("session_in_use");
            var next = TenantSessionMutations.Apply(current, change, at);
            if (change.Action == TenantSessionAction.Delete && _entries.TryGetValue(change.SessionId, out var entries))
                _entries[change.SessionId] = entries.Select(TenantSessionMutations.RedactEntry).ToList();
            Store(next);
            return new(tenantSessions[change.SessionId], next with { Definition = AgentDefinitionAdminSnapshots.Freeze(next.Definition), Entries = [] });
        }
    }
}
