using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantExperienceManagementStore
{
    private TenantHumanExperienceOwner HumanExperience(Guid tenant, Guid agentId, HumanSessionReference human, CancellationToken ct)
    {
        RequireProfileHuman(tenant, human); var state = _tenants[tenant];
        var agent = FindTenantAgentAsync(tenant, agentId, ct).Result ?? throw new AccessDeniedException("experience_unavailable");
        return TenantExperienceManagementRules.OwnerAsync(state, agent, human, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
    }
    private IEnumerable<TenantExperience> HumanExperienceRows(TenantHumanExperienceOwner owner) => owner.Keys
        .SelectMany(key => tenantExperienceIndex.GetValueOrDefault((owner.TenantId, owner.AgentId, key)) ?? [])
        .Select(id => tenantExperiences[(owner.TenantId, id)]).Where(i => TenantExperienceManagementRules.Owns(owner, i));
    public ValueTask<TenantExperiencePage> ReadOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceQuery query, CancellationToken ct = default)
    {
        TenantExperienceManagementRules.Validate(query); ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var owner = HumanExperience(tenant, agent, human, ct); var owned = HumanExperienceRows(owner).ToArray();
            var retained = owned.Count(i => i.Visibility != ExperienceVisibility.Deleted);
            var rows = owned.Where(i => (query.ExperienceId is null || query.ExperienceId == i.ExperienceId) && (query.IncludeHistorical || i.Visibility is ExperienceVisibility.Eligible or ExperienceVisibility.Suppressed)).ToArray();
            var page = rows.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.ExperienceId).Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize)
                .Select(i => new TenantExperienceItemView(TenantExperienceRules.Copy(i), owner.CanManage && i.Visibility != ExperienceVisibility.Deleted)).ToArray();
            return ValueTask.FromResult(new TenantExperiencePage(page, rows.Length, retained, owner.SecurityRevision, query.Page, query.PageSize, owner.AgentName, owner.CanManage && retained > 0));
        }
    }
    public ValueTask<TenantExperienceChangeResult?> TryChangeOwnTenantExperienceAsync(Guid tenant, Guid agent, HumanSessionReference human, TenantExperienceChange change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantExperienceManagementRules.Validate(change, tenant, agent, human, audit); ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var owner = HumanExperience(tenant, agent, human, ct); var before = _tenants[tenant];
            if (before.Tenant.Revision != change.ExpectedSecurityRevision || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult<TenantExperienceChangeResult?>(null);
            if (!owner.CanManage) throw new AccessDeniedException("experience_manage_required");
            var rows = HumanExperienceRows(owner).Where(i => i.Visibility != ExperienceVisibility.Deleted && (change.ExperienceId is null || i.ExperienceId == change.ExperienceId)).ToArray();
            if (change.ExperienceId is not null && (rows.Length != 1 || rows[0].Revision != change.ExpectedExperienceRevision) || change.ExperienceId is null && rows.Length != change.ExpectedRetainedCount)
                return ValueTask.FromResult<TenantExperienceChangeResult?>(null);
            var values = rows.Select(i => TenantExperienceManagementRules.Change(i, change.Visibility, audit.At)).ToArray();
            var next = before with { Tenant = before.Tenant with { Revision = checked(before.Tenant.Revision + 1) } };
            ct.ThrowIfCancellationRequested(); foreach (var value in values) SaveExperience(value);
            _tenants[tenant] = next; _audit.Add(audit); return ValueTask.FromResult<TenantExperienceChangeResult?>(new(values.Length, next.Tenant.Revision));
        }
    }
}
