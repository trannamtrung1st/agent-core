using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record IssuedTenantInvitation(TenantInvitation Invitation, string Token);

public sealed class TenantAccessService(IIamStore store, IamAuthorizer authorizer, IdentityService identities,
    IIdentityTokens tokens, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<TenantSecurityState> CreateTenantAsync(Guid systemActorId, Guid accountId,
        string name, string slug, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(systemActorId, "system.tenants.create", AccessScope.System, ct).ConfigureAwait(false);
        var system = await store.ReadSystemAsync(ct).ConfigureAwait(false);
        if (!system.Principals.Any(p => p.PrincipalId == systemActorId && p.AccountId == accountId && p.Kind == PrincipalKind.User))
            throw new AccessDeniedException("actor_mismatch");
        var account = await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false);
        if (account?.Status != IdentityStatus.Active) throw new AccessDeniedException("account_unavailable");
        var state = identities.CreateTenant(account, name, slug);
        if (!await store.TryCreateTenantAsync(state, Audit(state, systemActorId, "system.tenants.create", state.Tenant.TenantId.ToString("D")), ct).ConfigureAwait(false))
            throw new SecurityConflictException("tenant_exists");
        return state;
    }

    public async ValueTask<TenantSecurityState> ReadAsync(Guid actorId, Guid tenantId, string permission = "members.read", CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actorId, permission, AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        return await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
    }

    public async ValueTask<IssuedTenantInvitation> InviteAsync(Guid actorId, Guid tenantId, string login,
        Guid roleId, long expectedRevision, CancellationToken ct = default)
    {
        var state = await ForChangeAsync(actorId, tenantId, "members.invite", expectedRevision, ct).ConfigureAwait(false);
        await RequireInStateAsync(state, actorId, "iam.roles.assign", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        var role = state.Roles.SingleOrDefault(r => r.RoleId == roleId) ?? throw new AccessDeniedException("role_unavailable");
        _ = role; // A human accepts this invitation; system-only/cross-tenant roles cannot occur in the aggregate.
        var key = IdentityService.NormalizeLogin(login);
        if (state.Invitations.Any(i => i.LoginKey == key && !i.Revoked && i.AcceptedAt is null && i.ExpiresAt > time.GetUtcNow()))
            throw new SecurityConflictException("invitation_pending");
        var token = tokens.Create();
        var invitation = new TenantInvitation(ids.NewId(), tenantId, key, tokens.Hash(token), roleId, actorId, time.GetUtcNow().AddDays(7));
        await SaveAsync(state with { Invitations = state.Invitations.Append(invitation).ToArray() }, actorId, "members.invite", invitation.InvitationId, ct).ConfigureAwait(false);
        return new(invitation, token);
    }

    public async ValueTask<(TenantInvitation Invitation, Tenant Tenant)> InspectInvitationAsync(Guid tenantId, Guid invitationId,
        string token, CancellationToken ct = default)
    {
        var state = await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false);
        var invite = state?.Invitations.SingleOrDefault(i => i.InvitationId == invitationId);
        if (state?.Tenant.Status != IdentityStatus.Active || invite is null || invite.Revoked || invite.AcceptedAt is not null
            || invite.ExpiresAt <= time.GetUtcNow() || !tokens.Matches(token, invite.TokenHash))
            throw new AccessDeniedException("invitation_unavailable");
        await RequireInStateAsync(state, invite.InvitedByPrincipalId, "members.invite", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        await RequireInStateAsync(state, invite.InvitedByPrincipalId, "iam.roles.assign", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        return (invite, state.Tenant);
    }

    public async ValueTask<TenantUser> AcceptInvitationAsync(Guid accountId, Guid tenantId, Guid invitationId,
        string token, CancellationToken ct = default)
    {
        var account = await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false);
        if (account?.Status != IdentityStatus.Active) throw new AccessDeniedException("account_unavailable");
        var state = await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (state?.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("invitation_unavailable");
        var invite = state.Invitations.SingleOrDefault(i => i.InvitationId == invitationId && i.LoginKey == account.LoginKey);
        if (invite is null || !tokens.Matches(token, invite.TokenHash) || invite.Revoked || invite.ExpiresAt <= time.GetUtcNow())
            throw new AccessDeniedException("invitation_unavailable");
        var existing = state.Users.SingleOrDefault(u => u.AccountId == accountId);
        if (invite.AcceptedAt is not null)
            return existing?.Status == IdentityStatus.Active ? existing : throw new AccessDeniedException("not_member");
        if (existing is not null) throw new SecurityConflictException("membership_exists");
        // A revoked inviter cannot confer new authority through an old invitation.
        await RequireInStateAsync(state, invite.InvitedByPrincipalId, "members.invite", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        await RequireInStateAsync(state, invite.InvitedByPrincipalId, "iam.roles.assign", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        if (!state.Roles.Any(r => r.RoleId == invite.RoleId)) throw new AccessDeniedException("role_unavailable");
        var userId = ids.NewId(); var principalId = ids.NewId(); var now = time.GetUtcNow();
        var user = new TenantUser(userId, tenantId, accountId, principalId, IdentityStatus.Active, now);
        var next = state with
        {
            Users = state.Users.Append(user).ToArray(),
            Principals = state.Principals.Append(new(principalId, PrincipalKind.User, tenantId, accountId, userId, null, IdentityStatus.Active)).ToArray(),
            Assignments = state.Assignments.Append(new(ids.NewId(), tenantId, principalId, invite.RoleId, AccessScope.ForTenant(tenantId), invite.InvitedByPrincipalId, now)).ToArray(),
            Invitations = state.Invitations.Select(i => i.InvitationId == invitationId ? i with { AcceptedAt = now } : i).ToArray()
        };
        await SaveAsync(next, principalId, "members.accept_invitation", invitationId, ct).ConfigureAwait(false);
        return user;
    }

    public async ValueTask RevokeInvitationAsync(Guid actorId, Guid tenantId, Guid invitationId, long expectedRevision, CancellationToken ct = default)
    {
        var state = await ForChangeAsync(actorId, tenantId, "members.invite", expectedRevision, ct).ConfigureAwait(false);
        if (!state.Invitations.Any(i => i.InvitationId == invitationId)) throw new AccessDeniedException("invitation_unavailable");
        await SaveAsync(state with { Invitations = state.Invitations.Select(i => i.InvitationId == invitationId ? i with { Revoked = true } : i).ToArray() },
            actorId, "members.revoke_invitation", invitationId, ct).ConfigureAwait(false);
    }

    public async ValueTask SetMemberStatusAsync(Guid actorId, Guid tenantId, Guid userId, IdentityStatus status,
        long expectedRevision, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Unknown membership status.", nameof(status));
        var state = await ForChangeAsync(actorId, tenantId, status == IdentityStatus.Removed ? "members.remove" : "members.update", expectedRevision, ct).ConfigureAwait(false);
        var user = state.Users.SingleOrDefault(u => u.UserId == userId) ?? throw new AccessDeniedException("member_unavailable");
        var next = state with
        {
            Users = state.Users.Select(u => u.UserId == userId ? u with { Status = status } : u).ToArray(),
            Principals = state.Principals.Select(p => p.PrincipalId == user.PrincipalId ? p with { Status = status } : p).ToArray()
        };
        EnsureOwner(next);
        await SaveAsync(next, actorId, "members.status", userId, ct).ConfigureAwait(false);
    }

    public async ValueTask<AccessRole> SaveRoleAsync(Guid actorId, Guid tenantId, Guid? roleId, string name,
        IReadOnlyList<string> permissions, long expectedRevision, CancellationToken ct = default)
    {
        var state = await ForChangeAsync(actorId, tenantId, "iam.roles.manage", expectedRevision, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count is < 1 or > 256) throw new ArgumentException("Select between 1 and 256 permissions.", nameof(permissions));
        var keys = permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (keys.Any(k => !PermissionCatalog.TryGet(k, out var permission) || permission.SystemOnly)) throw new AccessDeniedException("permission_unavailable");
        var old = roleId is { } id ? state.Roles.SingleOrDefault(r => r.RoleId == id) ?? throw new AccessDeniedException("role_unavailable") : null;
        if (old?.IsBuiltin == true) throw new AccessDeniedException("builtin_role_immutable");
        var human = keys.Any(k => PermissionCatalog.Get(k).HumanOnly);
        if (human && old is not null && state.Assignments.Any(a => a.RoleId == old.RoleId
            && state.Principals.Any(p => p.PrincipalId == a.PrincipalId && p.Kind == PrincipalKind.SystemIdentity)))
            throw new AccessDeniedException("human_only");
        var role = new AccessRole(old?.RoleId ?? ids.NewId(), tenantId, old?.Key ?? $"custom-{ids.NewId():N}", IdentityService.CleanName(name), false, human, keys);
        var next = state with { Roles = state.Roles.Where(r => r.RoleId != role.RoleId).Append(role).ToArray() };
        await SaveAsync(next, actorId, "iam.roles.manage", role.RoleId, ct).ConfigureAwait(false);
        return role;
    }

    public async ValueTask<RoleAssignment> AssignRoleAsync(Guid actorId, Guid tenantId, Guid principalId, Guid roleId,
        AccessScope scope, long expectedRevision, CancellationToken ct = default)
    {
        if (!IamAuthorizer.ValidScope(scope) || scope.Kind == AccessScopeKind.System || scope.TenantId != tenantId)
            throw new AccessDeniedException("scope_mismatch");
        var state = await ForChangeAsync(actorId, tenantId, "iam.roles.assign", expectedRevision, ct, scope).ConfigureAwait(false);
        var principal = state.Principals.SingleOrDefault(p => p.PrincipalId == principalId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        var role = state.Roles.SingleOrDefault(r => r.RoleId == roleId) ?? throw new AccessDeniedException("role_unavailable");
        if (principal.Kind == PrincipalKind.SystemIdentity && (role.HumanOnly || role.Permissions.Any(k => PermissionCatalog.Get(k).HumanOnly)))
            throw new AccessDeniedException("human_only");
        if (role.Key == BuiltinAccessRoles.Owner && scope.Kind != AccessScopeKind.Tenant) throw new AccessDeniedException("owner_requires_tenant_scope");
        var existing = state.Assignments.SingleOrDefault(a => a.PrincipalId == principalId && a.RoleId == roleId && a.Scope == scope);
        if (existing is not null) return existing;
        var assignment = new RoleAssignment(ids.NewId(), tenantId, principalId, roleId, scope, actorId, time.GetUtcNow());
        await SaveAsync(state with { Assignments = state.Assignments.Append(assignment).ToArray() }, actorId, "iam.roles.assign", assignment.AssignmentId, ct).ConfigureAwait(false);
        return assignment;
    }

    public async ValueTask RevokeAssignmentAsync(Guid actorId, Guid tenantId, Guid assignmentId, long expectedRevision, CancellationToken ct = default)
    {
        var state = await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var assignment = state.Assignments.SingleOrDefault(a => a.AssignmentId == assignmentId) ?? throw new AccessDeniedException("assignment_unavailable");
        if (state.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        await RequireInStateAsync(state, actorId, "iam.roles.assign", assignment.Scope, ct).ConfigureAwait(false);
        var next = state with { Assignments = state.Assignments.Where(a => a.AssignmentId != assignmentId).ToArray() };
        EnsureOwner(next);
        await SaveAsync(next, actorId, "iam.roles.revoke", assignmentId, ct).ConfigureAwait(false);
    }

    // Called only by the owning instance lifecycle, after validating this exact tenant-owned Instance.
    // No role is assigned to a new agent implicitly, even when its creator is an Owner.
    public async ValueTask<Principal> RegisterAgentAsync(Guid actorId, Guid tenantId, Guid instanceId, long expectedRevision, CancellationToken ct = default)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("An owned Instance is required.", nameof(instanceId));
        var state = await ForChangeAsync(actorId, tenantId, "agents.create", expectedRevision, ct).ConfigureAwait(false);
        if (state.SystemIdentities.Any(i => i.AgentInstanceId == instanceId)) throw new SecurityConflictException("agent_identity_exists");
        var identityId = ids.NewId(); var principalId = ids.NewId();
        var principal = new Principal(principalId, PrincipalKind.SystemIdentity, tenantId, null, null, identityId, IdentityStatus.Active);
        await SaveAsync(state with
        {
            Principals = state.Principals.Append(principal).ToArray(),
            SystemIdentities = state.SystemIdentities.Append(new(identityId, tenantId, principalId, SystemIdentityKind.Agent, instanceId, IdentityStatus.Active)).ToArray()
        }, actorId, "agents.identity.create", instanceId, ct).ConfigureAwait(false);
        return principal;
    }

    internal static void EnsureOwner(TenantSecurityState state)
    {
        var ownerRoles = state.Roles.Where(r => r.IsBuiltin && r.Key == BuiltinAccessRoles.Owner && r.HumanOnly).Select(r => r.RoleId).ToHashSet();
        if (!state.Assignments.Any(a => ownerRoles.Contains(a.RoleId) && a.Scope == AccessScope.ForTenant(state.Tenant.TenantId)
            && state.Users.Any(u => u.PrincipalId == a.PrincipalId && u.Status == IdentityStatus.Active)
            && state.Principals.Any(p => p.PrincipalId == a.PrincipalId && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active)))
            throw new SecurityConflictException("last_owner");
    }

    private async ValueTask<TenantSecurityState> ForChangeAsync(Guid actorId, Guid tenantId, string permission,
        long expectedRevision, CancellationToken ct, AccessScope? scope = null)
    {
        var state = await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await RequireInStateAsync(state, actorId, permission, scope ?? AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private async ValueTask RequireInStateAsync(TenantSecurityState state, Guid actorId, string permission, AccessScope scope, CancellationToken ct)
    {
        var decision = await authorizer.CheckTenantAsync(state, actorId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
    }
    private async ValueTask SaveAsync(TenantSecurityState state, Guid actorId, string action, Guid target, CancellationToken ct)
    {
        var expected = state.Tenant.Revision;
        var next = state with { Tenant = state.Tenant with { Revision = checked(expected + 1) } };
        if (!await store.TrySaveTenantAsync(next, expected, Audit(next, actorId, action, target.ToString("D")), ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
    }
    private AuthorizationAudit Audit(TenantSecurityState state, Guid actor, string action, string target) =>
        new(ids.NewId(), state.Tenant.TenantId, actor, action, target, "allowed", "authorized", time.GetUtcNow());
}
