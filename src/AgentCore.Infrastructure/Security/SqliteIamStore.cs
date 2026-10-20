using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore(IDbContextFactory<AgentCoreDbContext> contexts, IDefinitionResourceContentStore? definitionContent = null,
    IBuiltInAgentDefinitionStore? builtIns = null, AgentCore.Application.Agents.InheritedDefinitionResourceCatalog? inheritedResources = null,
    TimeProvider? clock = null, SqliteArtifactStore? artifactBacking = null, SqliteAttachmentStore? attachmentBacking = null, BrowserPrivacyAuthority? browserPrivacyAuthority = null) : IIamStore, ITenantSandboxRuntimeStore, ITenantSandboxVerificationStore, ITenantSandboxProfileStore, ITenantAgentStore, ITenantDefinitionStore, ITenantCredentialStore, ITenantCredentialBindingStore, ITenantCredentialOAuthStore, ITenantSessionStore, ITenantRunStore, ITenantOperationPolicyStore
{
    private readonly BrowserPrivacyAuthority privacyAuthority = BrowserPrivacyRules.FreezeAuthority(browserPrivacyAuthority ?? new(true, false, [], []));
    public async ValueTask<Account?> FindAccountAsync(string loginKey, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.LoginKey == loginKey, ct).ConfigureAwait(false); }
    public async ValueTask<Account?> ReadAccountAsync(Guid accountId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == accountId, ct).ConfigureAwait(false); }
    public async ValueTask<bool> HasBootstrapAsync(CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await db.Set<IamBootstrapRow>().AnyAsync(ct).ConfigureAwait(false); }

    public async ValueTask<bool> TryBootstrapAsync(Account account, SystemSecurityState system, TenantSecurityState tenant,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        tenant = IamStateRules.Freeze(tenant);
        if (tenant.SystemIdentities.Count > 0 || tenant.Assignments.Any(a => a.Scope.Kind == AccessScopeKind.Resource))
            throw new ArgumentException("Bootstrap cannot seed resource identities or grants.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (await db.Set<IamBootstrapRow>().AnyAsync(ct).ConfigureAwait(false) || await db.Set<Account>().AnyAsync(ct).ConfigureAwait(false)) return false;
        db.Add(account); db.Add(new IamBootstrapRow(1, audit.At)); db.Add(tenant.Tenant);
        db.AddRange(system.Principals); db.AddRange(system.Roles.Select(IamPersistenceModel.Row));
        db.AddRange(system.Roles.SelectMany(r => r.Permissions.Select(p => new IamRolePermissionRow(r.RoleId, p))));
        db.AddRange(system.Assignments.Select(IamPersistenceModel.Row));
        AddTenantChildren(db, tenant); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await transaction.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    public async ValueTask<bool> TryCreateAccountAsync(Account account, Principal accountPrincipal, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.Set<IamBootstrapRow>().AnyAsync(ct).ConfigureAwait(false)) throw new SecurityConflictException("bootstrap_required");
        if (accountPrincipal.AccountId != account.AccountId || accountPrincipal.Kind != PrincipalKind.User || accountPrincipal.TenantId is not null
            || accountPrincipal.UserId is not null || accountPrincipal.SystemIdentityId is not null || accountPrincipal.Status != account.Status)
            throw new ArgumentException("Invalid Account Principal.");
        db.Add(account); db.Add(accountPrincipal);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    public async ValueTask<bool> TryUpdateAccountAsync(Account account, string expectedSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<Account>().SingleOrDefaultAsync(a => a.AccountId == account.AccountId, ct).ConfigureAwait(false);
        if (old is null || old.SecurityStamp != expectedSecurityStamp) return false;
        if (account.LoginKey != old.LoginKey || audit.TenantId is not null || string.IsNullOrEmpty(account.SecurityStamp)) throw new ArgumentException("Invalid Account update.");
        if (account.Status != IdentityStatus.Active)
        {
            var tenantIds = await db.Set<TenantUser>().Where(u => u.AccountId == account.AccountId).Select(u => u.TenantId).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var tenantId in tenantIds)
            {
                var state = await ReadTenantAsync(db, tenantId, ct).ConfigureAwait(false);
                if (state is not null) await EnsureActiveAccountOwnerAsync(db, state, account, ct).ConfigureAwait(false);
            }
        }
        db.Entry(old).CurrentValues.SetValues(account); db.Add(audit);
        await db.SaveChangesAsync(ct).ConfigureAwait(false); await transaction.CommitAsync(ct).ConfigureAwait(false); return true;
    }
    public async ValueTask<SystemSecurityState> ReadSystemAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadSystemAsync(db, ct); await tx.CommitAsync(ct); return state;
    }
    private static async ValueTask<SystemSecurityState> ReadSystemAsync(AgentCoreDbContext db, CancellationToken ct)
    {
        var roles = await RolesAsync(db, null, ct).ConfigureAwait(false);
        return new(Array.AsReadOnly(await db.Set<Principal>().AsNoTracking().Where(p => p.TenantId == null).ToArrayAsync(ct).ConfigureAwait(false)), roles,
            Array.AsReadOnly((await db.Set<IamAssignmentRow>().AsNoTracking().Where(a => a.TenantId == null).ToArrayAsync(ct).ConfigureAwait(false)).Select(IamPersistenceModel.Domain).ToArray()));
    }
    public async ValueTask<TenantSecurityState?> ReadTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        // A read transaction prevents combining pre-revocation roles with post-revocation membership.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenantId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return state;
    }
    public async ValueTask<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await (from user in db.Set<TenantUser>().AsNoTracking()
            join tenant in db.Set<Tenant>().AsNoTracking() on user.TenantId equals tenant.TenantId
            where user.AccountId == accountId && user.Status == IdentityStatus.Active && tenant.Status == IdentityStatus.Active
            orderby tenant.Name, tenant.TenantId select new TenantMembership(tenant, user)).ToArrayAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TryCreateTenantAsync(TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
    {
        tenant = IamStateRules.Freeze(tenant); ValidateAudit(tenant, audit);
        if (tenant.Tenant.Revision != 1) return false;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await db.Set<IamBootstrapRow>().AnyAsync(ct).ConfigureAwait(false)) return false;
        await EnsureActiveAccountOwnerAsync(db, tenant, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, tenant, ct).ConfigureAwait(false);
        db.Add(tenant.Tenant); AddTenantChildren(db, tenant); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await transaction.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    public async ValueTask<bool> TrySaveTenantAsync(TenantSecurityState tenant, long expectedRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        tenant = IamStateRules.Freeze(tenant); ValidateAudit(tenant, audit);
        if (tenant.Tenant.Revision != checked(expectedRevision + 1)) return false;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == tenant.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expectedRevision) return false;
        var before = (await ReadTenantAsync(db, tenant.Tenant.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, tenant);
        foreach (var actor in IamStateRules.SecurityWriteActors(before, tenant, audit))
            await ValidateTenantActorAsync(db, actor.State, actor.Principal, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, tenant, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, tenant, ct).ConfigureAwait(false);
        db.Entry(current).CurrentValues.SetValues(tenant.Tenant);
        await ApplyTenantChildrenAsync(db, tenant, ct).ConfigureAwait(false);
        db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await transaction.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    private static async Task ApplyTenantChildrenAsync(AgentCoreDbContext db, TenantSecurityState tenant, CancellationToken ct)
    {
        await ApplyAsync(db, db.Set<TenantOperationPolicy>().Where(p => p.TenantId == tenant.Tenant.TenantId), tenant.OperationPolicies, p => p.PermissionKey, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<Principal>().Where(p => p.TenantId == tenant.Tenant.TenantId), tenant.Principals, p => p.PrincipalId, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<TenantUser>().Where(u => u.TenantId == tenant.Tenant.TenantId), tenant.Users, u => u.UserId, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<IamSystemIdentityRow>().Where(i => i.TenantId == tenant.Tenant.TenantId), tenant.SystemIdentities.Select(IamPersistenceModel.Row).ToArray(), i => i.SystemIdentityId, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<IamRoleRow>().Where(r => r.TenantId == tenant.Tenant.TenantId), tenant.Roles.Select(IamPersistenceModel.Row).ToArray(), r => r.RoleId, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<IamRolePermissionRow>().Where(p => db.Set<IamRoleRow>().Any(r => r.TenantId == tenant.Tenant.TenantId && r.RoleId == p.RoleId)),
            tenant.Roles.SelectMany(r => r.Permissions.Select(p => new IamRolePermissionRow(r.RoleId, p))).ToArray(), p => (p.RoleId, p.PermissionKey), ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<IamAssignmentRow>().Where(a => a.TenantId == tenant.Tenant.TenantId), tenant.Assignments.Select(IamPersistenceModel.Row).ToArray(), a => a.AssignmentId, ct).ConfigureAwait(false);
        await ApplyAsync(db, db.Set<TenantInvitation>().Where(i => i.TenantId == tenant.Tenant.TenantId), tenant.Invitations, i => i.InvitationId, ct).ConfigureAwait(false);
    }
    public async ValueTask SaveSessionAsync(AuthenticationSession session, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); db.Add(session); await db.SaveChangesAsync(ct).ConfigureAwait(false); }
    public async ValueTask<AuthenticationSession?> ReadSessionAsync(Guid sessionId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); return await db.Set<AuthenticationSession>().AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId, ct).ConfigureAwait(false); }
    public async ValueTask RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Set<AuthenticationSession>().Where(s => s.SessionId == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.RevokedAt, now), ct).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<AuthorizationAudit>> ReadAuditAsync(Guid tenantId, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<AuthorizationAudit>().AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.At)
            .ThenByDescending(a => a.AuditId).Take(Math.Clamp(limit, 1, 200)).ToArrayAsync(ct).ConfigureAwait(false);
    }
    private static async Task EnsureActiveAccountOwnerAsync(AgentCoreDbContext db, TenantSecurityState state, Account? changed, CancellationToken ct)
    {
        var ownerRole = state.Roles.Single(r => r.Key == BuiltinAccessRoles.Owner && r.IsBuiltin);
        var owners = state.Assignments.Where(a => a.RoleId == ownerRole.RoleId && a.Scope == AccessScope.ForTenant(state.Tenant.TenantId)).Select(a => a.PrincipalId).ToHashSet();
        var accountIds = state.Users.Where(u => owners.Contains(u.PrincipalId) && u.Status == IdentityStatus.Active).Select(u => u.AccountId).ToArray();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.AccountId)).ToArrayAsync(ct).ConfigureAwait(false);
        if (!accounts.Any(a => (a.AccountId == changed?.AccountId ? changed.Status : a.Status) == IdentityStatus.Active))
            throw new SecurityConflictException("last_owner");
    }
    private async Task EnsureResourceOwnershipAsync(AgentCoreDbContext db, TenantSecurityState state, CancellationToken ct, Guid? creatingInstance = null)
    {
        foreach (var assignment in state.Assignments.Where(a => TenantEventAdmissionRules.TryId(a.Scope, out _)))
            if (!await db.CoreEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == state.Tenant.TenantId && e.EventId == assignment.Scope.ResourceId, ct))
                throw new ArgumentException("A Core event assignment requires its actual tenant fact.");
        foreach (var assignment in state.Assignments.Where(a => TenantWebhookRules.TryId(a.Scope, out _)))
            if (!await db.WebhookEvents.IgnoreQueryFilters().AnyAsync(s => s.TenantId == state.Tenant.TenantId && s.ResourceId == assignment.Scope.ResourceId, ct))
                throw new ArgumentException("A webhook assignment requires its actual tenant source.");
        foreach (var assignment in state.Assignments.Where(a => TenantWorkspaceRules.TryParse(a.Scope, out _, out _)))
            if (!await OwnsWorkspace(db, assignment.Scope, ct).ConfigureAwait(false))
                throw new ArgumentException("A workspace assignment requires its actual current tenant owner.");
        foreach (var assignment in state.Assignments.Where(a => TenantSandboxProfileRules.TryId(a.Scope, out _)))
            if (!await OwnedSandboxProfiles(db, state.Tenant.TenantId).AnyAsync(r => r.Id == assignment.Scope.ResourceId, ct).ConfigureAwait(false))
                throw new ArgumentException("A Sandbox profile assignment requires its actual owned profile.");
        foreach (var assignment in state.Assignments.Where(a => TenantApprovalRules.TryApprovalId(a.Scope, out _)))
            if (!await db.Set<TenantApprovalRecord>().AnyAsync(r => r.TenantId == state.Tenant.TenantId && r.RequestId == Guid.Parse(assignment.Scope.ResourceId!), ct).ConfigureAwait(false))
                throw new ArgumentException("An Approval assignment requires its actual owned request.");
        foreach (var assignment in state.Assignments.Where(a => TenantRunRules.TryId(a.Scope, out _)))
            if (!await OwnedRuns(db, state.Tenant.TenantId).AnyAsync(r => r.AgentRunId == assignment.Scope.ResourceId, ct).ConfigureAwait(false))
                throw new ArgumentException("A Run assignment requires a current owned Run.");
        foreach (var assignment in state.Assignments.Where(a => TenantSessionRules.TryId(a.Scope, out _)))
            if (!await OwnedSessions(db, state.Tenant.TenantId).AnyAsync(s => s.SessionId == assignment.Scope.ResourceId, ct).ConfigureAwait(false))
                throw new ArgumentException("A Session assignment requires a current owned Session.");
        foreach (var assignment in state.Assignments.Where(a => TenantCredentialRules.TryId(a.Scope, out _)))
            if (!await OwnedCredentials(db, state.Tenant.TenantId).AnyAsync(c => c.CredentialId == assignment.Scope.ResourceId, ct).ConfigureAwait(false))
                throw new ArgumentException("A credential assignment requires a current owned credential.");
        foreach (var assignment in state.Assignments)
            if (TenantDefinitionResourceScopes.TryParse(assignment.Scope, out var reference)
                && !await OwnsDefinitionResourceAsync(db, state.Tenant.TenantId, reference, ct).ConfigureAwait(false))
                throw new ArgumentException("A Definition resource assignment requires a current owned binding.");
        foreach (var assignment in state.Assignments.Where(a => a.Scope.Kind == AccessScopeKind.Resource && a.Scope.ResourceType == "definition"))
            if (!await db.Set<TenantDefinitionRecord>().AnyAsync(d => d.TenantId == state.Tenant.TenantId && d.DeletedAt == null && d.DefinitionId == assignment.Scope.ResourceId, ct).ConfigureAwait(false))
                throw new ArgumentException("A definition assignment requires an owned definition.");
        foreach (var group in state.Assignments.Where(a => TenantAgentChildScopes.TryParse(a.Scope, out _, out _))
            .GroupBy(a => { TenantAgentChildScopes.TryParse(a.Scope, out var id, out _); return id; }))
        {
            var catalog = await ChildCatalogAsync(db, state.Tenant.TenantId, group.Key, null, ct).ConfigureAwait(false);
            if (group.Any(a => !catalog.Contains(a.Scope))) throw new ArgumentException("A child assignment requires a current child of the owned Instance.");
        }
        var instanceIds = state.SystemIdentities.Where(i => i.AgentInstanceId is not null).Select(i => i.AgentInstanceId!.Value.ToString("D")).ToArray();
        if (await db.Set<IamSystemIdentityRow>().AnyAsync(i => i.TenantId != state.Tenant.TenantId
            && i.AgentInstanceId != null && instanceIds.Contains(i.AgentInstanceId), ct).ConfigureAwait(false))
            throw new SecurityConflictException("agent_identity_exists");
        var required = instanceIds.Where(id => id != creatingInstance?.ToString("D")).ToArray();
        if (await db.AgentInstances.CountAsync(i => required.Contains(i.InstanceId), ct).ConfigureAwait(false) != required.Length)
            throw new ArgumentException("An agent identity requires a backing Instance.");
    }
    private static void ValidateAudit(TenantSecurityState state, AuthorizationAudit audit)
    { if (audit.TenantId != state.Tenant.TenantId) throw new ArgumentException("Audit must belong to the changed tenant.", nameof(audit)); }
    private static bool IsConstraint(DbUpdateException ex) => ex.InnerException is SqliteException { SqliteErrorCode: 19 };
    private static void AddTenantChildren(AgentCoreDbContext db, TenantSecurityState state)
    {
        db.AddRange(state.Principals); db.AddRange(state.Users); db.AddRange(state.SystemIdentities.Select(IamPersistenceModel.Row));
        db.AddRange(state.Roles.Select(IamPersistenceModel.Row));
        db.AddRange(state.Roles.SelectMany(r => r.Permissions.Select(p => new IamRolePermissionRow(r.RoleId, p))));
        db.AddRange(state.Assignments.Select(IamPersistenceModel.Row)); db.AddRange(state.Invitations);
    }
    private static async Task<IReadOnlyList<AccessRole>> RolesAsync(AgentCoreDbContext db, Guid? tenantId, CancellationToken ct)
    {
        var rows = await db.Set<IamRoleRow>().AsNoTracking().Where(r => r.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false);
        var permissions = await db.Set<IamRolePermissionRow>().AsNoTracking()
            .Where(p => db.Set<IamRoleRow>().Any(r => r.RoleId == p.RoleId && r.TenantId == tenantId)).ToArrayAsync(ct).ConfigureAwait(false);
        return Array.AsReadOnly(rows.Select(r => new AccessRole(r.RoleId, r.TenantId, r.Key, r.Name, r.IsBuiltin, r.HumanOnly,
            Array.AsReadOnly(permissions.Where(p => p.RoleId == r.RoleId).Select(p => p.PermissionKey).Order(StringComparer.Ordinal).ToArray()))).ToArray());
    }
    private static async Task<TenantSecurityState?> ReadTenantAsync(AgentCoreDbContext db, Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenantId, ct).ConfigureAwait(false);
        if (tenant is null) return null;
        return IamStateRules.Freeze(new(tenant,
            await db.Set<TenantUser>().AsNoTracking().Where(u => u.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false),
            await db.Set<Principal>().AsNoTracking().Where(p => p.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false),
            (await db.Set<IamSystemIdentityRow>().AsNoTracking().Where(i => i.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false)).Select(IamPersistenceModel.Domain).ToArray(),
            await RolesAsync(db, tenantId, ct).ConfigureAwait(false),
            (await db.Set<IamAssignmentRow>().AsNoTracking().Where(a => a.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false)).Select(IamPersistenceModel.Domain).ToArray(),
            await db.Set<TenantInvitation>().AsNoTracking().Where(i => i.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false))
            { OperationPolicies = await db.Set<TenantOperationPolicy>().AsNoTracking().Where(p => p.TenantId == tenantId).ToArrayAsync(ct).ConfigureAwait(false) });
    }
    private static async Task ApplyAsync<T, TKey>(AgentCoreDbContext db, IQueryable<T> query, IReadOnlyList<T> next,
        Func<T, TKey> key, CancellationToken ct) where T : class where TKey : notnull
    {
        var current = (await query.ToArrayAsync(ct).ConfigureAwait(false)).ToDictionary(key);
        var nextKeys = next.Select(key).ToHashSet();
        foreach (var row in current.Values.Where(row => !nextKeys.Contains(key(row)))) db.Remove(row);
        foreach (var row in next)
            if (current.TryGetValue(key(row), out var old)) db.Entry(old).CurrentValues.SetValues(row);
            else db.Add(row);
    }
}
