using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantHumanMemoryOwner(Guid TenantId, Guid AgentId, Guid ExecutorPrincipalId, string AgentName, TenantUser User, TenantMemoryQuery Query,
    IReadOnlyList<Guid> ReadableAgents, IReadOnlyList<Guid> WritableAgents);

public static class TenantMemoryManagementRules
{
    public static void Validate(TenantMemoryQuery query)
    {
        if (!Enum.IsDefined(query.Scope) || query.Page < 1 || query.Page > 100000 || query.PageSize is < 1 or > 100
            || query.Scope == MemoryScope.Session != (query.SessionId is not null) || query.SessionId == Guid.Empty || query.MemoryId == Guid.Empty)
            throw AgentCoreErrors.Validation("Select a Memory scope, an exact Session for Session memory, and valid pagination.");
    }
    public static async ValueTask<TenantHumanMemoryOwner> OwnerAsync(TenantSecurityState state, TenantAgent agent, HumanSessionReference human,
        TenantMemoryQuery query, IamAuthorizer iam, TenantSession? session, CancellationToken ct)
    {
        Validate(query); var tenant = state.Tenant.TenantId;
        var user = state.Users.SingleOrDefault(u => u.PrincipalId == human.PrincipalId && u.AccountId == human.AccountId && u.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("initiator_unavailable");
        if (agent.TenantId != tenant)
            throw new AccessDeniedException("memory_unavailable");
        async ValueTask<bool> Allowed(string permission, Guid id) => (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission),
            AccessScope.ForResource(tenant, "agent", id.ToString("D")), ct).ConfigureAwait(false)).Allowed;
        if (!await Allowed("agents.read", agent.Instance.InstanceId) || !await Allowed("memory.read", agent.Instance.InstanceId)) throw new AccessDeniedException("memory_unavailable");
        if (query.Scope == MemoryScope.Session && (session is null || session.Owner.TenantId != tenant || session.Owner.AgentInstanceId != agent.Instance.InstanceId
            || session.Snapshot.DurablyDeletedAt is not null || !(await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("sessions.read"),
                TenantSessionRules.Scope(tenant, query.SessionId!.Value), ct).ConfigureAwait(false)).Allowed)) throw new AccessDeniedException("memory_unavailable");
        var readable = new List<Guid>(); var writable = new List<Guid>();
        foreach (var source in state.SystemIdentities.Where(i => i.Kind == SystemIdentityKind.Agent && i.Status == IdentityStatus.Active
            && i.AgentInstanceId is not null && (query.Scope == MemoryScope.User || i.AgentInstanceId == agent.Instance.InstanceId)))
        {
            var id = source.AgentInstanceId!.Value;
            if (!await Allowed("agents.read", id) || !await Allowed("memory.read", id)) continue;
            readable.Add(id); if (await Allowed("memory.manage", id) && await Allowed("memory.manage", agent.Instance.InstanceId)) writable.Add(id);
        }
        return new(tenant, agent.Instance.InstanceId, agent.PrincipalId, agent.Instance.Persona.Name, user, query, Array.AsReadOnly(readable.Distinct().ToArray()), Array.AsReadOnly(writable.Distinct().ToArray()));
    }
    public static IReadOnlyList<string> OwnerKeys(TenantHumanMemoryOwner owner) => owner.Query.Scope switch {
        MemoryScope.Session => [$"session:{owner.Query.SessionId:D}:{owner.User.PrincipalId:D}", $"session:{owner.Query.SessionId:D}:{owner.ExecutorPrincipalId:D}"],
        MemoryScope.IdentityUser => [$"identityUser:{owner.AgentId:D}:{owner.User.PrincipalId:D}"],
        _ => [$"user:{owner.User.PrincipalId:D}"] };
    public static bool Owns(TenantHumanMemoryOwner owner, TenantLearnedMemory item) => item.TenantId == owner.TenantId && item.Scope == owner.Query.Scope
        && (owner.Query.MemoryId is null || item.MemoryId == owner.Query.MemoryId) && owner.ReadableAgents.Contains(item.AgentInstanceId)
        && (item.UserPrincipalId == owner.User.PrincipalId && item.AccountId == owner.User.AccountId && item.UserId == owner.User.UserId
            || item.UserPrincipalId is null && item.Scope == MemoryScope.Session && item.AccountId is null && item.UserId is null)
        && (item.Scope != MemoryScope.Session || item.SessionId == owner.Query.SessionId && item.AgentInstanceId == owner.AgentId)
        && (item.Scope != MemoryScope.IdentityUser || item.AgentInstanceId == owner.AgentId);
    public static bool Writable(TenantHumanMemoryOwner owner, TenantLearnedMemory item) => Owns(owner, item) && owner.WritableAgents.Contains(item.AgentInstanceId);
    public static void Validate(TenantMemoryForget change, Guid tenant, Guid agent, HumanSessionReference human, AuthorizationAudit audit)
    {
        Validate(change.Query);
        if (change.Query.MemoryId is not null || change.ExpectedSecurityRevision < 1 || change.MemoryId == Guid.Empty
            || change.MemoryId is null && (change.ExpectedMemoryRevision is not null || change.ExpectedRetainedCount is null or < 1)
            || change.MemoryId is not null && (change.ExpectedMemoryRevision is null or < 1 || change.ExpectedRetainedCount is not null)
            || audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "memory.forget"
            || audit.Target != agent.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "human_memory_forgotten"
            || audit.AuditId == Guid.Empty || audit.At.Offset != TimeSpan.Zero) throw AgentCoreErrors.Validation("Supply exact current Memory witnesses.");
    }
    public static TenantLearnedMemory Tombstone(TenantLearnedMemory item, DateTimeOffset at) => item with {
        Subject = "", Content = "", SubjectKey = "", Status = MemoryItemStatus.Deleted, Revision = checked(item.Revision + 1), UpdatedAt = at > item.UpdatedAt ? at : item.UpdatedAt };
}
