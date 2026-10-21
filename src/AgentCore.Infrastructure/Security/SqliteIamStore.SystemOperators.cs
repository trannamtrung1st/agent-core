using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ISystemOperatorStore
{
    public async ValueTask<SystemOperatorPage> QuerySystemOperatorsAsync(SystemCollectionQuery query, bool? isOperator, CancellationToken ct = default)
    {
        query = SystemAdministrationRules.Normalize(query); await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var system = await ReadSystemAsync(db, ct); var role = SystemOperatorRules.Role(system);
        var operatorAccounts = from a in db.Set<IamAssignmentRow>() join p in db.Set<Principal>() on a.PrincipalId equals p.PrincipalId
            where a.TenantId == null && a.RoleId == role.RoleId && a.ScopeKind == AccessScopeKind.System && a.ResourceType == "" && a.ResourceId == ""
                && p.TenantId == null && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && p.UserId == null && p.SystemIdentityId == null
            select p.AccountId;
        var rows = db.Set<Account>().AsNoTracking().Where(a => query.Status == null || a.Status == query.Status);
        if (query.Search is { } search) rows = rows.Where(a => a.DisplayName.Contains(search) || a.LoginKey.Contains(search));
        if (isOperator is { } hasRole) rows = rows.Where(a => operatorAccounts.Contains(a.AccountId) == hasRole);
        var total = await rows.CountAsync(ct); var page = await rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.AccountId).Skip(query.Page * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        var result = new SystemOperatorPage(page.Select(a => SystemOperatorRules.Summary(system, a)).ToArray(), total, query.Page, query.PageSize); await tx.CommitAsync(ct); return result;
    }
    public async ValueTask<bool> TrySetSystemOperatorAsync(HumanSessionReference human, Guid accountId, string expectedVersion, bool enabled,
        Guid assignmentId, string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        SystemAdministrationRules.AccountStatus(expectedVersion, IdentityStatus.Active);
        SystemAdministrationRules.Audit(human, audit, enabled ? "system.operators.assign" : "system.operators.revoke", accountId);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireSystemHumanAsync(db, human, "system.governance.manage", ct);
        var target = await db.Set<Account>().SingleOrDefaultAsync(a => a.AccountId == accountId, ct) ?? throw new AccessDeniedException("account_unavailable");
        var system = await ReadSystemAsync(db, ct);
        if (SystemOperatorRules.Version(system, target) != expectedVersion || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct)) return false;
        if (string.IsNullOrWhiteSpace(nextSecurityStamp) || nextSecurityStamp == target.SecurityStamp) throw new ArgumentException("Operator changes must rotate the Account security stamp.");
        var next = SystemOperatorRules.Change(system, await db.Set<Account>().AsNoTracking().ToArrayAsync(ct), target, enabled, assignmentId, human, audit.At);
        var old = SystemOperatorRules.Assignments(system, target);
        if (enabled) db.Add(IamPersistenceModel.Row(next.Assignments.Single(a => a.AssignmentId == assignmentId)));
        else { var removed = old.Select(a => a.AssignmentId).ToArray(); db.RemoveRange(await db.Set<IamAssignmentRow>().Where(a => removed.Contains(a.AssignmentId)).ToArrayAsync(ct)); }
        db.Entry(target).CurrentValues.SetValues(target with { SecurityStamp = nextSecurityStamp }); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
}
