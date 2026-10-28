using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantAgentIsolationState(TenantAgent Agent, IsolationConfigurationView Configuration, long SecurityRevision, bool CanUpdate);
public sealed record TenantAgentIsolationPlan(IsolationChangePlan Plan, long SecurityRevision);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentIsolationState> ReadIsolationAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var revision = await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false);
        var canUpdate = (await authorizer.CheckAsync(actor, "agents.update", Scope(tenant, id), ct).ConfigureAwait(false)).Allowed;
        var view = IsolationView(snapshot, definition);
        var current = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != snapshot.Agent.Instance.Revision || current.Isolation != snapshot.Isolation)
            throw new SecurityConflictException("stale_revision");
        return new(snapshot.Agent, view, revision, canUpdate);
    }

    public async ValueTask<TenantAgentIsolationPlan> PreviewIsolationAsync(Guid actor, Guid tenant, Guid id, IsolationDraft? draft,
        string? preset = null, CancellationToken ct = default)
    {
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var view = IsolationView(snapshot, definition);
        draft = preset is null ? draft ?? throw AgentCoreErrors.Validation("A coordinated draft is required.") : IsolationConfigurationPlanner.Preset(view, preset);
        var plan = await IsolationConfigurationPlanner.PreviewAsync(id, view, snapshot.Agent.Instance, definition, draft,
            browser ?? throw new InvalidOperationException("Browser policy is unavailable."), time, ct, observeBrowser: false).ConfigureAwait(false);
        var revision = await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false);
        var current = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != snapshot.Agent.Instance.Revision || current.Isolation != snapshot.Isolation)
            throw new SecurityConflictException("stale_revision");
        return new(plan, revision);
    }

    public ValueTask<TenantAgent> ApplyIsolationAsync(Guid actor, Guid tenant, Guid id, long securityRevision,
        IsolationOwnerRevisions expected, IsolationDraft draft, string fingerprint, HumanSessionReference initiator,
        CancellationToken ct = default) => WithGate(id, async token =>
    {
        await RequireOriginalHuman(actor, tenant, initiator, token).ConfigureAwait(false);
        var state = await ChangeState(actor, tenant, id, securityRevision, token).ConfigureAwait(false);
        var preview = await PreviewIsolationAsync(actor, tenant, id, draft, ct: token).ConfigureAwait(false);
        if (preview.SecurityRevision != securityRevision || preview.Plan.Expected != expected || preview.Plan.Fingerprint != fingerprint)
            throw new SecurityConflictException("stale_revision");
        if (preview.Plan.BlockingReasons.Count > 0) throw AgentCoreErrors.Validation(string.Join(" ", preview.Plan.BlockingReasons));
        var snapshot = await Snapshot(tenant, id, token).ConfigureAwait(false);
        RequireWritable(snapshot.Agent.Instance, expected.Instance);
        if (snapshot.Isolation.ExperienceRevision != expected.Experience || snapshot.Isolation.MaintenanceRevision != expected.Maintenance)
            throw new SecurityConflictException("stale_revision");
        if (preview.Plan.Changes.Count == 0) return snapshot.Agent;
        var (definition, _, _) = await Definition(tenant, snapshot.Agent.Instance.DefinitionId, snapshot.Agent.Instance.ActiveVersion, token).ConfigureAwait(false);
        var next = IsolationConfigurationPlanner.Patch(snapshot.Agent.Instance.SettingsOverrides ?? new(), draft);
        var effective = InstanceSettingsResolver.Resolve(definition, next, snapshot.Agent.ManagedKind is not null);
        _ = EffectiveSkillCatalogResolver.Resolve(effective, snapshot.Skills);
        _ = InstanceResourceRules.Resolve(effective, await Resources(tenant, definition, token).ConfigureAwait(false), snapshot.Resources, snapshot.Skills);
        var automations = await automationPolicy.PlanTenantAsync(tenant, security as ITenantAutomationStore
            ?? throw new InvalidOperationException("Canonical Automation store is unavailable."), snapshot.Agent.Instance, effective, time.GetUtcNow(), token).ConfigureAwait(false);
        var updated = await Commit(actor, tenant, state, new(id, expected.Instance, SetSettingsOverrides: true, SettingsOverrides: next,
            AutomationPolicyChanges: automations, Isolation: new(expected, draft.ExperienceEnabled, draft.AllowMaintenance)),
            "agents.state-isolation.update", token, initiator).ConfigureAwait(false);
        if (sandbox is not null) await sandbox.EnforceAsync(id, token).ConfigureAwait(false);
        return updated;
    }, ct);

    private IsolationConfigurationView IsolationView(TenantAgentConfigurationSnapshot snapshot, AgentDefinition definition)
    {
        var current = snapshot.Agent.Instance; var metadata = snapshot.Isolation;
        return new(new(current.Revision, metadata.ExperienceRevision, metadata.MaintenanceRevision),
            new[] { "stateIsolationPolicy", "memoryPolicy", "voice" }.Select(section => InstanceSettingsRules.WithHostConstraints(
                InstanceSettingsRules.View(current, definition, section), browser)).ToArray(),
            metadata.ExperienceEnabled, metadata.AllowMaintenance, browser?.HostPolicy.ProfileMode.ToString() ?? "Unavailable", browser?.HostPolicy.Enabled ?? false, "NotObserved");
    }
}
