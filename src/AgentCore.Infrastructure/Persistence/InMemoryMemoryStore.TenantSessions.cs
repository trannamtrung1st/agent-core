using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    private InMemoryAgentInstanceStore? tenantInstances = instances;
    internal InMemoryMemoryStore BindTenantInstances(InMemoryAgentInstanceStore backing)
    {
        lock (_gate)
        {
            if (tenantInstances is not null && !ReferenceEquals(tenantInstances, backing)) throw new InvalidOperationException("Tenant Sessions must share the actual Instance backing.");
            tenantInstances = backing; return this;
        }
    }
    private readonly Dictionary<Guid, TenantSessionRoot> tenantSessions = [];
    private System.Collections.Immutable.ImmutableDictionary<Guid, TenantSessionRoot> tenantSessionOwnership = System.Collections.Immutable.ImmutableDictionary<Guid, TenantSessionRoot>.Empty;
    // Retained immutable ownership only, used by legacy exclusion without reversing backing-store locks.
    internal TenantSessionRoot? RetainedTenantSessionOwnerSnapshot(Guid session) => Volatile.Read(ref tenantSessionOwnership).GetValueOrDefault(session);
    private IEnumerable<AgentCore.Domain.Conversation.SessionSnapshot> LegacySessions => _sessions.Values.Where(s => !tenantSessions.ContainsKey(s.SessionId));
    internal bool OwnsTenantSession(Guid tenant, Guid id)
    { lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot) && snapshot.DurablyDeletedAt is null; }
    internal bool RequiresTenantContext(Guid session, Guid instance)
    { lock (_gate) return tenantSessions.ContainsKey(session) || tenantInstances?.IsTenantOwned(instance) == true; }
    internal TenantSession? ReadTenantSession(Guid tenant, Guid id)
    {
        lock (_gate) return tenantSessions.TryGetValue(id, out var owner) && owner.TenantId == tenant && _sessions.TryGetValue(id, out var snapshot)
            && snapshot.DurablyDeletedAt is null ? new(owner, snapshot with { Definition = AgentDefinitionAdminSnapshots.Freeze(snapshot.Definition), Entries = [] }) : null;
    }
    internal TenantSessionPage ListTenantSessions(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size, bool includeArchived = true)
    {
        lock (_gate)
        {
            var roots = tenantSessions.Values.Where(s => s.TenantId == tenant && (allowed is null || allowed.Contains(s.SessionId))
                && _sessions.TryGetValue(s.SessionId, out var snapshot) && snapshot.DurablyDeletedAt is null && (includeArchived || snapshot.ArchivedAt is null)).OrderBy(s => s.SessionId).ToArray();
            return new(roots.Skip(page * size).Take(size).Select(s => ReadTenantSession(tenant, s.SessionId)!).ToArray(), roots.Length, page, size);
        }
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
