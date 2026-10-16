using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record AccessDecision(bool Allowed, string ReasonCode, IReadOnlyList<Guid> AssignmentIds)
{
    public static AccessDecision Deny(string reason) => new(false, reason, []);
}

public sealed class AccessDeniedException(string reasonCode) : Exception("This operation is not authorized.")
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed class SecurityConflictException(string reasonCode) : Exception("Security state changed. Reload before trying again.")
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed class IamAuthorizer(IIamStore store)
{
    // Target ownership must first be resolved by the owning service, never copied from a DTO.
    // No ambient default principal, tenant, owner token or permission cache is used here.
    public async ValueTask<AccessDecision> CheckAsync(Guid principalId, string permission,
        AccessScope target, CancellationToken ct = default)
    {
        if (!PermissionCatalog.TryGet(permission, out var metadata)) return AccessDecision.Deny("unknown_permission");
        if (!ValidScope(target)) return AccessDecision.Deny("invalid_scope");
        if (metadata.SystemOnly != (target.Kind == AccessScopeKind.System)) return AccessDecision.Deny("scope_mismatch");

        if (target.Kind == AccessScopeKind.System)
        {
            var system = await store.ReadSystemAsync(ct).ConfigureAwait(false);
            return await EvaluateAsync(principalId, metadata, target, system.Principals, system.Roles, system.Assignments, ct).ConfigureAwait(false);
        }
        var state = await store.ReadTenantAsync(target.TenantId!.Value, ct).ConfigureAwait(false);
        if (state is null || state.Tenant.Status != IdentityStatus.Active) return AccessDecision.Deny("tenant_unavailable");
        return await CheckTenantAsync(state, principalId, metadata, target, ct).ConfigureAwait(false);
    }

    public async ValueTask RequireAsync(Guid principalId, string permission, AccessScope target, CancellationToken ct = default)
    {
        var decision = await CheckAsync(principalId, permission, target, ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
    }

    internal async ValueTask<(T[] Items, bool All)> VisibleResourcesAsync<T>(TenantSecurityState state, Guid actor,
        string permission, IReadOnlyList<T> items, Func<T, AccessScope> scope, CancellationToken ct)
    {
        var metadata = PermissionCatalog.Get(permission);
        var inherited = await CheckTenantAsync(state, actor, metadata, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return (items.ToArray(), true);
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var visible = new List<T>();
        foreach (var item in items)
        {
            var target = scope(item);
            if (!state.Assignments.Any(a => a.PrincipalId == actor && a.Scope == target
                && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission, StringComparer.Ordinal)))) continue;
            var decision = await CheckTenantAsync(state, actor, metadata, target, ct).ConfigureAwait(false);
            if (decision.Allowed) visible.Add(item);
            else if (decision.ReasonCode is not ("permission_denied" or "resource_unavailable")) throw new AccessDeniedException(decision.ReasonCode);
        }
        return (visible.ToArray(), false);
    }

    internal async ValueTask<AccessDecision> CheckTenantAsync(TenantSecurityState state, Guid principalId,
        PermissionDefinition permission, AccessScope target, CancellationToken ct)
    {
        if (state.Tenant.Status != IdentityStatus.Active || state.Tenant.TenantId != target.TenantId
            || target.Kind == AccessScopeKind.System || permission.SystemOnly || !ValidScope(target))
            return AccessDecision.Deny("scope_mismatch");
        var principal = state.Principals.SingleOrDefault(p => p.PrincipalId == principalId);
        if (principal is null) return AccessDecision.Deny("not_member");
        if (principal.Kind == PrincipalKind.User
            && !state.Users.Any(u => u.PrincipalId == principalId && u.UserId == principal.UserId
                && u.AccountId == principal.AccountId && u.Status == IdentityStatus.Active))
            return AccessDecision.Deny("not_member");
        if (principal.Kind == PrincipalKind.SystemIdentity
            && !state.SystemIdentities.Any(s => s.PrincipalId == principalId && s.SystemIdentityId == principal.SystemIdentityId
                && s.TenantId == target.TenantId && s.Kind == SystemIdentityKind.Agent && s.AgentInstanceId is not null
                && s.Status == IdentityStatus.Active))
            return AccessDecision.Deny("identity_unavailable");
        if (target.Kind == AccessScopeKind.Resource)
        {
            if (!IamResourceScopes.Exists(state, target) && !await store.OwnsResourceAsync(target, ct).ConfigureAwait(false))
                return AccessDecision.Deny("resource_unavailable");
            if (!permission.AppliesToResource(target.ResourceType)) return AccessDecision.Deny("resource_type_mismatch");
        }
        return await EvaluateAsync(principalId, permission, target, state.Principals, state.Roles, state.Assignments, ct).ConfigureAwait(false);
    }

    private async ValueTask<AccessDecision> EvaluateAsync(Guid principalId, PermissionDefinition permission, AccessScope target,
        IReadOnlyList<Principal> principals, IReadOnlyList<AccessRole> roles, IReadOnlyList<RoleAssignment> assignments, CancellationToken ct)
    {
        var principal = principals.SingleOrDefault(p => p.PrincipalId == principalId && p.TenantId == target.TenantId);
        if (principal is null || principal.Status != IdentityStatus.Active) return AccessDecision.Deny("principal_unavailable");
        if (principal.Kind == PrincipalKind.SystemIdentity && permission.HumanOnly) return AccessDecision.Deny("human_only");
        if (principal.Kind == PrincipalKind.User)
        {
            var account = principal.AccountId is { } accountId ? await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false) : null;
            if (account?.Status != IdentityStatus.Active) return AccessDecision.Deny("account_unavailable");
        }
        var roleIds = roles.Where(r => r.TenantId == target.TenantId && (!r.HumanOnly || principal.Kind == PrincipalKind.User)
            && r.Permissions.Contains(permission.Key, StringComparer.Ordinal)).Select(r => r.RoleId).ToHashSet();
        var matches = assignments.Where(a => a.TenantId == target.TenantId && a.PrincipalId == principalId
            && roleIds.Contains(a.RoleId) && ValidScope(a.Scope) && a.Scope.TenantId == a.TenantId && a.Scope.Contains(target))
            .Select(a => a.AssignmentId).ToArray();
        return matches.Length > 0 ? new(true, "allowed", matches) : AccessDecision.Deny("permission_denied");
    }

    public static bool ValidScope(AccessScope scope) => scope.Kind switch
    {
        AccessScopeKind.System => scope.TenantId is null && scope.ResourceType is null && scope.ResourceId is null,
        AccessScopeKind.Tenant => scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType is null && scope.ResourceId is null,
        AccessScopeKind.Resource => scope.TenantId is { } tenant && tenant != Guid.Empty
            && !string.IsNullOrWhiteSpace(scope.ResourceType) && !string.IsNullOrWhiteSpace(scope.ResourceId),
        _ => false
    };
}
