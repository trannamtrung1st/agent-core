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

public sealed record TenantAgentEffectiveConfiguration(TenantAgent Agent, AgentDefinition Definition,
    AdminEffectiveConfiguration Effective, IReadOnlyList<EffectiveSkill> Skills,
    IReadOnlyList<EffectiveAgentResource> Resources, long SecurityRevision);

public sealed partial class TenantAgentConfigurationService(IIamStore security, ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, InheritedDefinitionResourceCatalog inheritedResources, IamAuthorizer authorizer,
    IModelCatalog models, IToolConfigurationGate tools, InstanceAutomationPolicy automationPolicy,
    IIdGenerator ids, TimeProvider time, AdminLifecycleCoordinator? lifecycleGate = null, SandboxPolicyEnforcement? sandbox = null,
    ProviderAliasSet? aliases = null, IBrowser? browser = null)
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
        await authorizer.RequireAsync(actor, "agents.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        return new(snapshot.Agent, effective, AdminEffectiveConfigurationResolver.Resolve(snapshot.Agent.Instance, baseline, models, tools,
            Guid.TryParse(baseline.Id, out _) ? "TenantPublication" : "BuiltIn", status),
            EffectiveSkillCatalogResolver.Resolve(effective, snapshot.Skills), manifest, state.Tenant.Revision);
    }

    public ValueTask<TenantAgent> UpdatePersonaAsync(Guid actor, Guid tenant, Guid id, AgentIdentity persona,
        long securityRevision, long instanceRevision, long personaRevision, CancellationToken ct = default) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        return await Commit(actor, tenant, state, new(id, instanceRevision, persona, personaRevision), "agents.persona.update", token).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgent> AdoptVersionAsync(Guid actor, Guid tenant, Guid id, int version,
        long securityRevision, long instanceRevision, CancellationToken ct = default) => WithGate(id, async token =>
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
            ExpectedPublicationRevision: metadataRevision, DefinitionSkills: target.SkillList, AutomationPolicyChanges: policy), "agents.version.adopt", token).ConfigureAwait(false);
        if (sandbox is not null) await sandbox.EnforceAsync(id, token).ConfigureAwait(false);
        return updated;
    }, ct);

    private async ValueTask<TenantAgent> Commit(Guid actor, Guid tenant, TenantSecurityState state, TenantAgentConfigurationChange change, string action, CancellationToken ct)
    {
        var now = time.GetUtcNow(); var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        return await agents.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, change, now,
            new(ids.NewId(), tenant, actor, action, change.InstanceId.ToString("D"), "allowed", "authorized", now), ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
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
