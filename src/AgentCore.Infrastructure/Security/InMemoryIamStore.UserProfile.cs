using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantUserProfileStore
{
    private readonly Dictionary<(Guid Tenant, Guid Principal), TenantUserProfile> userProfiles = [];
    public ValueTask<TenantUserProfileView> ReadOwnTenantUserProfileAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            RequireProfileHuman(tenant, human);
            var state = _tenants[tenant]; var user = state.Users.Single(u => u.PrincipalId == human.PrincipalId && u.AccountId == human.AccountId);
            var profile = userProfiles.GetValueOrDefault((tenant, human.PrincipalId)) ?? TenantUserProfileRules.Empty(user);
            if (profile.AccountId != human.AccountId) throw new AccessDeniedException("profile_unavailable");
            return ValueTask.FromResult(new TenantUserProfileView(profile.Freeze(), state.Tenant.Revision));
        }
    }
    public ValueTask<TenantUserProfileView?> TrySaveOwnTenantUserProfileAsync(TenantUserProfile profile, HumanSessionReference human,
        long expectedSecurityRevision, long expectedProfileRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); profile = profile.Freeze(); TenantUserProfileRules.Commit(profile, human, expectedSecurityRevision, expectedProfileRevision, audit);
        lock (_gate)
        {
            RequireProfileHuman(profile.TenantId, human); var before = _tenants[profile.TenantId];
            var old = userProfiles.GetValueOrDefault((profile.TenantId, profile.PrincipalId));
            if (before.Tenant.Revision != expectedSecurityRevision || (old?.Revision ?? 0) != expectedProfileRevision
                || old is not null && (old.AccountId != profile.AccountId || old.UpdatedAt > profile.UpdatedAt)
                || before.Users.Single(u => u.PrincipalId == human.PrincipalId).CreatedAt.ToUnixTimeMilliseconds() > profile.UpdatedAt.ToUnixTimeMilliseconds()
                || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult<TenantUserProfileView?>(null);
            ct.ThrowIfCancellationRequested();
            var next = before with { Tenant = before.Tenant with { Revision = checked(before.Tenant.Revision + 1) } };
            userProfiles[(profile.TenantId, profile.PrincipalId)] = profile; _tenants[profile.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantUserProfileView?>(new(profile.Freeze(), next.Tenant.Revision));
        }
    }
    private void RequireUserProfileReferences(TenantSecurityState next)
    {
        foreach (var profile in userProfiles.Values.Where(p => p.TenantId == next.Tenant.TenantId))
        {
            var prior = _tenants.GetValueOrDefault(next.Tenant.TenantId)?.Users.SingleOrDefault(u => u.PrincipalId == profile.PrincipalId && u.AccountId == profile.AccountId);
            if (prior is null || !next.Users.Any(u => u.UserId == prior.UserId && u.PrincipalId == profile.PrincipalId && u.AccountId == profile.AccountId))
                throw new ArgumentException("Retain the actual User Profile membership owner; change its status without reparenting it.");
        }
    }
    private void RequireProfileHuman(Guid tenant, HumanSessionReference human)
    {
        try { RequireHumanSession(tenant, human); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }
}
