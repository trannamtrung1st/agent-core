using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantChatAgent(Guid InstanceId, long Revision, string DefinitionId, int ActiveVersion,
    string Name, string Role, string Description, bool VoiceAvailable, string Language);
public sealed record TenantChatAgentPage(IReadOnlyList<TenantChatAgent> Items, int Total, int Page, int PageSize,
    long SecurityRevision, bool CanCreateSessions);

// Public communication discovery is independent of administrative Instance/configuration reads.
public sealed class TenantChatService(IIamStore security, ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask<TenantChatAgentPage> ListAgentsAsync(Guid tenant, TenantSessionInitiator human,
        int page = 0, int pageSize = 30, CancellationToken ct = default, string? query = null)
    {
        if (page is < 0 or > 100_000 || pageSize is < 1 or > 100) throw AgentCoreErrors.Validation("Chat page is invalid.");
        query = query?.Trim();
        if (query?.Length > 200) throw AgentCoreErrors.Validation("Chat search is too long.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var eligible = new List<Guid>();
        foreach (var executor in state.SystemIdentities.Where(i => i.Kind == SystemIdentityKind.Agent && i.Status == IdentityStatus.Active
            && i.AgentInstanceId is not null && state.Principals.Any(p => p.PrincipalId == i.PrincipalId && p.Status == IdentityStatus.Active)))
        {
            var id = executor.AgentInstanceId!.Value;
            if (!(await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agents.chat"),
                AccessScope.ForResource(tenant, "agent", id.ToString("D")), ct).ConfigureAwait(false)).Allowed) continue;
            if (!string.IsNullOrEmpty(query))
            {
                var candidate = await agents.FindTenantAgentAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                // Search only public picker labels. Private tone/settings never influence matches/counts.
                if (!candidate.Instance.Persona.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !candidate.Instance.Persona.Role.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            }
            eligible.Add(id);
        }
        // Filter before count/offset in the existing authoritative ownership store, never on a page
        // of admin-visible agents. A read-only administrator has no implied communication grant.
        var saved = await agents.ListTenantAgentsAsync(tenant, eligible, page, pageSize, ct).ConfigureAwait(false);
        var visible = new List<TenantChatAgent>(saved.Items.Count);
        foreach (var candidate in saved.Items)
        {
            TenantSessionRules.RequireExecutor(state, candidate);
            var instance = candidate.Instance;
            if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw new SecurityConflictException("stale_revision");
            var baseline = Guid.TryParse(instance.DefinitionId, out var definition)
                ? (await definitions.FindDefinitionPublicationAsync(tenant, definition, instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
                : await builtIns.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct).ConfigureAwait(false);
            if (baseline is null) throw new AccessDeniedException("definition_unavailable");
            var resolved = InstanceSettingsResolver.Resolve(baseline, instance.SettingsOverrides);
            visible.Add(new(instance.InstanceId, instance.Revision, instance.DefinitionId, instance.ActiveVersion,
                instance.Persona.Name, instance.Persona.Role, instance.Persona.Description, false, resolved.ConversationPolicy.Language));
        }
        var canCreate = true;
        foreach (var permission in new[] { "sessions.create", "sessions.read" })
            canCreate &= (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission),
                AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(visible, saved.Total, saved.Page, saved.PageSize, current.Tenant.Revision, canCreate);
    }
}
