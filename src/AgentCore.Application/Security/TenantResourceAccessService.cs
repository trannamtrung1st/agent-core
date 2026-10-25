using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record ResourceAccessAssignment(Guid AssignmentId, Guid PrincipalId, string PrincipalName, string PrincipalKind,
    Guid RoleId, string RoleName, AccessScope Scope, bool Inherited, bool CanRevoke);
public sealed record TenantResourceAccessEditor(AccessScope Scope, string Name, long SecurityRevision, bool CanAssign, bool CanManageRoles,
    AgentAccessPage<ResourceAccessAssignment> Assignments, AgentAccessPage<AgentAccessPrincipal> Principals, IReadOnlyList<AccessRole> Roles);

/// <summary>Human management projection; existing IAM commands own all assignments and audit.</summary>
public sealed class TenantResourceAccessService(IIamStore security, ITenantAgentStore agents, ITenantSpaceStore spaces,
    ITenantSecretStore secrets, IdentityService identity, IamAuthorizer iam, ITenantSpaceResourceStore? sourceResources = null)
{
    public async ValueTask<TenantResourceAccessEditor> ReadAsync(Guid tenant, string resourceType, string resourceId, HumanSessionReference human,
        int page = 0, int principalPage = 0, string? search = null, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || principalPage is < 0 or > 100_000 || search?.Length > 200
            || resourceType is not ("space" or "secret" or "workspace" or "resource")) throw new ArgumentException("Choose a supported resource and bounded page or search.");
        var scope = IamResourceScopes.Create(tenant, resourceType, resourceId);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await Require("iam.roles.read", scope).ConfigureAwait(false);
        string name;
        if (resourceType == "resource")
        {
            if (!TenantResourceScopes.TrySpace(scope, out var sourceSpace, out var sourceId) || sourceResources is null) throw new AccessDeniedException("resource_unavailable");
            var source = await sourceResources.FindSpaceResourceAsync(tenant, sourceSpace, sourceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            await Require("spaces.read", TenantSpaceService.Scope(tenant, sourceSpace)).ConfigureAwait(false);
            await Require("resources.read", scope).ConfigureAwait(false); name = source.LogicalPath;
        }
        else if (resourceType == "secret")
        {
            var value = await secrets.FindSecretAsync(tenant, Guid.Parse(resourceId), ct).ConfigureAwait(false);
            if (value is null || value.Status == SecretStatus.Deleted) throw new AccessDeniedException("resource_unavailable");
            await Require("secrets.read", scope).ConfigureAwait(false); name = value.Name;
        }
        else
        {
            Guid id;
            if (resourceType == "space") id = Guid.Parse(resourceId);
            else if (resourceId.StartsWith("home:", StringComparison.Ordinal) && Guid.TryParseExact(resourceId[5..], "D", out var parsed)) id = parsed;
            else throw new ArgumentException("Choose an exact Space home workspace scope.");
            var value = await spaces.FindSpaceAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            await Require("spaces.read", TenantSpaceService.Scope(tenant, id)).ConfigureAwait(false);
            name = resourceType == "space" ? value.Name : value.Name + " · /home files";
        }
        var canAssign = await Allowed("iam.roles.assign", scope).ConfigureAwait(false);
        var assignments = state.Assignments.Where(a => a.Scope.Contains(scope)).OrderBy(a => a.CreatedAt).ThenBy(a => a.AssignmentId).ToArray();
        var rows = new List<ResourceAccessAssignment>();
        foreach (var assignment in assignments.Skip(page * 30).Take(30))
        {
            var role = state.Roles.Single(r => r.RoleId == assignment.RoleId); var label = await Label(assignment.PrincipalId).ConfigureAwait(false);
            rows.Add(new(assignment.AssignmentId, assignment.PrincipalId, label.Name, label.Kind, role.RoleId, role.Name, assignment.Scope,
                assignment.Scope.Kind == AccessScopeKind.Tenant, await Allowed("iam.roles.assign", assignment.Scope).ConfigureAwait(false)));
        }
        var principals = new List<AgentAccessPrincipal>();
        if (canAssign)
            foreach (var principal in state.Principals.Where(p => p.Status == IdentityStatus.Active).OrderBy(p => p.PrincipalId))
            {
                if (principal.Kind == PrincipalKind.User)
                {
                    if (!state.Users.Any(u => u.PrincipalId == principal.PrincipalId && u.Status == IdentityStatus.Active)
                        || principal.AccountId is not { } account || (await security.ReadAccountAsync(account, ct).ConfigureAwait(false))?.Status != IdentityStatus.Active) continue;
                }
                else if (state.SystemIdentities.SingleOrDefault(s => s.PrincipalId == principal.PrincipalId && s.Kind == SystemIdentityKind.Agent && s.Status == IdentityStatus.Active)?.AgentInstanceId is not { } instance
                    || !await Allowed("agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))).ConfigureAwait(false)
                    || (await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false))?.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) continue;
                var label = await Label(principal.PrincipalId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(search) || label.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)) principals.Add(label);
            }
        var roles = canAssign ? state.Roles.Where(r => r.Key != BuiltinAccessRoles.Owner && r.Permissions.Any(k => PermissionCatalog.Get(k).AppliesToResource(resourceType))).ToArray() : [];
        var canManageRoles = await Allowed("iam.roles.manage", AccessScope.ForTenant(tenant)).ConfigureAwait(false);
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("stale_revision");
        return new(scope, name, state.Tenant.Revision, canAssign, canManageRoles, new(rows, assignments.Length, page, 30),
            new(principals.Skip(principalPage * 30).Take(30).ToArray(), principals.Count, principalPage, 30), roles);

        async ValueTask<bool> Allowed(string permission, AccessScope target) => (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), target, ct).ConfigureAwait(false)).Allowed;
        async ValueTask Require(string permission, AccessScope target) { if (!await Allowed(permission, target).ConfigureAwait(false)) throw new AccessDeniedException("permission_denied"); }
        async ValueTask<AgentAccessPrincipal> Label(Guid id)
        {
            var principal = state.Principals.SingleOrDefault(p => p.PrincipalId == id);
            if (principal?.Kind == PrincipalKind.User)
                return new(id, principal.AccountId is { } account ? (await security.ReadAccountAsync(account, ct).ConfigureAwait(false))?.DisplayName ?? "Former member" : "Former member", "user");
            var system = state.SystemIdentities.SingleOrDefault(s => s.PrincipalId == id);
            if (system?.AgentInstanceId is { } instance && await Allowed("agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D"))).ConfigureAwait(false)
                && await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) is { } agent) return new(id, agent.Instance.Persona.Name, "agent");
            return new(id, "Agent identity", "agent");
        }
    }
}
