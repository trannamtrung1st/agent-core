using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ISystemAdministrationStore
{
    private async ValueTask<Account> RequireAccountHumanAsync(AgentCoreDbContext db, HumanSessionReference human, CancellationToken ct)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var account = await (from session in db.Set<AuthenticationSession>() join a in db.Set<Account>() on session.AccountId equals a.AccountId
            join p in db.Set<Principal>() on a.AccountId equals p.AccountId
            where session.SessionId == human.AuthenticationSessionId && a.AccountId == human.AccountId && a.Status == IdentityStatus.Active
                && session.RevokedAt == null && session.ExpiresAt > now && session.SecurityStamp == a.SecurityStamp
                && p.PrincipalId == human.PrincipalId && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && p.TenantId == null && p.UserId == null && p.SystemIdentityId == null
            select a).AsNoTracking().SingleOrDefaultAsync(ct);
        if (account is null) throw new AccessDeniedException("initiator_unavailable");
        return account;
    }
    private async ValueTask<Account> RequireSystemHumanAsync(AgentCoreDbContext db, HumanSessionReference human, string permission, CancellationToken ct)
    {
        var account = await RequireAccountHumanAsync(db, human, ct).ConfigureAwait(false);
        if (!(await SystemAdministrationRules.Decision(await ReadSystemAsync(db, ct), account, human.PrincipalId, permission, new IamAuthorizer(this), ct)).Allowed)
            throw new AccessDeniedException("permission_denied");
        return account;
    }
    public async ValueTask<SystemAccountPage> QuerySystemAccountsAsync(SystemCollectionQuery query, CancellationToken ct = default)
    {
        query = SystemAdministrationRules.Normalize(query); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = db.Set<Account>().AsNoTracking().Where(a => query.Status == null || a.Status == query.Status);
        if (query.Search is { } search) rows = rows.Where(a => a.DisplayName.Contains(search) || a.LoginKey.Contains(search));
        var total = await rows.CountAsync(ct); var page = await rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.AccountId).Skip(query.Page * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(page.Select(SystemAdministrationRules.Summary).ToArray(), total, query.Page, query.PageSize);
    }
    public async ValueTask<SystemTenantPage> QuerySystemTenantsAsync(SystemCollectionQuery query, CancellationToken ct = default)
    {
        query = SystemAdministrationRules.Normalize(query); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = db.Set<Tenant>().AsNoTracking().Where(t => query.Status == null || t.Status == query.Status);
        if (query.Search is { } search) rows = rows.Where(t => t.Name.Contains(search) || t.Slug.Contains(search));
        var total = await rows.CountAsync(ct); var page = await rows.OrderBy(t => t.CreatedAt).ThenBy(t => t.TenantId).Skip(query.Page * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(page, total, query.Page, query.PageSize);
    }
    public async ValueTask<bool> TryCreateSystemTenantAsync(HumanSessionReference human, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
    {
        tenant = IamStateRules.Freeze(tenant); SystemAdministrationRules.NewTenant(human, tenant);
        SystemAdministrationRules.Audit(human, audit, "system.tenants.create", tenant.Tenant.TenantId, tenant.Tenant.TenantId);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireSystemHumanAsync(db, human, "system.tenants.create", ct);
        if (!await db.Set<IamBootstrapRow>().AnyAsync(ct)) return false;
        await EnsureActiveAccountOwnerAsync(db, tenant, null, ct); await EnsureResourceOwnershipAsync(db, tenant, ct);
        db.Add(tenant.Tenant); AddTenantChildren(db, tenant); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
    public async ValueTask<bool> TrySetSystemAccountStatusAsync(HumanSessionReference human, Guid accountId, string expectedVersion, IdentityStatus status,
        string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        SystemAdministrationRules.AccountStatus(expectedVersion, status); SystemAdministrationRules.Audit(human, audit, "system.accounts.status", accountId);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireSystemHumanAsync(db, human, "system.accounts.manage", ct);
        var account = await db.Set<Account>().SingleOrDefaultAsync(a => a.AccountId == accountId, ct) ?? throw new AccessDeniedException("account_unavailable");
        if (SystemAdministrationRules.Version(account) != expectedVersion || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct)) return false;
        if (string.IsNullOrWhiteSpace(nextSecurityStamp) || nextSecurityStamp == account.SecurityStamp) throw new ArgumentException("Account status must rotate its security stamp.");
        var next = account with { Status = status, SecurityStamp = nextSecurityStamp };
        if (status != IdentityStatus.Active)
        {
            SystemAdministrationRules.EnsureSystemOperator(await ReadSystemAsync(db, ct), await db.Set<Account>().AsNoTracking().ToArrayAsync(ct), next);
            var tenantIds = await db.Set<TenantUser>().Where(u => u.AccountId == accountId).Select(u => u.TenantId).ToArrayAsync(ct);
            foreach (var id in tenantIds) if (await ReadTenantAsync(db, id, ct) is { } state) await EnsureActiveAccountOwnerAsync(db, state, next, ct);
        }
        db.Entry(account).CurrentValues.SetValues(next); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
    public async ValueTask<bool> TrySetSystemTenantStatusAsync(HumanSessionReference human, Guid tenantId, long expectedRevision, IdentityStatus status,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        SystemAdministrationRules.TenantStatus(expectedRevision, status); SystemAdministrationRules.Audit(human, audit, "system.tenants.status", tenantId);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireSystemHumanAsync(db, human, "system.tenants.manage", ct);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == tenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (tenant.Revision != expectedRevision || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct)) return false;
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Status = status, Revision = checked(expectedRevision + 1) }); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
}
