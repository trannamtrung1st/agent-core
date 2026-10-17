using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxVerificationView(SandboxVerificationAttempt? Attempt, long CurrentProfileRevision, long SecurityRevision);

public sealed class TenantSandboxVerificationService(IIamStore security, ITenantSandboxVerificationStore store,
    TenantSandboxProfileService profiles, TenantSandboxImageAuthorization authorization, ISandboxImageAuthority images,
    ITenantSandboxVerifier verifier, IdentityService identity, IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantSandboxVerificationView> ReadAsync(Guid tenant, string profileId, HumanSessionReference human,
        Guid? attemptId = null, CancellationToken ct = default)
    {
        var view = await profiles.ReadAsync(tenant, profileId, human, ct: ct).ConfigureAwait(false);
        var attempt = await store.FindSandboxVerificationAsync(tenant, profileId, attemptId, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(human.PrincipalId, "sandbox.read", TenantSandboxProfileRules.Scope(tenant, profileId), ct).ConfigureAwait(false);
        if (current.Tenant.Revision != view.SecurityRevision) throw new SecurityConflictException("stale_revision");
        if (attemptId is not null && attempt is null) throw new AccessDeniedException("resource_unavailable");
        return new(attempt, view.Profile.Revision, view.SecurityRevision);
    }
    public async ValueTask<TenantSandboxVerificationView> VerifyAsync(Guid tenant, string profileId, HumanSessionReference human,
        long expectedSecurityRevision, long expectedProfileRevision, bool imageOnly = false, CancellationToken ct = default)
    {
        var view = await profiles.ReadAsync(tenant, profileId, human, ct: ct).ConfigureAwait(false);
        var proof = new SandboxImageAuthorization(human, view.SecurityRevision, view.Profile.Revision);
        await authorization.RequireAsync(view.Profile, proof, ct).ConfigureAwait(false);
        if (view.SecurityRevision != expectedSecurityRevision || view.Profile.Revision != expectedProfileRevision) throw new SecurityConflictException("stale_revision");
        var source = await images.InspectAsync(view.Profile, ct, requireVerification: false).ConfigureAwait(false);
        if (source.Status != "SourceAuthorized") throw AgentCoreErrors.Validation(source.Message);
        var profile = view.Profile with { RegistryCredentialRevision = source.CredentialRevision, RegistrySecretRevision = source.SecretRevision };
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; var started = time.GetUtcNow();
        var attempt = new SandboxVerificationAttempt(ids.NewId(), tenant, profileId, profile.Revision, imageOnly, 1, SandboxVerificationStatus.Running,
            started, started + TenantSandboxVerificationRules.MaximumLease, human.AccountId, human.AuthenticationSessionId, human.PrincipalId,
            next.Tenant.Revision, verifier.HostScope, profile.RegistryCredentialId, profile.RegistryCredentialRevision, profile.RegistrySecretRevision);
        if (!await store.TryBeginSandboxVerificationAsync(next, expectedSecurityRevision, attempt, profile, human, Audit(attempt, "sandbox.verification.begin"), ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        SandboxVerificationOutcome outcome;
        try
        {
            outcome = await verifier.VerifyAsync(attempt, profile, proof with { SecurityRevision = next.Tenant.Revision }, ct).ConfigureAwait(false);
            TenantSandboxVerificationRules.ValidateOutcome(attempt, outcome);
        }
        catch
        {
            // Unknown dispatch/adapter failures retain exact cleanup ownership. Recovery never
            // repeats initialization and cannot treat a missing response as successful verification.
            outcome = new(new(profileId, profile.Revision, time.GetUtcNow(), false, "Verification did not return trusted completion evidence. Exact cleanup must be confirmed before retrying.",
                "cleanup", ContractVersion: SandboxProfileVerification.CurrentContractVersion, RegistryCredentialId: attempt.RegistryCredentialId,
                RegistryCredentialRevision: attempt.RegistryCredentialRevision, RegistrySecretRevision: attempt.RegistrySecretRevision,
                TenantId: tenant, IncludesInitialization: !imageOnly, HostScope: attempt.HostScope), false);
        }
        using (var settle = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            _ = await store.FinishSandboxVerificationAsync(tenant, attempt.AttemptId, attempt.Revision, outcome, time.GetUtcNow(), Audit(attempt, "sandbox.verification.finish"), settle.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await ReadAsync(tenant, profileId, human, attempt.AttemptId, ct).ConfigureAwait(false);
    }
    private AuthorizationAudit Audit(SandboxVerificationAttempt a, string action) => new(ids.NewId(), a.TenantId, a.PrincipalId, action, a.ProfileId, "allowed", "sandbox_verification", time.GetUtcNow());
}

public sealed class TenantSandboxVerificationCleanup(ITenantSandboxVerificationStore store, ITenantSandboxVerifier verifier, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask RunPassAsync(CancellationToken ct = default)
    {
        var claims = await store.ClaimSandboxVerificationCleanupAsync(time.GetUtcNow(), ids.NewId(), ct: ct).ConfigureAwait(false);
        foreach (var claim in claims)
        {
            var confirmed = false;
            try { confirmed = await verifier.CleanupAsync(claim, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Exact durable ownership remains for the next pass. */ }
            finally
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _ = await store.FinishSandboxVerificationCleanupAsync(claim, confirmed, time.GetUtcNow(), settle.Token).ConfigureAwait(false);
            }
        }
    }
}
