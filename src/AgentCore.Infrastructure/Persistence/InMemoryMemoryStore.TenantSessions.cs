using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Definitions;

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
    internal TenantSessionPage ListTenantSessions(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size)
    {
        lock (_gate)
        {
            var roots = tenantSessions.Values.Where(s => s.TenantId == tenant && (allowed is null || allowed.Contains(s.SessionId))
                && _sessions.TryGetValue(s.SessionId, out var snapshot) && snapshot.DurablyDeletedAt is null).OrderBy(s => s.SessionId).ToArray();
            return new(roots.Skip(page * size).Take(size).Select(s => ReadTenantSession(tenant, s.SessionId)!).ToArray(), roots.Length, page, size);
        }
    }
    internal bool CreateTenantSession(TenantSessionRoot owner, AgentCore.Domain.Conversation.SessionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (tenantSessions.ContainsKey(owner.SessionId) || _sessions.ContainsKey(owner.SessionId)) return false;
            snapshot = snapshot with { Definition = AgentDefinitionAdminSnapshots.Freeze(snapshot.Definition) };
            Store(snapshot); tenantSessions.Add(owner.SessionId, owner); return true;
        }
    }
}
