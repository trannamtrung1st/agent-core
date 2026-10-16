using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentSkillPage(IReadOnlyList<InstanceSkillView> Items, int Total, int Page, int PageSize, long InstanceRevision, long SecurityRevision);
public sealed record TenantAgentSkillSaved(InstanceSkillView Skill, DefinitionSkillMutationState? DefinitionSkill, long InstanceRevision, long SecurityRevision, bool Changed);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentSkillPage> ReadSkillsAsync(Guid actor, Guid tenant, Guid id, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        await ChildReadState(actor, tenant, id, "skills.read", ct).ConfigureAwait(false);
        var (snapshot, baseline) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var definition = EffectiveChildDefinition(snapshot, baseline);
        var policy = ManagementPolicyResolver.Resolve(definition, snapshot.Agent.Instance);
        var items = AgentInstanceSkillService.Views(definition, snapshot.Skills).Concat(AgentInstanceSkillService.BuiltIns(definition, policy, snapshot.Agent.Instance))
            .Select(s => s with { InstanceRevision = snapshot.Agent.Instance.Revision }).ToArray();
        var state = await ChildReadState(actor, tenant, id, "skills.read", ct).ConfigureAwait(false);
        page = Math.Clamp(page, 0, 100_000); pageSize = Math.Clamp(pageSize, 1, 100);
        return new(items.Skip(page * pageSize).Take(pageSize).ToArray(), items.Length, page, pageSize, snapshot.Agent.Instance.Revision, state.Tenant.Revision);
    }

    public ValueTask<TenantAgentSkillSaved> WriteSkillAsync(Guid actor, Guid tenant, Guid id, string operation,
        long securityRevision, long instanceRevision, string? key = null, long? expectedSkillRevision = null,
        InstanceSkillInput? input = null, bool? enabled = null, CancellationToken ct = default) => WithChildGate<TenantAgentSkillSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, "skills.manage", "skills.read", securityRevision, token).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false); var owner = snapshot.Agent.Instance;
        RequireWritable(owner, instanceRevision);
        var (baseline, _, _) = await Definition(tenant, owner.DefinitionId, owner.ActiveVersion, token).ConfigureAwait(false);
        var definition = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, owner) };
        var agent = state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity;
        if (agent && !definition.SelfManagement!.Allows(HarnessManagementScope.Skills)) throw AgentCoreErrors.Forbidden("Skill administration is outside the effective self-management grant.");
        if (input is not null) { ArgumentNullException.ThrowIfNull(input.Procedure); DefinitionResourcePolicies.RejectSecretTokens(input.Procedure); }
        if (key?.StartsWith("builtin:", StringComparison.Ordinal) == true)
        {
            if (operation is not ("set_enabled" or "reset_enabled")) throw AgentCoreErrors.Forbidden("Built-in procedures are read-only.");
            var existing = AgentInstanceSkillService.BuiltIns(definition, definition.SelfManagement!, owner).SingleOrDefault(s => s.Key == key)
                ?? throw AgentCoreErrors.NotFound("Skill was not found.");
            if (existing.Revision != expectedSkillRevision) throw new SecurityConflictException("stale_revision");
            var overrides = owner.HarnessManagement is { } stored ? stored.Overrides ?? ManagementPolicyOverrides.FromLegacy(definition.SelfManagement!) : new();
            bool? choice = operation == "reset_enabled" ? null : enabled ?? throw AgentCoreErrors.Validation("Enabled is required.");
            overrides = key switch {
                ManagementBuiltInSkills.ConfigurationKey => overrides with { ConfigurationAssistance = choice is null ? null : new(choice.Value) },
                ManagementBuiltInSkills.HarnessKey => overrides with { HarnessAuthoring = choice is null ? null : new(choice.Value) },
                _ => throw AgentCoreErrors.Validation("Unknown Built-in Skill.") };
            // The existing Skills-area check above authorizes only these two choices; it cannot enable or unfreeze policy.
            var policy = SelfManagementService.Prepare(owner, baseline, overrides, null, false);
            var updated = await Commit(actor, tenant, state, new(id, instanceRevision, HarnessManagement: policy), "skills.choice.update", token).ConfigureAwait(false);
            var written = AgentInstanceSkillService.BuiltIns(definition, ManagementPolicyResolver.Resolve(baseline, updated.Instance), updated.Instance).Single(s => s.Key == key);
            return new(written, null, updated.Instance.Revision, state.Tenant.Revision + 1, true);
        }
        var prepared = AgentInstanceSkillService.Prepare(owner, definition, snapshot.Skills, operation, key, expectedSkillRevision, input, enabled,
            agent ? SkillAuthor.Agent : SkillAuthor.Admin, time.GetUtcNow());
        if (prepared.Mutation is null) return new(prepared.Skill, prepared.DefinitionSkill, owner.Revision, state.Tenant.Revision, false);
        _ = EffectiveSkillCatalogResolver.Resolve(definition, prepared.Next);
        _ = AgentInstanceResourceService.Resolve(definition, await Resources(tenant, baseline, token).ConfigureAwait(false), snapshot.Resources, prepared.Next);
        var saved = await Commit(actor, tenant, state, new(id, instanceRevision, Skill: prepared.Mutation), "skills." + operation, token).ConfigureAwait(false);
        return new(prepared.Skill with { InstanceRevision = saved.Instance.Revision }, prepared.DefinitionSkill, saved.Instance.Revision, state.Tenant.Revision + 1, true);
    }, ct);

    private async ValueTask<TenantSecurityState> ChildReadState(Guid actor, Guid tenant, Guid id, string permission, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var (key, scope) in new[] { ("agents.read", Scope(tenant, id)), (permission, AccessScope.ForTenant(tenant)) })
        {
            var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return state;
    }
    private async ValueTask<TenantSecurityState> ChildChangeState(Guid actor, Guid tenant, Guid id, string permission, string read, long expected, CancellationToken ct)
    {
        var state = await ChildReadState(actor, tenant, id, read, ct).ConfigureAwait(false);
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private ValueTask<T> WithChildGate<T>(Guid id, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct) =>
        lifecycleGate is null ? action(ct) : lifecycleGate.WithInstanceAsync(id, action, ct);
}
