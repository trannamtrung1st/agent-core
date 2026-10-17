using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAuthorizationAuditStore
{
    public async ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantOperationAuditRules.Validate(audit); var e = audit.Execution!;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var revision = await db.Set<Tenant>().Where(t => t.TenantId == audit.TenantId).Select(t => (long?)t.Revision).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, e.Context.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == e.Context.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (revision is null || row is null || !TenantOperationAuditRules.Matches(audit, AgentRunStoreMapping.ToDomain(row), revision.Value)) return false;
        var existing = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == audit.AuditId, ct).ConfigureAwait(false);
        if (existing is not null) return existing == audit;
        db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
}
