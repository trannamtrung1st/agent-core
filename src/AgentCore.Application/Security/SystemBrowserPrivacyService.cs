using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class SystemHumanSessionAuthority
{
    public static async ValueTask RequireAsync(IIamStore security, IdentityService identity, IamAuthorizer authorizer, HumanSessionReference human, string permission, CancellationToken ct)
    {
        var account = await identity.ValidateSessionAsync(human.AuthenticationSessionId, ct).ConfigureAwait(false);
        var state = await security.ReadSystemAsync(ct).ConfigureAwait(false);
        if (account?.AccountId != human.AccountId || !state.Principals.Any(p => p.PrincipalId == human.PrincipalId && p.AccountId == human.AccountId
            && p.TenantId is null && p.UserId is null && p.SystemIdentityId is null && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active))
            throw new AccessDeniedException("initiator_unavailable");
        await authorizer.RequireAsync(human.PrincipalId, permission, AccessScope.System, ct).ConfigureAwait(false);
    }
}

public sealed class SystemBrowserPrivacyService(IIamStore security, ISystemBrowserPrivacyStore store, BrowserPrivacyService privacy,
    IdentityService identity, IamAuthorizer authorizer, TimeProvider time, IIdGenerator ids)
{
    private ValueTask Require(HumanSessionReference human, CancellationToken ct) => SystemHumanSessionAuthority.RequireAsync(security, identity, authorizer, human, "system.governance.manage", ct);
    public async ValueTask<BrowserPrivacyView> ReadAsync(HumanSessionReference human, CancellationToken ct = default)
    {
        await Require(human, ct); var value = await privacy.ReadAsync(ct); await Require(human, ct); return value;
    }
    public async ValueTask<BrowserPrivacyView> SaveAsync(HumanSessionReference human, SystemBrowserPrivacyChange change, CancellationToken ct = default)
    {
        await Require(human, ct);
        // Validate and freeze the complete command before the owning transaction repeats deployment and human authority.
        var policy = privacy.Prepare(change);
        change = change with { UnmaskedOrigins = policy.UnmaskedOrigins, TrustedGraphicsOrigins = policy.TrustedGraphicsOrigins };
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var audit = new AuthorizationAudit(ids.NewId(), null, human.PrincipalId, "system.browser.privacy.update", "host", "allowed", "policy_saved_restart_required", at);
        if (!await store.TrySaveSystemBrowserPrivacyAsync(human, change, audit, ct)) throw new SecurityConflictException("stale_revision");
        return await ReadAsync(human, ct);
    }
}
