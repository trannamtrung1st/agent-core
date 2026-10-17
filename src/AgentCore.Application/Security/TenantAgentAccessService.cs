using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record AgentAccessAssignment(Guid AssignmentId, Guid PrincipalId, string PrincipalName, string PrincipalKind,
    Guid RoleId, string RoleName, AccessScope Scope, bool EffectiveChat, bool CanRevoke);
public sealed record AgentAccessPrincipal(Guid PrincipalId, string Name, string Kind);
public sealed record AgentAccessPermission(string Key, bool HumanOnly, bool Allowed, string ReasonCode, IReadOnlyList<Guid> AssignmentIds);
public sealed record AgentAccessPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record TenantAgentAccessView(Guid InstanceId, Guid PrincipalId, Guid SystemIdentityId, string Name, string Lifecycle,
    long InstanceRevision, long SecurityRevision, bool CanUpdate, bool CanGrantChat, bool CanGrantExecution, bool CanInspectOrganization,
    string AuthorityScope, AgentAccessPage<AgentAccessAssignment> Audience, AgentAccessPage<AgentAccessAssignment> Execution,
    AgentAccessPage<AgentAccessPrincipal> Principals, IReadOnlyList<AccessRole> ChatRoles, IReadOnlyList<AccessRole> ExecutionRoles,
    IReadOnlyList<AgentAccessPermission> Permissions);

