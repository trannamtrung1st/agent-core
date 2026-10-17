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
        var catalog = await ReadSkillCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        page = Math.Clamp(page, 0, 100_000); pageSize = Math.Clamp(pageSize, 1, 100);
        return new(catalog.Items.Skip(page * pageSize).Take(pageSize).ToArray(), catalog.Items.Length, page, pageSize,
            catalog.Agent.Instance.Revision, catalog.State.Tenant.Revision);
    }

    private async ValueTask<ChildCatalog<InstanceSkillView>> ReadSkillCatalog(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var (snapshot, baseline) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var definition = EffectiveChildDefinition(snapshot, baseline);
        var policy = ManagementPolicyResolver.Resolve(definition, snapshot.Agent.Instance);
        var items = AgentInstanceSkillService.Views(definition, snapshot.Skills).Concat(AgentInstanceSkillService.BuiltIns(definition, policy, snapshot.Agent.Instance))
            .Select(s => s with { InstanceRevision = snapshot.Agent.Instance.Revision }).ToArray();
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var visible = await VisibleChildren(state, actor, tenant, id, "skills.read", items, s => s.Key, ct, true).ConfigureAwait(false);
        return new(snapshot.Agent, visible.Items, state, visible.All);
    }

    public ValueTask<TenantAgentSkillSaved> WriteSkillAsync(Guid actor, Guid tenant, Guid id, string operation,
        long securityRevision, long instanceRevision, string? key = null, long? expectedSkillRevision = null,
        InstanceSkillInput? input = null, bool? enabled = null, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithChildGate<TenantAgentSkillSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, "skills.manage", "skills.read", securityRevision, token,
            operation is "create" or "customize" ? null : key ?? throw new AccessDeniedException("resource_unavailable")).ConfigureAwait(false);
        if (operation == "customize")
            await RequireChildPermission(state, actor, "skills.read", ChildScope(tenant, id, "skills.read", key ?? throw new AccessDeniedException("resource_unavailable")), token).ConfigureAwait(false);
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
            var updated = await Commit(actor, tenant, state, new(id, instanceRevision, HarnessManagement: policy), "skills.choice.update", token, initiator).ConfigureAwait(false);
            var written = AgentInstanceSkillService.BuiltIns(definition, ManagementPolicyResolver.Resolve(baseline, updated.Instance), updated.Instance).Single(s => s.Key == key);
            return new(written, null, updated.Instance.Revision, state.Tenant.Revision + 1, true);
        }
        var prepared = AgentInstanceSkillService.Prepare(owner, definition, snapshot.Skills, operation, key, expectedSkillRevision, input, enabled,
            agent ? SkillAuthor.Agent : SkillAuthor.Admin, time.GetUtcNow());
        if (prepared.Mutation is null)
        {
            await RequireOriginalHuman(actor, tenant, initiator, token).ConfigureAwait(false);
            return new(prepared.Skill, prepared.DefinitionSkill, owner.Revision, state.Tenant.Revision, false);
        }
        _ = EffectiveSkillCatalogResolver.Resolve(definition, prepared.Next);
        _ = AgentInstanceResourceService.Resolve(definition, await Resources(tenant, baseline, token).ConfigureAwait(false), snapshot.Resources, prepared.Next);
        var saved = await Commit(actor, tenant, state, new(id, instanceRevision, Skill: prepared.Mutation), "skills." + operation, token, initiator).ConfigureAwait(false);
        return new(prepared.Skill with { InstanceRevision = saved.Instance.Revision }, prepared.DefinitionSkill, saved.Instance.Revision, state.Tenant.Revision + 1, true);
    }, ct);

    private ValueTask<T> WithChildGate<T>(Guid id, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct) =>
        lifecycleGate is null ? action(ct) : lifecycleGate.WithInstanceAsync(id, action, ct);
}
