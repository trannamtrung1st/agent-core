using AgentCore.Domain.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentCore.Infrastructure.Security;

public sealed record IamRoleRow(Guid RoleId, Guid? TenantId, string Key, string Name, bool IsBuiltin, bool HumanOnly);
public sealed record IamRolePermissionRow(Guid RoleId, string PermissionKey);
public sealed record IamAssignmentRow(Guid AssignmentId, Guid? TenantId, Guid PrincipalId, Guid RoleId,
    AccessScopeKind ScopeKind, string ResourceType, string ResourceId, Guid CreatedByPrincipalId, DateTimeOffset CreatedAt);
public sealed record IamBootstrapRow(int Id, DateTimeOffset CompletedAt);
public sealed record IamSystemIdentityRow(Guid SystemIdentityId, Guid? TenantId, Guid PrincipalId,
    SystemIdentityKind Kind, string? AgentInstanceId, IdentityStatus Status);

internal static class IamPersistenceModel
{
    public static void Configure(ModelBuilder model)
    {
        TenantDefinitionRecord.Configure(model);
        TenantCredentialRecord.Configure(model);
        TenantSessionRecord.Configure(model);
        TenantUserProfileRecord.Configure(model);
        TenantLearnedMemoryRecord.Configure(model);
        TenantExperienceRecord.Configure(model);
        TenantApprovalRecord.Configure(model);
        TenantOperationReceiptRecord.Configure(model);
        TenantCredentialCheckDispatchRecord.Configure(model);
        var policies = model.Entity<TenantOperationPolicy>();
        policies.ToTable("TenantOperationPolicies", table => table.HasCheckConstraint("CK_TenantOperationPolicies_Values", "Mode IN (0,1,2) AND IndependentReview IN (0,1) AND Revision >= 1 AND length(PermissionKey) BETWEEN 1 AND 128"));
        policies.HasKey(p => new { p.TenantId, p.PermissionKey });
        policies.Property(p => p.PermissionKey).HasMaxLength(128);
        policies.Property(p => p.Revision).IsConcurrencyToken();
        policies.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        CredentialOAuthAttemptRow.Configure(model);
        var accounts = model.Entity<Account>();
        accounts.ToTable("Accounts"); accounts.HasKey(x => x.AccountId);
        accounts.Property(x => x.LoginKey).HasMaxLength(254); accounts.HasIndex(x => x.LoginKey).IsUnique();
        accounts.Property(x => x.PasswordHash).HasMaxLength(2048); accounts.Property(x => x.SecurityStamp).HasMaxLength(128).IsConcurrencyToken();
        var tenants = model.Entity<Tenant>();
        tenants.ToTable("Tenants"); tenants.HasKey(x => x.TenantId);
        tenants.HasIndex(x => x.Slug).IsUnique(); tenants.Property(x => x.Slug).HasMaxLength(64);
        tenants.Property(x => x.Revision).IsConcurrencyToken();
        var principals = model.Entity<Principal>();
        principals.ToTable("Principals", table => table.HasCheckConstraint("CK_Principal_Kind", "(Kind = 0 AND AccountId IS NOT NULL AND SystemIdentityId IS NULL) OR (Kind = 1 AND AccountId IS NULL AND UserId IS NULL AND SystemIdentityId IS NOT NULL)"));
        principals.HasKey(x => x.PrincipalId); principals.HasIndex(x => new { x.TenantId, x.PrincipalId }).IsUnique();
        principals.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        principals.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        var users = model.Entity<TenantUser>();
        users.ToTable("TenantUsers"); users.HasKey(x => x.UserId);
        users.HasIndex(x => new { x.TenantId, x.AccountId }).IsUnique(); users.HasIndex(x => x.PrincipalId).IsUnique();
        users.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        users.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        users.HasOne<Principal>().WithMany().HasForeignKey(x => x.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        users.HasAlternateKey(x => new { x.TenantId, x.PrincipalId, x.AccountId });
        var identities = model.Entity<IamSystemIdentityRow>();
        identities.ToTable("SystemIdentities"); identities.HasKey(x => x.SystemIdentityId);
        identities.HasIndex(x => x.AgentInstanceId).IsUnique(); identities.HasIndex(x => x.PrincipalId).IsUnique();
        identities.HasAlternateKey(x => new { x.TenantId, x.AgentInstanceId });
        identities.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        identities.HasOne<Principal>().WithMany().HasForeignKey(x => x.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        identities.HasOne<AgentCore.Infrastructure.Persistence.AgentInstanceRecord>().WithMany()
            .HasForeignKey(x => x.AgentInstanceId).OnDelete(DeleteBehavior.Restrict);
        var roles = model.Entity<IamRoleRow>();
        roles.ToTable("AccessRoles"); roles.HasKey(x => x.RoleId); roles.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        roles.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        var permissions = model.Entity<IamRolePermissionRow>();
        permissions.ToTable("AccessRolePermissions"); permissions.HasKey(x => new { x.RoleId, x.PermissionKey });
        permissions.HasOne<IamRoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        var assignments = model.Entity<IamAssignmentRow>();
        assignments.ToTable("AccessRoleAssignments", table => table.HasCheckConstraint("CK_Assignment_Scope", "(ScopeKind = 0 AND TenantId IS NULL AND ResourceType = '' AND ResourceId = '') OR (ScopeKind = 1 AND TenantId IS NOT NULL AND ResourceType = '' AND ResourceId = '') OR (ScopeKind = 2 AND TenantId IS NOT NULL AND ResourceType <> '' AND ResourceId <> '')"));
        assignments.HasKey(x => x.AssignmentId);
        assignments.HasIndex(x => new { x.TenantId, x.PrincipalId, x.RoleId, x.ScopeKind, x.ResourceType, x.ResourceId }).IsUnique();
        assignments.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasOne<Principal>().WithMany().HasForeignKey(x => x.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasOne<IamRoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        var invitations = model.Entity<TenantInvitation>();
        invitations.ToTable("TenantInvitations"); invitations.HasKey(x => x.InvitationId);
        invitations.HasIndex(x => new { x.TenantId, x.LoginKey, x.ExpiresAt }); invitations.HasIndex(x => x.TokenHash).IsUnique();
        invitations.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        invitations.HasOne<IamRoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        invitations.HasOne<Principal>().WithMany().HasForeignKey(x => x.InvitedByPrincipalId).OnDelete(DeleteBehavior.Restrict);
        var sessions = model.Entity<AuthenticationSession>();
        sessions.ToTable("AuthenticationSessions"); sessions.HasKey(x => x.SessionId);
        sessions.HasIndex(x => new { x.AccountId, x.ExpiresAt });
        sessions.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        var audit = model.Entity<AuthorizationAudit>();
        audit.ToTable("AuthorizationAudit", table => table.HasCheckConstraint("CK_AuthorizationAudit_Execution",
            "ExecutionJson IS NULL OR (length(ExecutionJson) <= 4096 AND json_valid(ExecutionJson))"));
        audit.HasKey(x => x.AuditId); audit.HasIndex(x => new { x.TenantId, x.At, x.AuditId });
        audit.Property(x => x.Execution).HasColumnName("ExecutionJson").HasConversion(
            value => value == null ? null : JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => value == null ? null : JsonSerializer.Deserialize<ExecutionAuthorizationAudit>(value, (JsonSerializerOptions?)null));
        audit.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        var bootstrap = model.Entity<IamBootstrapRow>();
        bootstrap.ToTable("IdentityBootstrap", table => table.HasCheckConstraint("CK_IdentityBootstrap_One", "Id = 1")); bootstrap.HasKey(x => x.Id);

        // Domain UTC values have one deterministic SQLite representation; no provider-specific types leak out.
        model.Entity<AuthenticationSession>().HasAlternateKey(s => new { s.SessionId, s.AccountId });
        foreach (var type in new[] { typeof(Account), typeof(Tenant), typeof(TenantUser), typeof(TenantInvitation), typeof(CredentialOAuthAttemptRow),
            typeof(AuthenticationSession), typeof(AuthorizationAudit), typeof(IamAssignmentRow), typeof(IamBootstrapRow), typeof(TenantDefinitionRecord), typeof(TenantCredentialRecord) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value)));
            if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(new ValueConverter<DateTimeOffset?, long?>(value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
                    value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null));
        }
    }

    public static IamAssignmentRow Row(RoleAssignment a) => new(a.AssignmentId, a.TenantId, a.PrincipalId, a.RoleId,
        a.Scope.Kind, a.Scope.ResourceType ?? "", a.Scope.ResourceId ?? "", a.CreatedByPrincipalId, a.CreatedAt);
    public static RoleAssignment Domain(IamAssignmentRow a) => new(a.AssignmentId, a.TenantId, a.PrincipalId, a.RoleId,
        new(a.ScopeKind, a.TenantId, a.ResourceType.Length == 0 ? null : a.ResourceType, a.ResourceId.Length == 0 ? null : a.ResourceId), a.CreatedByPrincipalId, a.CreatedAt);
    public static IamRoleRow Row(AccessRole role) => new(role.RoleId, role.TenantId, role.Key, role.Name, role.IsBuiltin, role.HumanOnly);
    public static IamSystemIdentityRow Row(SystemIdentity identity) => new(identity.SystemIdentityId, identity.TenantId,
        identity.PrincipalId, identity.Kind, identity.AgentInstanceId?.ToString("D"), identity.Status);
    public static SystemIdentity Domain(IamSystemIdentityRow row) => new(row.SystemIdentityId, row.TenantId,
        row.PrincipalId, row.Kind, row.AgentInstanceId is null ? null : Guid.Parse(row.AgentInstanceId), row.Status);
}
