using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantMemoryManagementStore
{
    private TenantHumanMemoryOwner HumanMemory(Guid tenant, Guid agentId, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct)
    {
        RequireProfileHuman(tenant, human); var state = _tenants[tenant];
        var agent = FindTenantAgentAsync(tenant, agentId, ct).Result ?? throw new AccessDeniedException("memory_unavailable");
        var session = query.SessionId is { } id ? sessionBacking.ReadTenantSession(tenant, id) : null;
        return TenantMemoryManagementRules.OwnerAsync(state, agent, human, query, new IamAuthorizer(this), session, ct).AsTask().GetAwaiter().GetResult();
    }
    private IEnumerable<TenantLearnedMemory> HumanMemoryRows(TenantHumanMemoryOwner owner) => TenantMemoryManagementRules.OwnerKeys(owner)
        .SelectMany(key => Enum.GetValues<MemoryItemStatus>().SelectMany(status => learnedMemoryIndex.GetValueOrDefault((owner.TenantId, key, status)) ?? []))
        .Select(id => learnedMemories[(owner.TenantId, id)]).Where(i => TenantMemoryManagementRules.Owns(owner, i));
    public ValueTask<TenantMemoryPage> ReadOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantMemoryQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var owner = HumanMemory(tenant, agent, human, query, ct);
            var rows = HumanMemoryRows(owner).ToArray(); var retained = rows.Where(i => i.Status != MemoryItemStatus.Deleted).ToArray();
            var page = rows.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.MemoryId).Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize)
                .Select(i => new TenantMemoryItemView(i, i.Status != MemoryItemStatus.Deleted && TenantMemoryManagementRules.Writable(owner, i))).ToArray();
            return ValueTask.FromResult(new TenantMemoryPage(page, rows.Length, retained.Length, query.Page, query.PageSize, _tenants[tenant].Tenant.Revision,
                retained.Length > 0 && retained.All(i => TenantMemoryManagementRules.Writable(owner, i)), owner.AgentName));
        }
    }
    public ValueTask<TenantMemoryForgetResult?> TryForgetOwnTenantMemoryAsync(Guid tenant, Guid agent, HumanSessionReference human,
        TenantMemoryForget change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantMemoryManagementRules.Validate(change, tenant, agent, human, audit);
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var owner = HumanMemory(tenant, agent, human, change.Query, ct); var before = _tenants[tenant];
            if (before.Tenant.Revision != change.ExpectedSecurityRevision || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult<TenantMemoryForgetResult?>(null);
            var rows = HumanMemoryRows(owner).Where(i => i.Status != MemoryItemStatus.Deleted && (change.MemoryId is null || i.MemoryId == change.MemoryId)).ToArray();
            if (change.MemoryId is not null && (rows.Length != 1 || rows[0].Revision != change.ExpectedMemoryRevision)
                || change.MemoryId is null && rows.Length != change.ExpectedRetainedCount) return ValueTask.FromResult<TenantMemoryForgetResult?>(null);
            if (rows.Any(i => !TenantMemoryManagementRules.Writable(owner, i))) throw new AccessDeniedException("memory_manage_required");
            var tombstones = rows.Select(i => TenantMemoryManagementRules.Tombstone(i, audit.At)).ToArray();
            var next = before with { Tenant = before.Tenant with { Revision = checked(before.Tenant.Revision + 1) } };
            ct.ThrowIfCancellationRequested(); foreach (var item in tombstones) SetLearnedMemory(item);
            _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantMemoryForgetResult?>(new(rows.Length, next.Tenant.Revision));
        }
    }
}
