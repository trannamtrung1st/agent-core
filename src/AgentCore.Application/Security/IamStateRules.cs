using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class IamStateRules
{
    public static TenantSecurityState Freeze(TenantSecurityState state)
    {
        var tenantId = state.Tenant.TenantId;
        void Require(bool ok) { if (!ok) throw new ArgumentException("Invalid tenant security aggregate.", nameof(state)); }
        Require(tenantId != Guid.Empty && state.Tenant.Revision >= 1 && Enum.IsDefined(state.Tenant.Status));
        Require(state.Users.All(u => u.TenantId == tenantId && u.AccountId != Guid.Empty && Enum.IsDefined(u.Status)));
        Require(state.Users.Select(u => u.AccountId).Distinct().Count() == state.Users.Count);
        Require(state.Principals.All(p => p.TenantId == tenantId && p.PrincipalId != Guid.Empty && Enum.IsDefined(p.Status) && Enum.IsDefined(p.Kind)));
        Require(state.Principals.Select(p => p.PrincipalId).Distinct().Count() == state.Principals.Count);
        Require(state.SystemIdentities.All(i => i.TenantId == tenantId && i.Kind == SystemIdentityKind.Agent && i.AgentInstanceId is { } agent && agent != Guid.Empty && Enum.IsDefined(i.Status)));
        Require(state.SystemIdentities.Select(i => i.AgentInstanceId).Distinct().Count() == state.SystemIdentities.Count);
        Require(state.Roles.All(r => r.TenantId == tenantId && r.RoleId != Guid.Empty && r.Permissions.Count > 0 && r.Permissions.Distinct(StringComparer.Ordinal).Count() == r.Permissions.Count
            && r.Permissions.All(k => PermissionCatalog.TryGet(k, out var definition) && !definition.SystemOnly)
            && (!r.Permissions.Any(k => PermissionCatalog.Get(k).HumanOnly) || r.HumanOnly)));
        Require(state.Roles.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == state.Roles.Count);
        var builtin = BuiltinAccessRoles.ForTenant(tenantId, () => Guid.Empty);
        foreach (var role in state.Roles.Where(r => r.IsBuiltin))
        {
            var expected = builtin.SingleOrDefault(r => r.Key == role.Key);
            Require(expected is not null && role.HumanOnly == expected.HumanOnly
                && role.Permissions.Order(StringComparer.Ordinal).SequenceEqual(expected.Permissions.Order(StringComparer.Ordinal), StringComparer.Ordinal));
        }
        Require(state.Roles.All(r => r.IsBuiltin || !builtin.Any(b => b.Key == r.Key)));
        foreach (var principal in state.Principals)
        {
            if (principal.Kind == PrincipalKind.User)
                Require(principal.SystemIdentityId is null && state.Users.Count(u => u.UserId == principal.UserId && u.PrincipalId == principal.PrincipalId
                    && u.AccountId == principal.AccountId && u.Status == principal.Status) == 1);
            else
                Require(principal.UserId is null && principal.AccountId is null && state.SystemIdentities.Count(i => i.SystemIdentityId == principal.SystemIdentityId
                    && i.PrincipalId == principal.PrincipalId && i.Status == principal.Status) == 1);
        }
        Require(state.Users.All(u => state.Principals.Count(p => p.PrincipalId == u.PrincipalId && p.UserId == u.UserId) == 1));
        Require(state.SystemIdentities.All(i => state.Principals.Count(p => p.PrincipalId == i.PrincipalId && p.SystemIdentityId == i.SystemIdentityId) == 1));
        Require(state.Assignments.All(a => a.TenantId == tenantId && a.Scope.TenantId == tenantId && a.Scope.Kind != AccessScopeKind.System
            && IamAuthorizer.ValidScope(a.Scope) && state.Roles.Any(r => r.RoleId == a.RoleId)
            && (a.Scope.Kind != AccessScopeKind.Resource || IamResourceScopes.Exists(state, a.Scope))
            && state.Principals.Any(p => p.PrincipalId == a.PrincipalId)
            && (state.Principals.Single(p => p.PrincipalId == a.PrincipalId).Kind == PrincipalKind.User || !state.Roles.Single(r => r.RoleId == a.RoleId).HumanOnly)
            && (state.Roles.Single(r => r.RoleId == a.RoleId).Key != BuiltinAccessRoles.Owner || a.Scope.Kind == AccessScopeKind.Tenant)));
        Require(state.Assignments.Select(a => (a.PrincipalId, a.RoleId, a.Scope)).Distinct().Count() == state.Assignments.Count);
        Require(state.Invitations.All(i => i.TenantId == tenantId && state.Roles.Any(r => r.RoleId == i.RoleId)
            && state.Principals.Any(p => p.PrincipalId == i.InvitedByPrincipalId && p.Kind == PrincipalKind.User)));
        TenantAccessService.EnsureOwner(state);
        return state with
        {
            Users = Array.AsReadOnly(state.Users.ToArray()), Principals = Array.AsReadOnly(state.Principals.ToArray()),
            SystemIdentities = Array.AsReadOnly(state.SystemIdentities.ToArray()),
            Roles = Array.AsReadOnly(state.Roles.Select(r => r with { Permissions = Array.AsReadOnly(r.Permissions.ToArray()) }).ToArray()),
            Assignments = Array.AsReadOnly(state.Assignments.ToArray()), Invitations = Array.AsReadOnly(state.Invitations.ToArray())
        };
    }
}
