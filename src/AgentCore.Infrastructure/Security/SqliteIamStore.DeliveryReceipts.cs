using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantDeliveryReceiptStore
{
    private async Task RequireReceiptHumanAsync(AgentCoreDbContext db, TenantDeliveryReceipt proof, CancellationToken ct)
    {
        await RequireHumanSessionAsync(db, proof.Owner.TenantId, proof.Human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, proof.Owner.TenantId, ct).ConfigureAwait(false);
        if (state?.Tenant.Revision != proof.SecurityRevision) throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask SaveTenantRuntimeReceiptAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RequireReceiptHumanAsync(db, proof, ct).ConfigureAwait(false);
        var (root, _) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var entry = snapshot.Entries.SingleOrDefault(e => e.EntryId == proof.EntryId) ?? throw new AccessDeniedException("receipt_unavailable");
        if (TenantDeliveryReceiptRules.Apply(proof, root.Domain(), entry) != entry) throw new AccessDeniedException("receipt_unavailable");
        await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, snapshot, expectedRevision, ct).ConfigureAwait(false);
        await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false);
    }
    public async ValueTask<long> SaveTenantQuiescentReceiptAsync(TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RequireReceiptHumanAsync(db, proof, ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, proof.Owner.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == proof.Owner.SessionId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("session_unavailable");
        var session = await db.Sessions.SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
        var row = await db.Entries.SingleOrDefaultAsync(e => e.SessionId == root.SessionId && e.EntryId == proof.EntryId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("receipt_unavailable");
        var entry = SqliteMemoryStore.ToEntry(row); var updated = TenantDeliveryReceiptRules.Apply(proof, root.Domain(), entry);
        if (updated == entry) return session.Revision;
        row.ReceivedTextEndExclusive = updated.ReceivedTextEndExclusive; session.Revision = checked(session.Revision + 1);
        await CommitReceiptAsync(db, tx, ct).ConfigureAwait(false); return session.Revision;
    }
    private static async Task CommitReceiptAsync(AgentCoreDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw new SecurityConflictException("stale_revision"); }
    }
}
