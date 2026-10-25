using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record TenantSpaceAccess(IReadOnlyList<string> Permissions, long SecurityRevision, long SpaceRevision);
public sealed record TenantMembershipBindings(SpaceMembership? Membership, IReadOnlyList<SpaceResourceBinding> Inherited, IReadOnlyList<SpaceResourceBinding> Effective, long SecurityRevision, long SpaceRevision);

public sealed partial class TenantSpaceService(IIamStore security, ITenantSpaceStore spaces, ITenantAgentStore agents, IamAuthorizer authorizer,
    TenantRunConfigurationResolver configurations, ITenantCredentialBindingStore credentials, ITenantCredentialStore credentialResources,
    IIdGenerator ids, TimeProvider time, ITenantSecretStore? secretResources = null, IdentityService? identity = null, ITenantEnvironmentStore? environmentChoices = null, ITenantSpaceResourceStore? resourceSources = null,
    ITenantWorkspaceArtifactSourceStore? workspaceSources = null, IDefinitionResourceContentStore? resourceContent = null)
{
    public async ValueTask<Space> PrivateAsync(Guid actor, Guid tenant, Guid instance, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        if (await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) is null) throw new AccessDeniedException("resource_unavailable");
        var space = await spaces.FindPrivateSpaceAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        await authorizer.RequireAsync(actor, "spaces.read", Scope(tenant, space.SpaceId), ct).ConfigureAwait(false);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return space;
    }
    public async ValueTask<SpacePage> ListAsync(Guid actor, Guid tenant, int page = 0, int size = 30, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var all = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("spaces.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        List<Guid>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor).Select(a => a.Scope).Distinct())
                if (scope.ResourceType == "space" && Guid.TryParseExact(scope.ResourceId, "D", out var id)
                    && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("spaces.read"), scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(id);
        }
        var result = await spaces.ListSpacesAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(size, 1, 100), ct).ConfigureAwait(false);
        await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<Space> ReadAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var space = await spaces.FindSpaceAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await authorizer.RequireAsync(actor, "spaces.read", Scope(tenant, id), ct).ConfigureAwait(false);
        await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return space;
    }
    public async ValueTask<TenantSpaceAccess> AccessAsync(Guid actor, Guid tenant, Guid id, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var space = await ReadAsync(actor, tenant, id, ct, initiator).ConfigureAwait(false); var keys = new List<string>();
        foreach (var key in new[] { "spaces.read", "spaces.manage", "spaces.delete", "space-memberships.read", "space-memberships.manage", "environments.read", "environments.manage", "environments.activate", "variables.read", "variables.manage", "variables.resolve", "iam.roles.read", "iam.roles.assign" })
            if ((key != "spaces.delete" || space.Kind == SpaceKind.Shared) && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), Scope(tenant, id), ct).ConfigureAwait(false)).Allowed) keys.Add(key);
        if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("resources.manage"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed
            && keys.Contains("spaces.manage") && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("workspaces.read"), TenantWorkspaceRules.Home(tenant, id), ct).ConfigureAwait(false)).Allowed) keys.Add("resources.manage");
        await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return new(keys, state.Tenant.Revision, space.Revision);
    }
    public async ValueTask<TenantMembershipBindings> MemberBindingsAsync(Guid actor, Guid tenant, Guid id, Guid instance, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var space = await ReadAsync(actor, tenant, id, ct, initiator).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "space-memberships.read", Scope(tenant, id), ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        var inherited = await InheritedAsync(tenant, instance, ct).ConfigureAwait(false);
        var member = (await spaces.ReadMembershipsAsync(tenant, id, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == instance);
        var effective = member is null ? inherited : EffectiveSpaceBindings.Resolve(inherited, member);
        await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return new(member, inherited, effective, state.Tenant.Revision, space.Revision);
    }
    public async ValueTask<Space> CreateAsync(Guid actor, Guid tenant, Guid id, string name, string description, string instructions,
        long expectedSecurityRevision, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        if (expectedSecurityRevision < 1) throw new ArgumentException("Use a valid expected security revision.");
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var allowed = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("spaces.create"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (!allowed.Allowed) throw new AccessDeniedException(allowed.ReasonCode);
        var existing = await spaces.FindSpaceAsync(tenant, id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await authorizer.RequireAsync(actor, "spaces.read", Scope(tenant, id), ct).ConfigureAwait(false);
            if (existing.Revision != 1 || existing.Kind != SpaceKind.Shared || existing.Name != name.Trim() || existing.Description != description
                || existing.Instructions != instructions || existing.EnvironmentsEnabled) throw new SecurityConflictException("create_identity_conflict");
            await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return existing;
        }
        if (state.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var at = Now(); var space = new Space(tenant, id, SpaceKind.Shared, null, name.Trim(), description, instructions, false, 1, at, at);
        await Commit(state, new(space, 0), actor, "spaces.create", initiator, ct).ConfigureAwait(false); return space;
    }
    public async ValueTask<Space> UpdateAsync(Guid actor, Guid tenant, Guid id, string name, string description, string instructions,
        bool environmentsEnabled, long expectedSecurityRevision, long expectedRevision, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        var space = await ReadAsync(actor, tenant, id, ct).ConfigureAwait(false);
        var state = await ChangeState(actor, tenant, "spaces.manage", Scope(tenant, id), expectedSecurityRevision, ct).ConfigureAwait(false);
        if (space.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        var next = space with { Name = name.Trim(), Description = description, Instructions = instructions, EnvironmentsEnabled = environmentsEnabled, EnvironmentSelectionEpoch = space.EnvironmentsEnabled && !environmentsEnabled ? checked(space.EnvironmentSelectionEpoch + 1) : space.EnvironmentSelectionEpoch, Revision = checked(space.Revision + 1), UpdatedAt = Now() };
        await Commit(state, new(next, expectedRevision), actor, "spaces.manage", initiator, ct).ConfigureAwait(false); return next;
    }
    public async ValueTask DeleteAsync(Guid actor, Guid tenant, Guid id, long expectedSecurityRevision, long expectedRevision,
        HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        var space = await ReadAsync(actor, tenant, id, ct).ConfigureAwait(false);
        var state = await ChangeState(actor, tenant, "spaces.delete", Scope(tenant, id), expectedSecurityRevision, ct).ConfigureAwait(false);
        if (space.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        state = state with { Assignments = state.Assignments.Where(a => a.Scope != Scope(tenant, id)
            && a.Scope != TenantWorkspaceRules.Home(tenant, id) && !(TenantResourceScopes.TrySpace(a.Scope, out var sourceSpace, out _) && sourceSpace == id)).ToArray() };
        await Commit(state, new(space with { Revision = checked(space.Revision + 1), UpdatedAt = Now() }, expectedRevision, Delete: true), actor, "spaces.delete", initiator, ct).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<SpaceMembership>> MembersAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ReadState(actor, tenant, initiator, ct).ConfigureAwait(false);
        await ReadAsync(actor, tenant, id, ct, initiator).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "space-memberships.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var result = await spaces.ReadMembershipsAsync(tenant, id, ct).ConfigureAwait(false);
        await CompleteRead(actor, state, initiator, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<SpaceMembership> SetMemberAsync(Guid actor, Guid tenant, Guid id, Guid instance, string responsibility,
        string instructions, IReadOnlyList<string> disabled, IReadOnlyList<SpaceResourceBinding> added, long expectedSecurityRevision,
        long expectedSpaceRevision, long expectedMembershipRevision, bool remove = false, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        var space = await ReadAsync(actor, tenant, id, ct).ConfigureAwait(false);
        var state = await ChangeState(actor, tenant, "space-memberships.manage", Scope(tenant, id), expectedSecurityRevision, ct).ConfigureAwait(false);
        if (space.Revision != expectedSpaceRevision) throw new SecurityConflictException("stale_revision");
        await authorizer.RequireAsync(actor, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        var old = (await spaces.ReadMembershipsAsync(tenant, id, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == instance);
        if ((old?.Revision ?? 0) != expectedMembershipRevision) throw new SecurityConflictException("stale_revision");
        var at = Now(); var member = new SpaceMembership(tenant, id, instance, responsibility, instructions, disabled, added, checked(expectedMembershipRevision + 1), old?.CreatedAt ?? at, at).Freeze();
        var inherited = await InheritedAsync(tenant, instance, ct).ConfigureAwait(false);
        member = EffectiveSpaceBindings.Prune(member, inherited);
        EffectiveSpaceBindings.Resolve(inherited, member);
        foreach (var binding in member.AddedBindings)
        {
            binding.Validate();
            if (binding.Kind == SpaceBindingKind.Credential)
            {
                var credential = await credentialResources.FindTenantCredentialAsync(tenant, Guid.Parse(binding.ResourceKey), ct).ConfigureAwait(false);
                if (credential?.Value.Status != CredentialStatus.Active || CredentialRules.Reference(binding.AccountRef!) != binding.AccountRef) throw new AccessDeniedException("resource_unavailable");
                await authorizer.RequireAsync(actor, "credentials.bind", AccessScope.ForResource(tenant, "credential", binding.ResourceKey), ct).ConfigureAwait(false);
            }
            else if (binding.Kind == SpaceBindingKind.Secret)
            {
                if (secretResources is null || (await secretResources.FindSecretAsync(tenant, Guid.Parse(binding.ResourceKey), ct).ConfigureAwait(false))?.Status != SecretStatus.Active) throw new AccessDeniedException("resource_unavailable");
                await authorizer.RequireAsync(actor, "secrets.read", TenantSecretService.Scope(tenant, Guid.Parse(binding.ResourceKey)), ct).ConfigureAwait(false);
            }
            else if (binding.Kind == SpaceBindingKind.SpaceFile)
            {
                if (!SpaceResourceKeys.TryParse(binding.ResourceKey, out var sourceSpace, out var sourceId) || sourceSpace != id
                    || resourceSources is null || await resourceSources.FindSpaceResourceAsync(tenant, id, sourceId, ct).ConfigureAwait(false) is null)
                    throw new AccessDeniedException("resource_unavailable");
                await authorizer.RequireAsync(actor, "resources.read", TenantResourceScopes.Source(tenant, instance, binding.ResourceKey), ct).ConfigureAwait(false);
            }
            else if (!inherited.Any(b => b.StableKey == binding.StableKey)) throw new AccessDeniedException("resource_unavailable");
        }
        await Commit(state, new(space with { Revision = checked(space.Revision + 1), UpdatedAt = at }, expectedSpaceRevision, member, expectedMembershipRevision, remove), actor, "space-memberships.manage", initiator, ct).ConfigureAwait(false);
        return member;
    }
    public async ValueTask<IReadOnlyList<SpaceResourceBinding>> InheritedAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    {
        var resources = await configurations.CurrentResourcesAsync(tenant, instance, ct).ConfigureAwait(false);
        var bound = await credentials.ReadTenantCredentialBindingsAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return resources.Resources.Select(r => new SpaceResourceBinding(r.Key.StartsWith("definition:", StringComparison.Ordinal) ? SpaceBindingKind.DefinitionResource : SpaceBindingKind.InstanceResource, r.Key))
            .Concat(bound.Bindings.Select(b => new SpaceResourceBinding(SpaceBindingKind.Credential, b.Binding.CredentialId.ToString("D"), b.Binding.Reference))).ToArray();
    }
    private async ValueTask<TenantSecurityState> ReadState(Guid actor, Guid tenant, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is null) return await State(tenant, ct).ConfigureAwait(false);
        if (human.PrincipalId != actor || identity is null) throw new AccessDeniedException("initiator_unavailable");
        return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
    }
    private async ValueTask CompleteRead(Guid actor, TenantSecurityState before, HumanSessionReference? human, CancellationToken ct)
    { if ((await ReadState(actor, before.Tenant.TenantId, human, ct).ConfigureAwait(false)).Tenant.Revision != before.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask<TenantSecurityState> State(Guid tenant, CancellationToken ct) => await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
    private async ValueTask<TenantSecurityState> ChangeState(Guid actor, Guid tenant, string permission, AccessScope scope, long expected, CancellationToken ct)
    {
        var state = await State(tenant, ct).ConfigureAwait(false);
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision"); return state;
    }
    private async ValueTask Commit(TenantSecurityState state, SpaceChange change, Guid actor, string permission, HumanSessionReference? initiator, CancellationToken ct)
    {
        change.Space.Validate();
        if (!await spaces.TryChangeSpaceAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision,
            change, new(ids.NewId(), state.Tenant.TenantId, actor, permission, change.Space.SpaceId.ToString("D"), "allowed", "space_updated", Now()), initiator, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
    public static AccessScope Scope(Guid tenant, Guid space) => AccessScope.ForResource(tenant, "space", space.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    { id = default; return scope is { Kind: AccessScopeKind.Resource, ResourceType: "space", TenantId: not null } && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D"); }
}
