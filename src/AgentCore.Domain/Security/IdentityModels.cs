namespace AgentCore.Domain.Security;

public enum IdentityStatus { Active, Suspended, Removed }
public enum PrincipalKind { User, SystemIdentity }
public enum AccessScopeKind { System, Tenant, Resource }
public enum SystemIdentityKind { Agent, PlatformService }

public sealed record Account(Guid AccountId, string LoginKey, string DisplayName,
    string PasswordHash, string SecurityStamp, IdentityStatus Status, DateTimeOffset CreatedAt);

public sealed record Tenant(Guid TenantId, string Name, string Slug, IdentityStatus Status,
    long Revision, DateTimeOffset CreatedAt, int? SavedChatRetentionDays = null);

public sealed record TenantUser(Guid UserId, Guid TenantId, Guid AccountId, Guid PrincipalId,
    IdentityStatus Status, DateTimeOffset CreatedAt);

public sealed record SystemIdentity(Guid SystemIdentityId, Guid? TenantId, Guid PrincipalId,
    SystemIdentityKind Kind, Guid? AgentInstanceId, IdentityStatus Status, string? ManagedKind = null);

public sealed record Principal(Guid PrincipalId, PrincipalKind Kind, Guid? TenantId,
    Guid? AccountId, Guid? UserId, Guid? SystemIdentityId, IdentityStatus Status);

public sealed record AccessRole(Guid RoleId, Guid? TenantId, string Key, string Name,
    bool IsBuiltin, bool HumanOnly, IReadOnlyList<string> Permissions);

public sealed record AccessScope(AccessScopeKind Kind, Guid? TenantId = null,
    string? ResourceType = null, string? ResourceId = null)
{
    public static AccessScope System { get; } = new(AccessScopeKind.System);
    public static AccessScope ForTenant(Guid tenantId) => new(AccessScopeKind.Tenant, tenantId);
    public static AccessScope ForResource(Guid tenantId, string type, string id) =>
        new(AccessScopeKind.Resource, tenantId, type, id);

    public bool Contains(AccessScope target) => Kind switch
    {
        AccessScopeKind.System => target.Kind == AccessScopeKind.System && target.TenantId is null,
        AccessScopeKind.Tenant => TenantId == target.TenantId && target.Kind is AccessScopeKind.Tenant or AccessScopeKind.Resource,
        AccessScopeKind.Resource => target.Kind == AccessScopeKind.Resource && TenantId == target.TenantId
            && ResourceType == target.ResourceType && ResourceId == target.ResourceId,
        _ => false
    };
}

public sealed record RoleAssignment(Guid AssignmentId, Guid? TenantId, Guid PrincipalId,
    Guid RoleId, AccessScope Scope, Guid CreatedByPrincipalId, DateTimeOffset CreatedAt);

public sealed record TenantInvitation(Guid InvitationId, Guid TenantId, string LoginKey,
    string TokenHash, Guid RoleId, Guid InvitedByPrincipalId, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt = null, bool Revoked = false);

public sealed record AuthenticationSession(Guid SessionId, Guid AccountId, string SecurityStamp,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt = null);

public sealed record AuthorizationAudit(Guid AuditId, Guid? TenantId, Guid ActorPrincipalId,
    string Action, string Target, string Decision, string ReasonCode, DateTimeOffset At,
    ExecutionAuthorizationAudit? Execution = null);

// One tenant security revision protects membership, role, identity, invitation and Owner changes.
// Stores commit this immutable aggregate and its audit atomically; it is not a permission snapshot.
public sealed record TenantSecurityState(Tenant Tenant, IReadOnlyList<TenantUser> Users,
    IReadOnlyList<Principal> Principals, IReadOnlyList<SystemIdentity> SystemIdentities,
    IReadOnlyList<AccessRole> Roles, IReadOnlyList<RoleAssignment> Assignments,
    IReadOnlyList<TenantInvitation> Invitations)
{
    public IReadOnlyList<TenantOperationPolicy> OperationPolicies { get; init; } = [];
}

public sealed record SystemSecurityState(IReadOnlyList<Principal> Principals,
    IReadOnlyList<AccessRole> Roles, IReadOnlyList<RoleAssignment> Assignments);

public sealed record TenantMembership(Tenant Tenant, TenantUser User);
