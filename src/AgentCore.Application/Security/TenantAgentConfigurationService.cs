using AgentCore.Application.Admin;
using AgentCore.Application.Models;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentChildAccess(bool Skills, bool Resources, bool AllSkills, bool AllResources);
public sealed record TenantAgentEffectiveConfiguration(TenantAgent Agent, AgentDefinition Definition,
    AdminEffectiveConfiguration Effective, IReadOnlyList<EffectiveSkill> Skills,
    IReadOnlyList<EffectiveAgentResource> Resources, long SecurityRevision, TenantAgentChildAccess ChildAccess);

public sealed partial class TenantAgentConfigurationService(IIamStore security, ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, InheritedDefinitionResourceCatalog inheritedResources, IamAuthorizer authorizer,
    IModelCatalog models, IToolConfigurationGate tools, InstanceAutomationPolicy automationPolicy,
    IIdGenerator ids, TimeProvider time, AdminLifecycleCoordinator? lifecycleGate = null, SandboxPolicyEnforcement? sandbox = null,
    ProviderAliasSet? aliases = null, IBrowser? browser = null, IDefinitionResourceContentStore? content = null, IdentityService? identity = null)
{
    public async ValueTask<TenantAgentEffectiveConfiguration> ReadAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, "agents.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        var (baseline, _, status) = await Definition(tenant, snapshot.Agent.Instance.DefinitionId, snapshot.Agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        var resources = await Resources(tenant, baseline, ct).ConfigureAwait(false);
        var effective = InstanceSettingsResolver.Resolve(baseline, snapshot.Agent.Instance.SettingsOverrides) with
            { SelfManagement = ManagementPolicyResolver.Resolve(baseline, snapshot.Agent.Instance) };
        var manifest = AgentInstanceResourceService.Resolve(effective, resources, snapshot.Resources, snapshot.Skills);
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var parent = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agents.read"), Scope(tenant, id), ct).ConfigureAwait(false);
        if (!parent.Allowed) throw new AccessDeniedException(parent.ReasonCode);
        var skillKeys = effective.SkillList.Select(s => "definition:" + s.Id).Concat(snapshot.Skills.InstanceSkills.Select(s => "instance:" + s.SkillId))
            .Concat(ManagementBuiltInSkills.Catalog.Select(s => s.Key)).ToArray();
        var readableSkills = await VisibleChildren(state, actor, tenant, id, "skills.read", skillKeys, key => key, ct).ConfigureAwait(false);
        var readableResources = await VisibleChildren(state, actor, tenant, id, "resources.read", manifest, r => r.Key, ct).ConfigureAwait(false);
        var allowedSkills = readableSkills.Items.ToHashSet(StringComparer.Ordinal);
        var skillView = EffectiveSkillCatalogResolver.Resolve(effective, snapshot.Skills).Where(s => allowedSkills.Contains(s.Key)).ToArray();
        var visible = effective with { Skills = effective.SkillList.Where(s => allowedSkills.Contains("definition:" + s.Id)).ToArray(),
            Environment = readableResources.All ? effective.Environment : RoleEnvironments.Of(effective) with { KnowledgeSources = [] } };
        var overview = AdminEffectiveConfigurationResolver.Resolve(snapshot.Agent.Instance, baseline, models, tools,
            Guid.TryParse(baseline.Id, out _) ? "TenantPublication" : "BuiltIn", status);
        if (!readableResources.All) overview = overview with { KnowledgeSources = [] };
        return new(snapshot.Agent, visible, overview, skillView, readableResources.Items, state.Tenant.Revision,
            new(readableSkills.All || readableSkills.Items.Length > 0, readableResources.All || readableResources.Items.Length > 0, readableSkills.All, readableResources.All));

    }

    public ValueTask<TenantAgent> UpdatePersonaAsync(Guid actor, Guid tenant, Guid id, AgentIdentity persona,
        long securityRevision, long instanceRevision, long personaRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        return await Commit(actor, tenant, state, new(id, instanceRevision, persona, personaRevision), "agents.persona.update", token, initiator).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgent> AdoptVersionAsync(Guid actor, Guid tenant, Guid id, int version,
        long securityRevision, long instanceRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false); var current = snapshot.Agent.Instance;
        if (current.Revision != instanceRevision) throw new SecurityConflictException("stale_revision");
        if (Guid.TryParse(current.DefinitionId, out var definitionId))
            await authorizer.RequireAsync(actor, "agent-definitions.read", AccessScope.ForResource(tenant, "definition", definitionId.ToString("D")), token).ConfigureAwait(false);
        var (target, metadataRevision, status) = await Definition(tenant, current.DefinitionId, version, token).ConfigureAwait(false);
        if (status != "Active") throw new AccessDeniedException("definition_unavailable");
        var effective = InstanceSettingsResolver.Resolve(target, current.SettingsOverrides);
        _ = SessionModelBinder.PinDefault(models, effective);
        var skills = new InstanceSkillSnapshot(snapshot.Skills.DefinitionStates.Concat(target.SkillList
            .Where(s => !snapshot.Skills.DefinitionStates.Any(old => old.DefinitionSkillId == s.Id))
            .Select(s => new AgentDefinitionSkillState(id, s.Id, null, 1, time.GetUtcNow()))).ToArray(), snapshot.Skills.InstanceSkills);
        _ = EffectiveSkillCatalogResolver.Resolve(effective, skills);
        _ = AgentInstanceResourceService.Resolve(effective, await Resources(tenant, target, token).ConfigureAwait(false), snapshot.Resources, skills);
        var policy = await automationPolicy.PlanAsync(current, effective, time.GetUtcNow(), token).ConfigureAwait(false);
        var updated = await Commit(actor, tenant, state, new(id, instanceRevision, ActiveVersion: version,
            ExpectedPublicationRevision: metadataRevision, DefinitionSkills: target.SkillList, AutomationPolicyChanges: policy), "agents.version.adopt", token, initiator).ConfigureAwait(false);
        if (sandbox is not null) await sandbox.EnforceAsync(id, token).ConfigureAwait(false);
        return updated;
    }, ct);

    private async ValueTask<TenantAgent> Commit(Guid actor, Guid tenant, TenantSecurityState state, TenantAgentConfigurationChange change, string action, CancellationToken ct, HumanSessionReference? initiator)
    {
        await RequireOriginalHuman(actor, tenant, initiator, ct).ConfigureAwait(false);
        change = change with { Initiator = initiator };
        var now = time.GetUtcNow(); var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        if (state.Assignments.Any(a => TenantAgentChildScopes.BelongsTo(a.Scope, change.InstanceId)))
        {
            var snapshot = TenantAgentChildScopes.After(await Snapshot(tenant, change.InstanceId, ct).ConfigureAwait(false), change);
            var (definition, _, _) = await Definition(tenant, snapshot.Agent.Instance.DefinitionId, snapshot.Agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
            next = TenantAgentChildScopes.RetainAvailable(next, change.InstanceId,
                TenantAgentChildScopes.Catalog(tenant, snapshot, definition, await Resources(tenant, definition, ct).ConfigureAwait(false)));
        }
        return await agents.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, change, now,
            new(ids.NewId(), tenant, actor, action, change.InstanceId.ToString("D"), "allowed", "authorized", now), ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask RequireOriginalHuman(Guid actor, Guid tenant, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return; // Trusted non-HTTP callers retain their existing actor authority checks.
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        await TenantHumanSessionAuthority.RequireAsync(security, identity ?? throw new InvalidOperationException("Human-bound mutations require identity services."), tenant, initiator, ct).ConfigureAwait(false);
    }

    private async ValueTask<TenantSecurityState> ChangeState(Guid actor, Guid tenant, Guid id, long revision, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var key in new[] { "agents.read", "agents.update" })
        {
            var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), Scope(tenant, id), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (state.Tenant.Revision != revision) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private async ValueTask<(AgentDefinition Definition, long? MetadataRevision, string Status)> Definition(Guid tenant, string id, int version, CancellationToken ct)
    {
        if (Guid.TryParse(id, out var owned))
        {
            var publication = await definitions.FindDefinitionPublicationAsync(tenant, owned, version, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("definition_unavailable");
            return (publication.Payload, publication.MetadataRevision, publication.Status.ToString());
        }
        var shipped = await builtIns.GetAsync(id, version, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("definition_unavailable");
        return (shipped, null, "Active");
    }
    private async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> Resources(Guid tenant, AgentDefinition definition, CancellationToken ct) =>
        Guid.TryParse(definition.Id, out var id) ? await definitions.ListOwnedPublicationResourcesAsync(tenant, id, definition.Version, ct).ConfigureAwait(false)
        : await inheritedResources.ListAsync(definition, ct).ConfigureAwait(false);
    private async ValueTask<TenantAgentConfigurationSnapshot> Snapshot(Guid tenant, Guid id, CancellationToken ct) =>
        await agents.ReadTenantAgentConfigurationAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private ValueTask<TenantAgent> WithGate(Guid id, Func<CancellationToken, ValueTask<TenantAgent>> action, CancellationToken ct) =>
        lifecycleGate is null ? action(ct) : lifecycleGate.WithInstanceAsync(id, action, ct);
    private static AccessScope Scope(Guid tenant, Guid id) => AccessScope.ForResource(tenant, "agent", id.ToString("D"));
}
