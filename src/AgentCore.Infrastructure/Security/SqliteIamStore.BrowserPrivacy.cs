using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ISystemBrowserPrivacyStore
{
    public async ValueTask<bool> TrySaveSystemBrowserPrivacyAsync(HumanSessionReference human, SystemBrowserPrivacyChange change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        BrowserPrivacyRules.ValidateAudit(human, audit);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var account = await (from session in db.Set<AuthenticationSession>() join a in db.Set<Account>() on session.AccountId equals a.AccountId
            join p in db.Set<Principal>() on a.AccountId equals p.AccountId
            where session.SessionId == human.AuthenticationSessionId && a.AccountId == human.AccountId && a.Status == IdentityStatus.Active
                && session.RevokedAt == null && session.ExpiresAt > now && session.SecurityStamp == a.SecurityStamp
                && p.PrincipalId == human.PrincipalId && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && p.TenantId == null && p.UserId == null && p.SystemIdentityId == null
            select a).AsNoTracking().SingleOrDefaultAsync(ct);
        if (account is null) throw new AccessDeniedException("initiator_unavailable");
        if (!(await BrowserPrivacyRules.SystemDecision(await ReadSystemAsync(db, ct), account, human.PrincipalId, new IamAuthorizer(this), ct)).Allowed)
            throw new AccessDeniedException("permission_denied");
        var policy = BrowserPrivacyRules.Prepare(change, privacyAuthority);
        var row = await db.BrowserPrivacy.SingleOrDefaultAsync(ct);
        if ((row?.Revision ?? 0) != change.ExpectedRevision || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == audit.AuditId, ct)) return false;
        if (row is null) { row = new() { Id = 1 }; db.BrowserPrivacy.Add(row); }
        row.Revision = policy.Revision; row.PolicyJson = JsonSerializer.Serialize(policy); db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
}
