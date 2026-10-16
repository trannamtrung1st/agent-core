using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantOAuthSetup(bool Configured, bool Synthetic, bool HostEnabled);
public sealed record TenantOAuthStart(string AuthorizationUrl, string Correlation, Guid CredentialId, DateTimeOffset ExpiresAt, long SecurityRevision);
public sealed record TenantOAuthCompletion(Guid TenantId, Guid CredentialId, string Outcome);
public sealed record TenantOAuthDisconnected(TenantCredentialView Credential, bool ProviderRevoked);

public sealed class TenantCredentialOAuthService(IIamStore security, ITenantCredentialStore credentials, ITenantCredentialOAuthStore attempts,
    ITenantCredentialOAuthProvider provider, IdentityService identity, IamAuthorizer authorizer, TenantCredentialService views,
    IIdentityTokens tokens, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantOAuthSetup> SetupAsync(Guid actor, Guid tenant, Guid credential, CancellationToken ct = default)
    {
        var current = await Owned(tenant, credential, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "credentials.read", TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
        RequireOAuth(current.Value); var descriptor = provider.Describe(current.Value.Purpose);
        return new(descriptor.Configured, descriptor.Synthetic, descriptor.HostEnabled);
    }

    public async ValueTask<TenantOAuthStart> StartAsync(Guid tenant, Guid credential, CredentialOAuthInitiator initiator,
        long expectedSecurityRevision, long expectedCredentialRevision, CancellationToken ct = default)
    {
        var current = await Owned(tenant, credential, ct).ConfigureAwait(false);
        var state = await Authority(tenant, credential, initiator, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Value.Revision != expectedCredentialRevision) throw new SecurityConflictException("stale_revision");
        RequireOAuth(current.Value);
        if (current.Value.Status != CredentialStatus.Active) throw new AccessDeniedException("credential_disabled");
        var descriptor = provider.Describe(current.Value.Purpose); RequireProvider(descriptor);
        var attemptId = ids.NewId(); var stateProof = $"{credential:N}.{attemptId:N}.{tokens.Create()}"; var correlation = tokens.Create(); var now = time.GetUtcNow();
        var attempt = new CredentialOAuthAttempt(attemptId, tenant, credential, current.Value.Purpose, initiator.AccountId, initiator.AuthenticationSessionId,
            initiator.PrincipalId, current.Value.Revision, TenantCredentialOAuthRules.Epoch(current.Value), tokens.Hash(stateProof), tokens.Hash(correlation), "", descriptor, now, now.AddMinutes(5));
        var prepared = provider.Prepare(attempt, stateProof); attempt = attempt with { ProtectedProof = prepared.ProtectedProof };
        if (!await attempts.TryBeginCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt, Audit(attempt, "oauth.start"), ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return new(prepared.AuthorizationUrl, correlation, credential, attempt.ExpiresAt, checked(state.Tenant.Revision + 1));
    }

    public async ValueTask<CredentialPurpose> SyntheticConsentAsync(string? state, string? correlation, Guid? browserAccount, CancellationToken ct = default)
    {
        var a = await Pending(state, correlation, browserAccount, true, ct).ConfigureAwait(false);
        await Current(a, ct).ConfigureAwait(false); RequireSameProvider(a);
        return a.Purpose;
    }

    public async ValueTask<TenantOAuthCompletion> CompleteAsync(string? state, string? correlation, Guid? browserAccount,
        string? code, string? error, bool syntheticCallback, CancellationToken ct = default)
    {
        // Purpose-specific proof is required even when the Strict Account cookie is absent on a provider redirect.
        var a = await Pending(state, correlation, browserAccount, syntheticCallback, ct).ConfigureAwait(false);
        var (before, current) = await Current(a, ct).ConfigureAwait(false); RequireSameProvider(a);
        if (code is { Length: > 8192 } || error is { Length: > 256 }) throw InvalidCallback();
        var claim = await attempts.TryClaimCredentialOAuthAsync(a.AttemptId, tokens.Hash(state!), tokens.Hash(correlation!), before.Tenant.Revision, ct).ConfigureAwait(false);
        if (claim is null) throw InvalidCallback();
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            var denied = error == "access_denied";
            await Finish(claim, denied ? CredentialOAuthOutcome.Denied : CredentialOAuthOutcome.Failed).ConfigureAwait(false);
            return new(a.TenantId, a.CredentialId, denied ? "denied" : "failed");
        }
        try
        {
            var remaining = a.ExpiresAt - time.GetUtcNow();
            using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time);
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var grant = await provider.ExchangeAsync(claim, code, current.Value, exchange.Token).ConfigureAwait(false);
            // Provider I/O is outside locks. Permissions, original session and all Credential witnesses are fresh again.
            var (fresh, retained) = await Current(a, ct).ConfigureAwait(false); RequireSameProvider(a);
            var value = TenantCredentialOAuthRules.Connect(retained.Value, grant, time.GetUtcNow());
            if (!await attempts.TryCommitCredentialOAuthAsync(Next(fresh), fresh.Tenant.Revision, a.AttemptId, claim.Attempt.Revision,
                value, retained.Value.SecretRevision, Audit(a, "oauth.connect"), ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
            return new(a.TenantId, a.CredentialId, "connected");
        }
        catch (OperationCanceledException)
        {
            await Finish(claim, CredentialOAuthOutcome.Failed).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            return new(a.TenantId, a.CredentialId, "failed");
        }
        catch (Exception ex) when (ex is AgentCoreException or AccessDeniedException or SecurityConflictException or ArgumentException)
        {
            await Finish(claim, CredentialOAuthOutcome.Failed).ConfigureAwait(false);
            return new(a.TenantId, a.CredentialId, "failed");
        }
    }

    public async ValueTask<TenantOAuthDisconnected> DisconnectAsync(Guid tenant, Guid credential, CredentialOAuthInitiator initiator,
        long expectedSecurityRevision, long expectedCredentialRevision, CancellationToken ct = default)
    {
        var current = await Owned(tenant, credential, ct).ConfigureAwait(false);
        var state = await Authority(tenant, credential, initiator, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Value.Revision != expectedCredentialRevision) throw new SecurityConflictException("stale_revision");
        RequireOAuth(current.Value);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, initiator.PrincipalId, "credentials.oauth.disconnect", credential.ToString("D"), "allowed", "local_grant_removed", time.GetUtcNow());
        if (!await attempts.TryDisconnectCredentialOAuthAsync(Next(state), state.Tenant.Revision, credential, initiator, current.Value.Revision, current.Value.SecretRevision, audit, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        var revoked = string.IsNullOrEmpty(current.Value.ProtectedPayload) || await provider.RevokeAsync(current.Value, ct).ConfigureAwait(false);
        return new(await views.ReadAsync(initiator.PrincipalId, tenant, credential, ct).ConfigureAwait(false), revoked);
    }

    public static bool TryState(string? state, out Guid credential, out Guid attempt)
    {
        credential = attempt = Guid.Empty;
        if (state is not { Length: >= 90 and <= 160 }) return false;
        var parts = state.Split('.');
        return parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out credential) && credential != Guid.Empty
            && Guid.TryParseExact(parts[1], "N", out attempt) && attempt != Guid.Empty
            && parts[2].Length is >= 32 and <= 86 && parts[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
    private async ValueTask<CredentialOAuthAttempt> Pending(string? state, string? correlation, Guid? browserAccount, bool synthetic, CancellationToken ct)
    {
        if (!TryState(state, out var credential, out var id) || correlation is not { Length: >= 32 and <= 128 }) throw InvalidCallback();
        var a = await attempts.ReadCredentialOAuthAttemptAsync(id, ct).ConfigureAwait(false);
        if (a is null || a.CredentialId != credential || a.Provider.Synthetic != synthetic || a.Outcome != CredentialOAuthOutcome.Pending || a.ExpiresAt <= time.GetUtcNow()
            || browserAccount is { } account && account != a.AccountId
            || !TenantCredentialOAuthRules.DigestMatches(a.StateHash, tokens.Hash(state!)) || !TenantCredentialOAuthRules.DigestMatches(a.CorrelationHash, tokens.Hash(correlation))) throw InvalidCallback();
        return a;
    }
    private async ValueTask<(TenantSecurityState State, TenantCredential Credential)> Current(CredentialOAuthAttempt a, CancellationToken ct)
    {
        var current = await Owned(a.TenantId, a.CredentialId, ct).ConfigureAwait(false);
        var state = await Authority(a.TenantId, a.CredentialId, new(a.AccountId, a.AuthenticationSessionId, a.PrincipalId), ct).ConfigureAwait(false);
        if (current.Value.Revision != a.CredentialRevision || current.Value.Purpose != a.Purpose || !TenantCredentialOAuthRules.IsOAuth(current.Value)
            || current.Value.Status != CredentialStatus.Active || TenantCredentialOAuthRules.Epoch(current.Value) != a.AuthEpoch || a.ExpiresAt <= time.GetUtcNow()) throw InvalidCallback();
        return (state, current);
    }
    private ValueTask<TenantSecurityState> Authority(Guid tenant, Guid credential, CredentialOAuthInitiator initiator, CancellationToken ct) =>
        TenantCredentialOAuthAuthority.RequireAsync(security, identity, authorizer, tenant, credential, initiator, false, ct);
    private async ValueTask Finish(CredentialOAuthClaim claim, CredentialOAuthOutcome outcome)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        await attempts.FinishCredentialOAuthAsync(claim.Attempt.AttemptId, claim.Attempt.Revision, outcome, cleanup.Token).ConfigureAwait(false);
    }
    private void RequireSameProvider(CredentialOAuthAttempt a)
    {
        var current = provider.Describe(a.Purpose); RequireProvider(current);
        if (a.Provider != current) throw new SecurityConflictException("oauth_client_changed");
    }
    private static void RequireProvider(CredentialOAuthDescriptor d)
    {
        if (!d.HostEnabled) throw new AccessDeniedException("oauth_host_disabled");
        if (!d.Configured) throw new AgentCoreException("oauth_not_configured", "Ask the operator to configure shared Google OAuth, then connect again.", 409);
    }
    private static void RequireOAuth(Credential current) { if (!TenantCredentialOAuthRules.IsOAuth(current)) throw AgentCoreErrors.Validation("This credential does not use Google OAuth."); }
    private async ValueTask<TenantCredential> Owned(Guid tenant, Guid credential, CancellationToken ct) =>
        await credentials.FindTenantCredentialAsync(tenant, credential, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(CredentialOAuthAttempt a, string action) => new(ids.NewId(), a.TenantId, a.PrincipalId, "credentials." + action, a.CredentialId.ToString("D"), "allowed",
        action == "oauth.start" ? "oauth_attempt_started" : "oauth_grant_connected", time.GetUtcNow());
    private static AccessDeniedException InvalidCallback() => new("oauth_callback_unavailable");
}
