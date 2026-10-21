using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentEditorState(TenantAgent Agent, IReadOnlyList<InstanceSettingsSection> Settings,
    long SecurityRevision, bool CanUpdate, IReadOnlyList<string> UnavailableSettings);
public sealed record TenantAgentControlsState(TenantAgent Agent, TenantAgentBudgets Budgets, TenantAgentManagement Management,
    long SecurityRevision, bool CanUpdate);

// Current original-human boundary over the existing owning services, including post-I/O read fences.
public sealed class TenantAgentReadService(IIamStore security, ITenantAgentStore agents, TenantAgentConfigurationService configuration,
    IdentityService identity, IamAuthorizer authorizer, TenantAgentService directory)
{
    public ValueTask<TenantAgent> ReadAgentAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, agent, _) => ValueTask.FromResult(agent!), ct);

    public ValueTask<TenantAgentPage> ListAsync(Guid tenant, TenantSessionInitiator human, int page = 0, int pageSize = 30, CancellationToken ct = default) =>
        Fenced(tenant, null, human, (_, _, token) => directory.ListAsync(human.PrincipalId, tenant, page, pageSize, token), ct);

    public ValueTask<IReadOnlyList<AgentDefinition>> ListTemplatesAsync(Guid tenant, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, null, human, (_, _, token) => directory.ListTemplatesAsync(human.PrincipalId, tenant, token), ct);

    public ValueTask<TenantAgentEffectiveConfiguration> ReadConfigurationAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentSettings> ReadSettingsAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadSettingsAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentIsolationState> ReadIsolationAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadIsolationAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentIsolationPlan> PreviewIsolationAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        IsolationDraft? draft, string? preset = null, CancellationToken ct = default) =>
        Fenced<TenantAgentIsolationPlan>(tenant, instance, human, async (state, agent, token) =>
        {
            var result = await configuration.PreviewIsolationAsync(human.PrincipalId, tenant, instance, draft, preset, token).ConfigureAwait(false);
            if (result.SecurityRevision != state.Tenant.Revision || result.Plan.Expected.Instance != agent!.Instance.Revision)
                throw new SecurityConflictException("stale_revision");
            return result;
        }, ct);

    public ValueTask<TenantAgentSandboxSetup> ReadSandboxAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadSandboxAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentBudgets> ReadBudgetsAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadBudgetsAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentManagement> ReadManagementAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadManagementAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentSkillPage> ReadSkillsAsync(Guid tenant, Guid instance, TenantSessionInitiator human, int page = 0, int pageSize = 30, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadSkillsAsync(human.PrincipalId, tenant, instance, page, pageSize, token), ct);

    public ValueTask<TenantAgentResources> ReadResourcesAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadResourcesAsync(human.PrincipalId, tenant, instance, token), ct);

    public ValueTask<TenantAgentSkillEditor> ReadSkillEditorAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int page = 0, int pageSize = 30, string? search = null, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadSkillEditorAsync(human.PrincipalId, tenant, instance, page, pageSize, search, token), ct);

    public ValueTask<TenantAgentResourceEditor> ReadResourceEditorAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int page = 0, int pageSize = 30, string? search = null, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadResourceEditorAsync(human.PrincipalId, tenant, instance, page, pageSize, search, token), ct);

    public ValueTask<TenantAgentSkillDetail> ReadSkillDetailAsync(Guid tenant, Guid instance, TenantSessionInitiator human, string key, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadSkillDetailAsync(human.PrincipalId, tenant, instance, key, token), ct);

    public ValueTask<TenantAgentResourceDetail> ReadResourceDetailAsync(Guid tenant, Guid instance, TenantSessionInitiator human, string key, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadResourceDetailAsync(human.PrincipalId, tenant, instance, key, token), ct);

    public ValueTask<TenantAgentResourceContent> ReadResourceContentAsync(Guid tenant, Guid instance, TenantSessionInitiator human, string key, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadResourceContentAsync(human.PrincipalId, tenant, instance, key, token), ct);

    // Parsing/render preparation can outlive a logout or revocation, just like byte I/O. Keep both
    // inside the same original-human/Instance/security fence; projection never authorizes its source.
    public ValueTask<T> ProjectResourceContentAsync<T>(Guid tenant, Guid instance, TenantSessionInitiator human, string key,
        Func<InstanceResourceView, int> maximumBytes, Func<TenantAgentResourceContent, CancellationToken, ValueTask<T>> project, CancellationToken ct = default) =>
        Fenced<T>(tenant, instance, human, async (_, _, token) =>
        {
            var source = await configuration.ReadResourceContentAsync(human.PrincipalId, tenant, instance, key, token, maximumBytes).ConfigureAwait(false);
            return await project(source, token).ConfigureAwait(false);
        }, ct);

    public ValueTask<TenantAgentVersionCatalog> ReadVersionsAsync(Guid tenant, Guid instance, TenantSessionInitiator human, int page = 0, int pageSize = 30, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.ReadVersionsAsync(human.PrincipalId, tenant, instance, page, pageSize, token), ct);

    public ValueTask<TenantAgentVersionPreview> PreviewVersionAsync(Guid tenant, Guid instance, TenantSessionInitiator human, int version, CancellationToken ct = default) =>
        Fenced(tenant, instance, human, (_, _, token) => configuration.PreviewVersionAsync(human.PrincipalId, tenant, instance, version, token), ct);

    public ValueTask<TenantAgentControlsState> ReadControlsAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced<TenantAgentControlsState>(tenant, instance, human, async (state, agent, token) =>
        {
            var budgets = await configuration.ReadBudgetsAsync(human.PrincipalId, tenant, instance, token).ConfigureAwait(false);
            var management = await configuration.ReadManagementAsync(human.PrincipalId, tenant, instance, token).ConfigureAwait(false);
            if (budgets.SecurityRevision != state.Tenant.Revision || management.SecurityRevision != state.Tenant.Revision
                || budgets.InstanceRevision != agent!.Instance.Revision || management.Policy.InstanceRevision != agent.Instance.Revision)
                throw new SecurityConflictException("stale_revision");
            var canUpdate = (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agents.update"), Scope(tenant, instance), token).ConfigureAwait(false)).Allowed;
            return new(agent, budgets, management, state.Tenant.Revision, canUpdate);
        }, ct);

    public ValueTask<TenantAgentEditorState> ReadEditorAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default) =>
        Fenced<TenantAgentEditorState>(tenant, instance, human, async (state, agent, token) =>
        {
            var settings = await configuration.ReadSettingsAsync(human.PrincipalId, tenant, instance, token).ConfigureAwait(false);
            var canUpdate = (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agents.update"), Scope(tenant, instance), token).ConfigureAwait(false)).Allowed;
            if (settings.SecurityRevision != state.Tenant.Revision || settings.Items.Any(item => item.InstanceRevision != agent!.Instance.Revision))
                throw new SecurityConflictException("stale_revision");
            // Dedicated editors own protected Sandbox setup and all coordinated State/Memory/Voice enablement fields.
            var independent = settings.Items.Where(item => item.Section is not ("sandboxPolicy" or "stateIsolationPolicy" or "memoryPolicy"))
                .Select(item => item.Section != "voice" ? item : item with {
                    Overrides = item.Overrides.Where(p => p.Key != "enabled").ToDictionary(),
                    Effective = item.Effective.Where(p => p.Key != "enabled").ToDictionary(),
                    Sources = item.Sources.Where(p => p.Key != "enabled").ToDictionary(),
                    DefinitionDefaults = item.DefinitionDefaults.Where(p => p.Key != "enabled").ToDictionary(),
                    Constraints = item.Constraints.Where(p => p.Key != "enabled").ToDictionary() }).ToArray();
            return new(agent!, independent, state.Tenant.Revision, canUpdate, ["sandboxPolicy", "stateIsolationPolicy", "memoryPolicy", "voice.enabled"]);
        }, ct);

    private async ValueTask<T> Fenced<T>(Guid tenant, Guid? instance, TenantSessionInitiator human,
        Func<TenantSecurityState, TenantAgent?, CancellationToken, ValueTask<T>> read, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(state);
        var agent = instance is { } id ? await agents.FindTenantAgentAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable") : null;
        var result = await read(state, agent, ct).ConfigureAwait(false);
        var currentAgent = instance is { } currentId ? await agents.FindTenantAgentAsync(tenant, currentId, ct).ConfigureAwait(false) : null;
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(current);
        if (state.Tenant.Revision != current.Tenant.Revision || agent is not null
            && (currentAgent?.Instance.Revision != agent.Instance.Revision || currentAgent.PrincipalId != agent.PrincipalId))
            throw new SecurityConflictException("stale_revision");
        return result;

        async ValueTask RequireRead(TenantSecurityState value)
        {
            if (instance is not { } target) return; // Catalog services retain their own create/list permission rules.
            var decision = await authorizer.CheckTenantAsync(value, human.PrincipalId, PermissionCatalog.Get("agents.read"), Scope(tenant, target), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }
    private static AccessScope Scope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
}
