using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantUserProfileService(IIamStore security, ITenantUserProfileStore profiles, IdentityService identity,
    IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantUserProfileView> ReadAsync(Guid tenant, HumanSessionReference human, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var before = await profiles.ReadOwnTenantUserProfileAsync(tenant, human, ct).ConfigureAwait(false);
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var after = await profiles.ReadOwnTenantUserProfileAsync(tenant, human, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (!Same(before, after) || current.Tenant.Revision != after.SecurityRevision) throw new SecurityConflictException("profile_changed");
        return before;
    }
    public async ValueTask<TenantUserProfileView> UpdateAsync(Guid tenant, HumanSessionReference human, TenantUserProfileChange change, CancellationToken ct = default)
    {
        if (change.Values is null || change.Values.Count is < 1 or > 4 || change.ExpectedSecurityRevision < 1 || change.ExpectedProfileRevision < 0)
            throw AgentCoreErrors.Validation("Supply current revisions and one to four Profile fields.");
        var values = change.Values.ToArray();
        foreach (var (key, value) in values)
        {
            if (!TenantUserProfile.Fields.Contains(key, StringComparer.Ordinal)) throw AgentCoreErrors.Validation("Unknown User Profile field.");
            if (value is not null)
                try { TenantUserProfile.ValidateField(key, key == "preferredName" ? value.Trim() : value); }
                catch (ArgumentException) { throw AgentCoreErrors.Validation("Invalid User Profile value. Use null to clear a field."); }
        }
        var before = await ReadAsync(tenant, human, ct).ConfigureAwait(false);
        if (before.SecurityRevision != change.ExpectedSecurityRevision || before.Profile.Revision != change.ExpectedProfileRevision) throw new SecurityConflictException("stale_revision");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        if (now < before.Profile.UpdatedAt) throw new SecurityConflictException("profile_changed");
        var merged = new Dictionary<string, UserProfileValue>(before.Profile.Preferences, StringComparer.Ordinal);
        foreach (var (key, value) in values)
            if (value is null) merged.Remove(key); else merged[key] = new(key == "preferredName" ? value.Trim() : value, UserProfileValueSource.UserSet, now);
        var next = (before.Profile with { Revision = checked(before.Profile.Revision + 1), Preferences = merged, UpdatedAt = now }).Freeze();
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "profile.update", human.PrincipalId.ToString("D"), "allowed", "profile_updated", now);
        var saved = await profiles.TrySaveOwnTenantUserProfileAsync(next, human, change.ExpectedSecurityRevision, change.ExpectedProfileRevision, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        var current = await ReadAsync(tenant, human, ct).ConfigureAwait(false);
        if (!Same(saved, current)) throw new SecurityConflictException("profile_changed");
        return saved;
    }
    private static bool Same(TenantUserProfileView a, TenantUserProfileView b) => a.SecurityRevision == b.SecurityRevision
        && a.Profile.TenantId == b.Profile.TenantId && a.Profile.PrincipalId == b.Profile.PrincipalId && a.Profile.AccountId == b.Profile.AccountId
        && a.Profile.Revision == b.Profile.Revision && a.Profile.UpdatedAt == b.Profile.UpdatedAt;
}
