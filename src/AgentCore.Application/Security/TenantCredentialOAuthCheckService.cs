using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using System.Collections.Concurrent;

namespace AgentCore.Application.Security;

public sealed record TenantOAuthChecked(TenantCredentialView Credential, bool ReconnectRequired);

public sealed class TenantCredentialOAuthCheckService(IIamStore security, ITenantCredentialStore credentials, ITenantCredentialOAuthStore attempts,
    ITenantCredentialOAuthVerifier verifier, IdentityService identity, IamAuthorizer authorizer, TenantCredentialService views, IIdGenerator ids, TimeProvider time)
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Credential), SemaphoreSlim> checks = new();
    public async ValueTask<TenantOAuthChecked> CheckAsync(Guid tenant, Guid id, CredentialOAuthInitiator initiator,
        long expectedSecurityRevision, long expectedCredentialRevision, CancellationToken ct = default)
    {
        var current = await Owned(tenant, id, ct).ConfigureAwait(false);
        var state = await Authority(tenant, id, initiator, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Revision != expectedCredentialRevision) throw new SecurityConflictException("stale_revision");
        var gate = checks.GetOrAdd((tenant, id), _ => new(1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new SecurityConflictException("oauth_check_in_progress");
        try
        {
            if ((await Authority(tenant, id, initiator, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            RequireConnected(current);
            var provider = verifier.Describe(current.Purpose); RequireProvider(provider);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            CredentialOAuthCheck check;
            try { check = await verifier.CheckAsync(current, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentCoreException("oauth_check_unavailable", "Google access could not be checked. The saved connection was retained. Try again.", 503); }
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new AgentCoreException("oauth_check_unavailable", "Google access could not be checked. The saved connection was retained. Try again.", 503);
            // Recheck authority before stale witnesses so a revoked caller gains no state oracle.
            var fresh = await Authority(tenant, id, initiator, ct).ConfigureAwait(false);
            var retained = await Owned(tenant, id, ct).ConfigureAwait(false);
            if (retained.Revision != current.Revision || retained.SecretRevision != current.SecretRevision) throw new SecurityConflictException("stale_revision");
            RequireConnected(retained);
            if (verifier.Describe(retained.Purpose) != provider) throw new SecurityConflictException("oauth_client_changed");
            var next = fresh with { Tenant = fresh.Tenant with { Revision = checked(fresh.Tenant.Revision + 1) } };
            var audit = new AuthorizationAudit(ids.NewId(), tenant, initiator.PrincipalId, "credentials.oauth.check", id.ToString("D"), "allowed",
                check.ReconnectRequired ? "oauth_reconnect_required" : "oauth_access_checked", time.GetUtcNow());
            if (!await attempts.TryCheckCredentialOAuthAsync(next, fresh.Tenant.Revision, id, initiator, retained.Revision, retained.SecretRevision, check, audit, ct).ConfigureAwait(false))
                throw new SecurityConflictException("stale_revision");
            return new(await views.ReadAsync(initiator.PrincipalId, tenant, id, ct, initiator: initiator).ConfigureAwait(false), check.ReconnectRequired);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<Credential> Owned(Guid tenant, Guid id, CancellationToken ct) =>
        (await credentials.FindTenantCredentialAsync(tenant, id, ct).ConfigureAwait(false))?.Value ?? throw new AccessDeniedException("resource_unavailable");
    private ValueTask<TenantSecurityState> Authority(Guid tenant, Guid id, CredentialOAuthInitiator initiator, CancellationToken ct) =>
        TenantCredentialOAuthAuthority.RequireAsync(security, identity, authorizer, tenant, id, initiator, true, ct);
    private static void RequireConnected(Credential value)
    {
        if (!TenantCredentialOAuthRules.IsOAuth(value) || value.Status != CredentialStatus.Active || value.ProtectedPayload.Length == 0
            || !(value.EmailAccount?.ConnectionStatus == EmailConnectionStatus.Connected || value.DriveAccount?.ConnectionStatus == DriveConnectionStatus.Connected))
            throw new AccessDeniedException("oauth_reconnect_required");
    }
    private static void RequireProvider(CredentialOAuthDescriptor value)
    {
        if (!value.Configured) throw new AgentCoreException("oauth_not_configured", "Configure shared Google OAuth before checking access.", 409);
        if (!value.HostEnabled) throw new AccessDeniedException("oauth_host_disabled");
    }
}
