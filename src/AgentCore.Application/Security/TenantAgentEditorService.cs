using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentEditorState(TenantAgent Agent, IReadOnlyList<InstanceSettingsSection> Settings,
    long SecurityRevision, bool CanUpdate, IReadOnlyList<string> UnavailableSettings);

// One read witness for the existing Profile and Settings owners. Configuration is never IAM authority.
public sealed class TenantAgentEditorService(IIamStore security, ITenantAgentStore agents, TenantAgentConfigurationService configuration,
    IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask<TenantAgentEditorState> ReadAsync(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var scope = AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
        await RequireRead(state);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var settings = await configuration.ReadSettingsAsync(human.PrincipalId, tenant, instance, ct).ConfigureAwait(false);
        var canUpdate = (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agents.update"), scope, ct).ConfigureAwait(false)).Allowed;
        var currentAgent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(current);
        if (state.Tenant.Revision != current.Tenant.Revision || settings.SecurityRevision != state.Tenant.Revision
            || settings.Items.Any(item => item.InstanceRevision != agent.Instance.Revision)
            || currentAgent?.Instance.Revision != agent.Instance.Revision || currentAgent.PrincipalId != agent.PrincipalId)
            throw new SecurityConflictException("stale_revision");
        // Its tenant profile catalog/effects have not been cut over. Never use a global catalog fallback.
        return new(agent, settings.Items.Where(item => item.Section != "sandboxPolicy").ToArray(), state.Tenant.Revision, canUpdate, ["sandboxPolicy"]);

        async ValueTask RequireRead(TenantSecurityState value)
        {
            var decision = await authorizer.CheckTenantAsync(value, human.PrincipalId, PermissionCatalog.Get("agents.read"), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }
}
