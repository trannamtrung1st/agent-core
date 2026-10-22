using AgentCore.Domain.Triggers;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Models;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentVersion(int Version, string Status, long? MetadataRevision);
public sealed record TenantAgentVersionCatalog(TenantAgent Agent, IReadOnlyList<TenantAgentVersion> Items, int Total, int Page, int PageSize,
    long SecurityRevision, bool CanAdopt);
public sealed record TenantAgentVersionPreview(InstanceVersionPreview Preview, long SecurityRevision, long? TargetPublicationRevision,
    TenantAgentChildAccess ChildAccess, bool CanAdopt);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentVersionCatalog> ReadVersionsAsync(Guid actor, Guid tenant, Guid id, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid version page.");
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        await RequireVersionSourceRead(actor, tenant, snapshot.Agent.Instance, ct).ConfigureAwait(false);
        var source = snapshot.Agent.Instance.DefinitionId;
        var items = Guid.TryParse(source, out var definitionId)
            ? (await definitions.ListDefinitionPublicationsAsync(tenant, definitionId, ct).ConfigureAwait(false)).Select(p => new TenantAgentVersion(p.Version, p.Status.ToString(), p.MetadataRevision)).ToArray()
            : (await builtIns.ListAsync(ct).ConfigureAwait(false)).Where(p => p.Id == source).Select(p => new TenantAgentVersion(p.Version, "Active", null)).ToArray();
        var canAdopt = await CanAdopt(state, actor, snapshot.Agent.Instance, ct).ConfigureAwait(false);
        return new(snapshot.Agent, items.OrderByDescending(p => p.Version).Skip(page * pageSize).Take(pageSize).ToArray(), items.Length, page, pageSize, state.Tenant.Revision, canAdopt);
    }

    public async ValueTask<TenantAgentVersionPreview> PreviewVersionAsync(Guid actor, Guid tenant, Guid id, int version, CancellationToken ct = default)
    {
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        PreparedVersion prepared;
        try { prepared = await PrepareVersionAsync(actor, tenant, snapshot, version, ct).ConfigureAwait(false); }
        // Compatibility failures may refer to unreadable local children. Never expose their names through a preview error.
        catch (AgentCoreException error) when (error.StatusCode == 400) { throw new ArgumentException("The target version is incompatible with the current Instance configuration."); }
        catch (ArgumentException) { throw new ArgumentException("The target version is incompatible with the current Instance configuration."); }
        var (baseline, _, _) = await Definition(tenant, snapshot.Agent.Instance.DefinitionId, snapshot.Agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        var oldResources = await Resources(tenant, baseline, ct).ConfigureAwait(false);
        var newResources = await Resources(tenant, prepared.Target, ct).ConfigureAwait(false);
        var skillKeys = baseline.SkillList.Concat(prepared.Target.SkillList).Select(s => "definition:" + s.Id).Distinct(StringComparer.Ordinal).ToArray();
        var resourceKeys = oldResources.Concat(newResources).Select(r => "definition:" + r.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        var visibleSkills = await VisibleChildren(state, actor, tenant, id, "skills.read", skillKeys, key => key, ct).ConfigureAwait(false);
        var visibleResources = await VisibleChildren(state, actor, tenant, id, "resources.read", resourceKeys, key => key, ct).ConfigureAwait(false);
        var skills = visibleSkills.Items.ToHashSet(StringComparer.Ordinal); var resources = visibleResources.Items.ToHashSet(StringComparer.Ordinal);
        var preview = InstanceVersionPreviewFactory.Create(snapshot.Agent.Instance,
            baseline with { Skills = baseline.SkillList.Where(s => skills.Contains("definition:" + s.Id)).ToArray() },
            prepared.Target with { Skills = prepared.Target.SkillList.Where(s => skills.Contains("definition:" + s.Id)).ToArray() },
            oldResources.Where(r => resources.Contains("definition:" + r.ResourceId)).ToArray(), newResources.Where(r => resources.Contains("definition:" + r.ResourceId)).ToArray());
        return new(preview, state.Tenant.Revision, prepared.MetadataRevision,
            new(visibleSkills.All || skills.Count > 0, visibleResources.All || resources.Count > 0, visibleSkills.All, visibleResources.All),
            await CanAdopt(state, actor, snapshot.Agent.Instance, ct).ConfigureAwait(false));
    }

    private async ValueTask<bool> CanAdopt(TenantSecurityState state, Guid actor, AgentInstance instance, CancellationToken ct) =>
        instance.Lifecycle == AgentInstanceLifecycle.Active && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agents.update"), Scope(state.Tenant.TenantId, instance.InstanceId), ct).ConfigureAwait(false)).Allowed;

    private async ValueTask RequireVersionSourceRead(Guid actor, Guid tenant, AgentInstance instance, CancellationToken ct)
    {
        if (Guid.TryParse(instance.DefinitionId, out var definitionId))
            await authorizer.RequireAsync(actor, "agent-definitions.read", AccessScope.ForResource(tenant, "definition", definitionId.ToString("D")), ct).ConfigureAwait(false);
    }

    private sealed record PreparedVersion(AgentDefinition Target, long? MetadataRevision, IReadOnlyList<InstanceAutomationPolicyChange> AutomationChanges);
    private async ValueTask<PreparedVersion> PrepareVersionAsync(Guid actor, Guid tenant, TenantAgentConfigurationSnapshot snapshot, int version, CancellationToken ct)
    {
        if (version < 1) throw new ArgumentException("An exact positive version is required.");
        var current = snapshot.Agent.Instance;
        await RequireVersionSourceRead(actor, tenant, current, ct).ConfigureAwait(false);
        var (target, metadataRevision, status) = await Definition(tenant, current.DefinitionId, version, ct).ConfigureAwait(false);
        if (status != "Active") throw new AccessDeniedException("definition_unavailable");
        if (current.Lifecycle != AgentInstanceLifecycle.Active) throw new ArgumentException("Archived Instances are read-only.");
        var effective = InstanceSettingsResolver.Resolve(target, current.SettingsOverrides);
        _ = SessionModelBinder.PinDefault(models, effective);
        var skills = new InstanceSkillSnapshot(snapshot.Skills.DefinitionStates.Concat(target.SkillList
            .Where(s => !snapshot.Skills.DefinitionStates.Any(old => old.DefinitionSkillId == s.Id))
            .Select(s => new AgentDefinitionSkillState(current.InstanceId, s.Id, null, 1, time.GetUtcNow()))).ToArray(), snapshot.Skills.InstanceSkills);
        _ = EffectiveSkillCatalogResolver.Resolve(effective, skills);
        _ = AgentInstanceResourceService.Resolve(effective, await Resources(tenant, target, ct).ConfigureAwait(false), snapshot.Resources, skills);
        var policy = await automationPolicy.PlanTenantAsync(tenant, security as ITenantAutomationStore ?? throw new InvalidOperationException("Canonical Automation store is unavailable."), current, effective, time.GetUtcNow(), ct).ConfigureAwait(false);
        return new(target, metadataRevision, policy);
    }
}
