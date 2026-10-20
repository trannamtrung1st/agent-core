using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentSettings(IReadOnlyList<InstanceSettingsSection> Items, long SecurityRevision);
public sealed record TenantAgentManagement(ManagementPolicyView Policy, long SecurityRevision);
public sealed record TenantAgentBudgets(long InstanceRevision, long SecurityRevision, ExecutionBudgetPolicy? Overrides,
    ExecutionBudgetPolicy? DefinitionDefaults, IReadOnlyDictionary<ExecutionBudgetClass, EffectiveExecutionBudget> Effective, ExecutionBudgetCeilings HostLimits);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentSettings> ReadSettingsAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var sections = InstanceSettingsResolver.Sections.Select(section => AgentInstanceSettingsService.WithHostConstraints(
            AgentInstanceSettingsService.View(snapshot.Agent.Instance, definition, section), browser)).ToArray();
        return new(sections, await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false));
    }

    public ValueTask<TenantAgent> PatchSettingsAsync(Guid actor, Guid tenant, Guid id, string section,
        long securityRevision, long instanceRevision, IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        if (section == "sandboxPolicy")
            foreach (var key in new[] { "sandbox.read", "sandbox.manage" })
                await authorizer.RequireAsync(actor, key, Scope(tenant, id), token).ConfigureAwait(false);
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(set); ArgumentNullException.ThrowIfNull(clear);
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false); var current = snapshot.Agent.Instance;
        RequireWritable(current, instanceRevision);
        var (definition, _, _) = await Definition(tenant, current.DefinitionId, current.ActiveVersion, token).ConfigureAwait(false);
        if (section == "sandboxPolicy") definition = await PrepareSandboxPatch(actor, tenant, current, definition, set, clear, token).ConfigureAwait(false);
        if (aliases is null) throw AgentCoreErrors.Validation("Configured provider choices are unavailable.");
        var (next, effective) = AgentInstanceSettingsService.ResolvePatch(definition, current.SettingsOverrides ?? new(), section, set, clear, models, aliases);
        AgentInstanceSettingsService.ValidateBrowser(effective, browser);
        _ = EffectiveSkillCatalogResolver.Resolve(effective, snapshot.Skills);
        _ = AgentInstanceResourceService.Resolve(effective, await Resources(tenant, definition, token).ConfigureAwait(false), snapshot.Resources, snapshot.Skills);
        var plan = await automationPolicy.PlanTenantAsync(tenant, security as ITenantAutomationStore ?? throw new InvalidOperationException("Canonical Automation store is unavailable."), current, effective, time.GetUtcNow(), token).ConfigureAwait(false);
        var updated = await Commit(actor, tenant, state, new(id, instanceRevision, SetSettingsOverrides: true,
            SettingsOverrides: next, AutomationPolicyChanges: plan), "agents.settings.update", token, initiator).ConfigureAwait(false);
        if (sandbox is not null) await sandbox.EnforceAsync(id, token).ConfigureAwait(false);
        return updated;
    }, ct);

    public async ValueTask<TenantAgentBudgets> ReadBudgetsAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false); var current = snapshot.Agent.Instance;
        return new(current.Revision, await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false), current.ExecutionBudgets, definition.ExecutionBudgets,
            Enum.GetValues<ExecutionBudgetClass>().ToDictionary(kind => kind, kind => ExecutionBudgetPolicy.Resolve(kind, definition.ExecutionBudgets, current.ExecutionBudgets)), ExecutionBudgetCeilings.Default);
    }
    public ValueTask<TenantAgent> UpdateBudgetsAsync(Guid actor, Guid tenant, Guid id, long securityRevision,
        long instanceRevision, ExecutionBudgetPolicy? budgets, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        budgets?.Validate();
        return await Commit(actor, tenant, state, new(id, instanceRevision, SetExecutionBudgets: true, ExecutionBudgets: budgets), "agents.budgets.update", token, initiator).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgent> UpdateUnattendedModelAsync(Guid actor, Guid tenant, Guid id, long securityRevision,
        long instanceRevision, string? catalogKey, string? reasoningEffort, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        ExecutionModelPolicy.RequireSelectable(models, catalogKey, reasoningEffort);
        return await Commit(actor, tenant, state, new(id, instanceRevision, SetUnattendedModel: true,
            UnattendedModelCatalogKey: catalogKey, UnattendedReasoningEffort: reasoningEffort), "agents.unattended-model.update", token, initiator).ConfigureAwait(false);
    }, ct);

    public async ValueTask<TenantAgentManagement> ReadManagementAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        return new(SelfManagementService.View(snapshot.Agent.Instance, definition), await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false));
    }
    public ValueTask<TenantAgent> UpdateManagementAsync(Guid actor, Guid tenant, Guid id, long securityRevision, long instanceRevision,
        ManagementPolicyOverrides overrides, bool? frozen = null, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false); var current = snapshot.Agent.Instance;
        RequireWritable(current, instanceRevision);
        var (definition, _, _) = await Definition(tenant, current.DefinitionId, current.ActiveVersion, token).ConfigureAwait(false);
        var agent = state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity;
        var next = SelfManagementService.Prepare(current, definition, overrides, frozen, agent);
        return await Commit(actor, tenant, state, new(id, instanceRevision, HarnessManagement: next), "agents.self-management.update", token, initiator).ConfigureAwait(false);
    }, ct);

    private async ValueTask<(TenantAgentConfigurationSnapshot Snapshot, AgentDefinition Definition)> BoundForRead(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, "agents.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        var (definition, _, _) = await Definition(tenant, snapshot.Agent.Instance.DefinitionId, snapshot.Agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        return (snapshot, definition);
    }
    public ValueTask<TenantAgent> FreezeManagementAsync(Guid actor, Guid tenant, Guid id, long securityRevision, long instanceRevision,
        CancellationToken ct = default, HumanSessionReference? initiator = null) => WithGate(id, async token =>
    {
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false); var current = snapshot.Agent.Instance;
        RequireWritable(current, instanceRevision);
        var (definition, _, _) = await Definition(tenant, current.DefinitionId, current.ActiveVersion, token).ConfigureAwait(false);
        return await Commit(actor, tenant, state, new(id, instanceRevision, HarnessManagement: SelfManagementService.PrepareFreeze(current, definition)),
            "agents.self-management.freeze", token, initiator).ConfigureAwait(false);
    }, ct);
    private async ValueTask<long> ReadRevision(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agents.read"), Scope(tenant, id), ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        return state.Tenant.Revision;
    }
    private static void RequireWritable(AgentInstance instance, long revision)
    {
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived Instances are read-only.");
        if (instance.Revision != revision) throw new SecurityConflictException("stale_revision");
    }
}