// Read projections only. Existing assignment commands and IAM remain the authority for every change.
public sealed class TenantAgentAccessService(IIamStore security, ITenantAgentStore agents, IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask<TenantAgentAccessView> ReadAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int audiencePage = 0, int executionPage = 0, int principalPage = 0, string? q = null, string authorityScope = "agent", CancellationToken ct = default)
    {
        if (new[] { audiencePage, executionPage, principalPage }.Any(p => p is < 0 or > 100000) || q?.Length > 200 || authorityScope is not ("agent" or "tenant"))
            throw new ArgumentException("Select a valid page, search (up to 200 characters), and authority scope.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var scope = AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
        await Require("agents.read", scope); await Require("iam.roles.read", scope);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var organization = AccessScope.ForTenant(tenant);
        var canInspectOrganization = await Allowed("iam.roles.read", organization);
        var target = authorityScope == "tenant" ? organization : scope;
        await Require("iam.roles.read", target);
        var canGrantChat = await Allowed("iam.roles.assign", scope);
        var canGrantExecution = canInspectOrganization && await Allowed("iam.roles.assign", organization);
        var audience = new List<AgentAccessAssignment>(); var execution = new List<AgentAccessAssignment>();
        foreach (var assignment in state.Assignments.OrderBy(a => a.CreatedAt).ThenBy(a => a.AssignmentId))
        {
            var role = state.Roles.SingleOrDefault(r => r.RoleId == assignment.RoleId);
            if (role is null) continue;
            var chat = assignment.Scope.Contains(scope) && role.Permissions.Contains("agents.chat", StringComparer.Ordinal);
            var own = assignment.PrincipalId == agent.PrincipalId && await Allowed("iam.roles.read", assignment.Scope);
            if (!chat && !own) continue;
            var label = await PrincipalLabel(assignment.PrincipalId);
            var effective = chat && agent.Instance.Lifecycle == AgentInstanceLifecycle.Active
                && (await authorizer.CheckTenantAsync(state, assignment.PrincipalId, PermissionCatalog.Get("agents.chat"), scope, ct).ConfigureAwait(false)).Allowed;
            var row = new AgentAccessAssignment(assignment.AssignmentId, assignment.PrincipalId, label.Name, label.Kind,
                role.RoleId, role.Name, assignment.Scope, effective, await Allowed("iam.roles.assign", assignment.Scope));
            if (chat) audience.Add(row);
            if (own) execution.Add(row);
        }
        var candidates = new List<AgentAccessPrincipal>();
        if (canGrantChat)
        {
            foreach (var principal in state.Principals.Where(p => p.Status == IdentityStatus.Active).OrderBy(p => p.PrincipalId))
            {
                if (principal.Kind == PrincipalKind.User && !state.Users.Any(u => u.PrincipalId == principal.PrincipalId && u.Status == IdentityStatus.Active)) continue;
                if (principal.Kind == PrincipalKind.SystemIdentity)
                {
                    var system = state.SystemIdentities.SingleOrDefault(s => s.PrincipalId == principal.PrincipalId && s.Kind == SystemIdentityKind.Agent && s.Status == IdentityStatus.Active);
                    if (system?.AgentInstanceId is not { } candidate || !await Allowed("agents.read", AccessScope.ForResource(tenant, "agent", candidate.ToString("D")))) continue;
                }
                if (principal.Kind == PrincipalKind.User && (principal.AccountId is not { } account || (await security.ReadAccountAsync(account, ct).ConfigureAwait(false))?.Status != IdentityStatus.Active)) continue;
                var label = await PrincipalLabel(principal.PrincipalId);
                if (string.IsNullOrWhiteSpace(q) || label.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)) candidates.Add(label);
            }
        }
        var permissions = new List<AgentAccessPermission>();
        foreach (var permission in PermissionCatalog.All.Where(p => !p.SystemOnly && (target.Kind == AccessScopeKind.Tenant || p.AppliesToResource("agent"))).OrderBy(p => p.Key))
        {
            var decision = await authorizer.CheckTenantAsync(state, agent.PrincipalId, permission, target, ct).ConfigureAwait(false);
            permissions.Add(new(permission.Key, permission.HumanOnly, decision.Allowed, decision.ReasonCode, decision.AssignmentIds));
        }
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var currentAgent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision || currentAgent?.Instance.Revision != agent.Instance.Revision || currentAgent.PrincipalId != agent.PrincipalId)
            throw new SecurityConflictException("stale_revision");
        return new(instance, agent.PrincipalId, agent.SystemIdentityId, agent.Instance.Persona.Name, agent.Instance.Lifecycle.ToString(),
            agent.Instance.Revision, state.Tenant.Revision, await Allowed("agents.update", scope), canGrantChat, canGrantExecution, canInspectOrganization,
            authorityScope, Page(audience, audiencePage), Page(execution, executionPage), Page(candidates, principalPage),
            canGrantChat ? state.Roles.Where(r => r.Key != BuiltinAccessRoles.Owner && r.Permissions.Contains("agents.chat", StringComparer.Ordinal)).ToArray() : [],
            canGrantExecution ? state.Roles.Where(r => !r.HumanOnly && r.Permissions.All(k => !PermissionCatalog.Get(k).HumanOnly)).ToArray() : [], permissions);

        async ValueTask<bool> Allowed(string permission, AccessScope resource) => (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), resource, ct).ConfigureAwait(false)).Allowed;
        async ValueTask Require(string permission, AccessScope resource) { var decision = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), resource, ct).ConfigureAwait(false); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }
        async ValueTask<AgentAccessPrincipal> PrincipalLabel(Guid id)
        {
            var principal = state.Principals.SingleOrDefault(p => p.PrincipalId == id);
            if (principal?.Kind == PrincipalKind.User)
            {
                var account = principal.AccountId is { } accountId ? await security.ReadAccountAsync(accountId, ct).ConfigureAwait(false) : null;
                return new(id, account?.DisplayName ?? "Former member", "user");
            }
            var system = state.SystemIdentities.SingleOrDefault(s => s.PrincipalId == id);
            if (system?.AgentInstanceId is { } agentId && await Allowed("agents.read", AccessScope.ForResource(tenant, "agent", agentId.ToString("D"))))
            {
                var other = await agents.FindTenantAgentAsync(tenant, agentId, ct).ConfigureAwait(false);
                if (other is not null) return new(id, other.Instance.Persona.Name, "agent");
            }
            return new(id, "Agent identity", "agent");
        }
    }
    private static AgentAccessPage<T> Page<T>(IReadOnlyList<T> source, int page) => new(source.Skip(page * 10).Take(10).ToArray(), source.Count, page, 10);
}
