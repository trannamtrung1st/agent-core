using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxProfileInput(string Name, string Description, bool Enabled, string ImageReference,
    string? InitializationScript = null, double Cpu = 1, int MemoryMiB = 1024, int Pids = 64, int MaxCommandSeconds = 120,
    int IdleTtlSeconds = 1800, int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024, Guid? RegistryCredentialId = null);
public sealed record TenantSandboxProfileView(SandboxProfile Profile, bool CanManage, long SecurityRevision, SandboxImageAvailability? Availability = null, SandboxProfileVerification? Verification = null);
public sealed record TenantSandboxCatalog(IReadOnlyList<TenantSandboxProfileView> Items, int Total, int Page, int PageSize, bool CanCreate, bool AllReadable, long SecurityRevision);

public sealed class TenantSandboxProfileService(IIamStore security, ITenantSandboxProfileStore profiles, IdentityService identity,
    IamAuthorizer authorizer, ISandboxImageAuthority images, IIdGenerator ids, TimeProvider time, ITenantSandboxVerificationStore? verifications = null, ISandboxReadiness? readiness = null)
{
    public async ValueTask<TenantSandboxCatalog> ListAsync(Guid tenant, HumanSessionReference human,
        int page = 0, int pageSize = 30, string? search = null, CancellationToken ct = default)
    {
        TenantSandboxProfileRules.Query(tenant, page, pageSize, search ?? ""); var query = (search ?? "").Trim();
        var state = await Human(tenant, human, ct).ConfigureAwait(false);
        var all = await Check(state, human.PrincipalId, "sandbox.read", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        List<string>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId && TenantSandboxProfileRules.TryId(a.Scope, out _)).Select(a => a.Scope).Distinct())
                if ((await Check(state, human.PrincipalId, "sandbox.read", scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(scope.ResourceId!);
        }
        var result = await profiles.ListTenantSandboxProfilesAsync(tenant, allowed, page, pageSize, query, ct).ConfigureAwait(false);
        var items = new List<TenantSandboxProfileView>();
        foreach (var profile in result.Items) items.Add(await Project(profile, (await Check(state, human.PrincipalId, "sandbox.manage", TenantSandboxProfileRules.Scope(tenant, profile.Id), ct).ConfigureAwait(false)).Allowed, state.Tenant.Revision, ct).ConfigureAwait(false));
        var create = all.Allowed && (await Check(state, human.PrincipalId, "sandbox.manage", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        await Fence(state, human, ct).ConfigureAwait(false);
        return new(items, result.Total, result.Page, result.PageSize, create, all.Allowed, state.Tenant.Revision);
    }
    public async ValueTask<TenantSandboxProfileView> ReadAsync(Guid tenant, string id, HumanSessionReference human, long? revision = null, CancellationToken ct = default)
    {
        if (!TenantSandboxProfileRules.ValidId(id) || revision is < 1) throw new AccessDeniedException("resource_unavailable");
        var state = await Human(tenant, human, ct).ConfigureAwait(false); var scope = TenantSandboxProfileRules.Scope(tenant, id);
        await Require(state, human.PrincipalId, "sandbox.read", scope, ct).ConfigureAwait(false);
        var value = await profiles.FindTenantSandboxProfileAsync(tenant, id, revision, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var manage = (await Check(state, human.PrincipalId, "sandbox.manage", scope, ct).ConfigureAwait(false)).Allowed;
        var result = await Project(value, manage, state.Tenant.Revision, ct).ConfigureAwait(false);
        await Fence(state, human, ct, scope).ConfigureAwait(false);
        return result;
    }
    public async ValueTask<TenantSandboxProfileView> SaveAsync(Guid tenant, string? id, HumanSessionReference human, TenantSandboxProfileInput input,
        long expectedSecurityRevision, long expectedProfileRevision, CancellationToken ct = default)
    {
        var create = id is null;
        if (!create && !TenantSandboxProfileRules.ValidId(id)) throw new AccessDeniedException("resource_unavailable");
        var state = await Human(tenant, human, ct).ConfigureAwait(false);
        var scope = create ? AccessScope.ForTenant(tenant) : TenantSandboxProfileRules.Scope(tenant, id!);
        await Require(state, human.PrincipalId, "sandbox.read", scope, ct).ConfigureAwait(false);
        await Require(state, human.PrincipalId, "sandbox.manage", scope, ct).ConfigureAwait(false);
        if ((create ? expectedProfileRevision != 0 : expectedProfileRevision < 1) || state.Tenant.Revision != expectedSecurityRevision)
            throw new SecurityConflictException("stale_revision");
        var old = create ? null : await profiles.FindTenantSandboxProfileAsync(tenant, id!, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!create && old!.Revision != expectedProfileRevision) throw new SecurityConflictException("stale_revision");
        var profile = new SandboxProfile(id ?? "p-" + ids.NewId().ToString("N"), checked(expectedProfileRevision + 1), input.Name, input.Description, input.Enabled,
            input.ImageReference, input.InitializationScript, input.Cpu, input.MemoryMiB, input.Pids, input.MaxCommandSeconds, input.IdleTtlSeconds,
            input.MaxLifetimeSeconds, input.StorageBytes, RegistryCredentialId: input.RegistryCredentialId, TenantId: tenant);
        var disabling = !profile.Enabled && old is not null && old.ImageReference == profile.ImageReference && old.RegistryCredentialId == profile.RegistryCredentialId;
        if (disabling) profile = profile with { RegistryCredentialRevision = old!.RegistryCredentialRevision, RegistrySecretRevision = old.RegistrySecretRevision };
        else
        {
            if (input.RegistryCredentialId is { } credential)
                foreach (var key in new[] { "credentials.read", "credentials.bind", "credentials.use" })
                    await Require(state, human.PrincipalId, key, TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
            profile = await images.ResolveAsync(profile, new SandboxImageAuthorization(human, state.Tenant.Revision, expectedProfileRevision), ct).ConfigureAwait(false);
        }
        profile.Validate();
        var root = new TenantSandboxProfileRoot(tenant, profile.Id, time.GetUtcNow());
        TenantSandboxProfileRules.Validate(root, profile, expectedProfileRevision);
        await Fence(state, human, ct, scope, manage: true).ConfigureAwait(false);
        var at = time.GetUtcNow(); var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        if (!await profiles.TrySaveTenantSandboxProfileAsync(next, expectedSecurityRevision, root, profile, expectedProfileRevision, human,
            new(ids.NewId(), tenant, human.PrincipalId, "sandbox.profile.save", profile.Id, "allowed", "sandbox_profile_changed", at), ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return new(profile, true, next.Tenant.Revision);
    }
    private async ValueTask<TenantSandboxProfileView> Project(SandboxProfile value, bool manage, long revision, CancellationToken ct)
    {
        var availability = await images.InspectAsync(value, ct).ConfigureAwait(false);
        var observation = verifications is null ? null : (await verifications.ReadSandboxVerificationAsync(value.TenantId!.Value, value.Id, value.Revision, ct).ConfigureAwait(false)).Observation;
        if (availability.Eligible && readiness is not null && readiness.State != "Ready")
            availability = availability with { Eligible = false, Status = "ServerUnavailable", Message = "This saved revision is verified, but the private server is unavailable or disabled." };
        return new(value, manage, revision, availability, observation);
    }
    private ValueTask<TenantSecurityState> Human(Guid tenant, HumanSessionReference human, CancellationToken ct) => TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
    private async ValueTask Fence(TenantSecurityState state, HumanSessionReference human, CancellationToken ct, AccessScope? scope = null, bool manage = false)
    {
        var current = await Human(state.Tenant.TenantId, human, ct).ConfigureAwait(false);
        if (scope is not null)
        {
            await Require(current, human.PrincipalId, "sandbox.read", scope, ct).ConfigureAwait(false);
            if (manage) await Require(current, human.PrincipalId, "sandbox.manage", scope, ct).ConfigureAwait(false);
        }
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }
    private ValueTask<AccessDecision> Check(TenantSecurityState state, Guid actor, string key, AccessScope scope, CancellationToken ct) => authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct);
    private async ValueTask Require(TenantSecurityState state, Guid actor, string key, AccessScope scope, CancellationToken ct)
    {
        var result = await Check(state, actor, key, scope, ct).ConfigureAwait(false);
        if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode);
    }
}
