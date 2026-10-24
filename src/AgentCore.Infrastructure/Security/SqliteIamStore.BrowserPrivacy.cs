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
        _ = await RequireSystemHumanAsync(db, human, "system.governance.manage", ct).ConfigureAwait(false);
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
