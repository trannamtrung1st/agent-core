using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxInstanceVerificationView(Guid InstanceId, SandboxVerificationAttempt? Attempt, SandboxProfileVerification? Observation,
    string Status, bool CanVerify, long CurrentInstanceRevision, long CurrentProfileRevision, long SecurityRevision);

public sealed class TenantSandboxInstanceVerificationService(IIamStore security, ITenantAgentStore agents, ITenantSandboxProfileStore profiles,
    ITenantSandboxVerificationStore store, TenantSandboxSetupResolver setups, TenantSandboxImageAuthorization authorization,
    ISandboxImageAuthority images, ITenantSandboxVerifier verifier, ISandboxVerificationHost host, ISandboxReadiness readiness,
    TenantSandboxVerificationService dispatch, IdentityService identity, IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantSandboxInstanceVerificationView> ReadAsync(Guid tenant, Guid instance, string profileId, HumanSessionReference human,
        Guid? attemptId = null, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(tenant, instance, profileId, human, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var profile = await profiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var attempt = await store.FindSandboxVerificationAsync(tenant, profileId, attemptId, ct, instance).ConfigureAwait(false);
        if (attemptId is not null && attempt is null) throw new AccessDeniedException("resource_unavailable");
        var evidence = await store.ReadSandboxInstanceVerificationAsync(tenant, instance, agent.Instance.Revision, profileId, profile.Revision, ct).ConfigureAwait(false);
        var source = await images.InspectAsync(profile, ct, requireVerification: false).ConfigureAwait(false);
        var observation = evidence.Observation;
        if (observation?.TenantId != tenant || observation.InstanceId != instance || observation.InstanceRevision != agent.Instance.Revision
            || observation.ProfileId != profile.Id || observation.ProfileRevision != profile.Revision || !observation.IncludesInitialization
            || observation.HostScope != host.Scope || observation.ContractVersion != SandboxProfileVerification.CurrentContractVersion
            || observation.RegistryCredentialId != profile.RegistryCredentialId || observation.RegistryCredentialRevision != source.CredentialRevision
            || observation.RegistrySecretRevision != source.SecretRevision) observation = null;
        var active = agent.Instance.Lifecycle == AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active
            && state.SystemIdentities.Any(i => i.AgentInstanceId == instance && i.Status == IdentityStatus.Active);
        var canVerify = active && source.Status == "SourceAuthorized" && (await authorizer.CheckAsync(human.PrincipalId, "sandbox.manage", AgentScope(tenant, instance), ct).ConfigureAwait(false)).Allowed;
        if (canVerify && profile.RegistryCredentialId is { } credential)
            foreach (var key in new[] { "credentials.read", "credentials.bind", "credentials.use" })
                canVerify &= (await authorizer.CheckAsync(human.PrincipalId, key, TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false)).Allowed;
        if (canVerify)
        {
            try
            {
                var setup = await setups.ResolveAsync(tenant, instance, profileId, ct).ConfigureAwait(false);
                canVerify = setup.Agent.Instance.Revision == agent.Instance.Revision && setup.Profile.Revision == profile.Revision;
            }
            catch (AccessDeniedException) { canVerify = false; }
            catch (AgentCoreException error) when (error.Code == "ValidationError") { canVerify = false; }
        }
        var status = evidence.Pending ? "VerificationPending" : !active ? "Inactive" : !profile.Enabled ? "Disabled" : source.Status != "SourceAuthorized" ? source.Status
            : observation is null ? "NeedsVerification" : !observation.Passed ? "VerificationFailed" : readiness.State != "Ready" ? "ServerUnavailable" : "Verified";
        var currentAgent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false);
        await RequireRead(tenant, instance, profileId, human, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision || currentAgent?.Instance.Revision != agent.Instance.Revision)
            throw new SecurityConflictException("stale_revision");
        return new(instance, attempt, observation, status, canVerify && !evidence.Pending, agent.Instance.Revision, profile.Revision, state.Tenant.Revision);
    }

    public async ValueTask<TenantSandboxInstanceVerificationView> VerifyAsync(Guid tenant, Guid instance, string profileId, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, long expectedProfileRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(tenant, instance, profileId, human, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(human.PrincipalId, "sandbox.manage", AgentScope(tenant, instance), ct).ConfigureAwait(false);
        var setup = await setups.ResolveAsync(tenant, instance, profileId, ct).ConfigureAwait(false);
        var proof = new SandboxImageAuthorization(human, state.Tenant.Revision, setup.Profile.Revision, instance, setup.Agent.Instance.Revision);
        await authorization.RequireAsync(setup.Profile, proof, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || setup.Agent.Instance.Revision != expectedInstanceRevision || setup.Profile.Revision != expectedProfileRevision)
            throw new SecurityConflictException("stale_revision");
        var source = await images.InspectAsync(setup.Profile, ct, requireVerification: false).ConfigureAwait(false);
        if (source.Status != "SourceAuthorized") throw AgentCoreErrors.Validation(source.Message);
        var profile = setup.Profile with { RegistryCredentialRevision = source.CredentialRevision, RegistrySecretRevision = source.SecretRevision };
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; var started = time.GetUtcNow();
        var attempt = new SandboxVerificationAttempt(ids.NewId(), tenant, profileId, profile.Revision, false, 1, SandboxVerificationStatus.Running,
            started, started + TenantSandboxVerificationRules.MaximumLease, human.AccountId, human.AuthenticationSessionId, human.PrincipalId,
            next.Tenant.Revision, verifier.HostScope, profile.RegistryCredentialId, profile.RegistryCredentialRevision, profile.RegistrySecretRevision,
            InstanceId: instance, InstanceRevision: setup.Agent.Instance.Revision);
        if (!await store.TryBeginSandboxVerificationAsync(next, state.Tenant.Revision, attempt, profile, human,
            new(ids.NewId(), tenant, human.PrincipalId, "agents.sandbox.verification.begin", instance.ToString("D"), "allowed", "sandbox_verification", started), ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        await dispatch.CompleteAsync(attempt, profile, proof with { SecurityRevision = next.Tenant.Revision }, ct).ConfigureAwait(false);
        return await ReadAsync(tenant, instance, profileId, human, attempt.AttemptId, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireRead(Guid tenant, Guid instance, string profile, HumanSessionReference human, CancellationToken ct)
    {
        if (instance == Guid.Empty || !TenantSandboxProfileRules.ValidId(profile)) throw new AccessDeniedException("resource_unavailable");
        foreach (var key in new[] { "agents.read", "sandbox.read" }) await authorizer.RequireAsync(human.PrincipalId, key, AgentScope(tenant, instance), ct).ConfigureAwait(false);
        await authorizer.RequireAsync(human.PrincipalId, "sandbox.read", TenantSandboxProfileRules.Scope(tenant, profile), ct).ConfigureAwait(false);
    }
    private static AccessScope AgentScope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
}
