using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(); var id = session.ToString("D");
        var row = await (from owner in db.Set<TenantSessionRecord>().AsNoTracking() join saved in db.Sessions.AsNoTracking() on owner.SessionId equals saved.SessionId
            where owner.TenantId == tenant && owner.SessionId == id && saved.RetentionDueAtUtc <= now
            select new { Owner = owner, saved.Revision, saved.RetentionDueAtUtc }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new(row.Owner.Domain(), row.Revision, DateTimeOffset.FromUnixTimeMilliseconds(row.RetentionDueAtUtc!.Value));
    }
    public async ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var due = after?.DueAtUtc.ToUnixTimeMilliseconds(); var key = after?.Owner.SessionId.ToString("D");
        var rows = await (from session in db.Sessions.AsNoTracking() join owner in db.Set<TenantSessionRecord>().AsNoTracking() on session.SessionId equals owner.SessionId
            where session.DurablyDeletedAtUtc == null && session.RetentionDueAtUtc <= now
                && (due == null || session.RetentionDueAtUtc > due || session.RetentionDueAtUtc == due && string.Compare(session.SessionId, key) > 0)
            orderby session.RetentionDueAtUtc, session.SessionId
            select new { Owner = owner, session.Revision, session.RetentionDueAtUtc }).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(row => new TenantSessionRetentionTarget(row.Owner.Domain(), row.Revision, DateTimeOffset.FromUnixTimeMilliseconds(row.RetentionDueAtUtc!.Value))).ToArray();
    }

    public async ValueTask<bool> RetireDueSessionAsync(TenantSessionRetentionTarget target, Guid auditId, CancellationToken ct = default)
    {
        if (auditId == Guid.Empty) throw new ArgumentException("Audit identity required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var owner = await db.Set<TenantSessionRecord>().SingleOrDefaultAsync(s => s.SessionId == target.Owner.SessionId.ToString("D") && s.TenantId == target.Owner.TenantId, ct).ConfigureAwait(false);
        if (owner?.Domain() != target.Owner) return false;
        var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == owner.SessionId, ct).ConfigureAwait(false);
        var current = SqliteMemoryStore.ToSnapshot(row, []); var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (current.Revision != target.Revision || current.RetentionDueAtUtc != target.DueAtUtc || current.DurablyDeletedAt is not null || !SessionRetention.IsDue(current, now)) return false;
        var runRows = await db.AgentRuns.Where(r => r.TenantId == owner.TenantId && r.SessionId == owner.SessionId).ToArrayAsync(ct).ConfigureAwait(false);
        var allRuns = runRows.Select(AgentRunStoreMapping.ToDomain).ToArray();
        var approvalRows = await db.Set<TenantApprovalRecord>().Where(a => a.TenantId == owner.TenantId && a.SessionId == owner.SessionId).ToArrayAsync(ct).ConfigureAwait(false);
        var requestIds = approvalRows.Select(a => a.RequestId).ToArray();
        foreach (var grant in await db.Set<TenantOperationGrant>().Where(g => g.TenantId == owner.TenantId && requestIds.Contains(g.RequestId) && g.Status == OperationGrantStatus.Available).ToArrayAsync(ct).ConfigureAwait(false))
            db.Entry(grant).CurrentValues.SetValues(grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
        foreach (var approval in approvalRows.Where(a => a.Status == (int)TenantApprovalStatus.Pending))
            db.Entry(approval).CurrentValues.SetValues(TenantApprovalRecord.From(approval.Domain().Decide(now >= approval.Domain().ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, approval.Revision, now)));
        // Unknown effects remain recoverable. Persist expiry and cancellation intent,
        // but never redact their evidence or report successful execution.
        if (allRuns.Any(r => r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
        {
            foreach (var runRow in runRows)
            {
                var run = AgentRunStoreMapping.ToDomain(runRow);
                if (!run.IsTerminal && !run.CancellationRequested)
                {
                    var requested = run.RequestCancellation(run.Revision, null, now);
                    AgentRunStoreMapping.Apply(runRow, requested); await TenantCoreEventProduction.StageRunAsync(db, run, requested, ct).ConfigureAwait(false);
                }
            }
            if (!SessionLifecycle.IsTerminal(current.LifecycleStatus))
            {
                var expired = TenantSessionMutations.ExpireDue(current, now) with { Revision = checked(current.Revision + 1) };
                SqliteMemoryStore.ApplySession(row, expired);
                await TenantCoreEventProduction.StageSessionAsync(db, current, expired, owner.Domain(), null, ct).ConfigureAwait(false);
                db.Add(new AuthorizationAudit(auditId, owner.TenantId, Guid.Empty, "sessions.retention.expire", owner.SessionId, "allowed", "system_due_effect_reconciliation_pending", now));
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return false;
        }
        foreach (var runRow in runRows)
        {
            var run = AgentRunStoreMapping.ToDomain(runRow); if (run.IsTerminal) continue;
            var cancelled = TenantRunCancellationRules.Cancel(run, now);
            AgentRunStoreMapping.Apply(runRow, cancelled);
            await TenantCoreEventProduction.StageRunAsync(db, run, cancelled, ct).ConfigureAwait(false);
            await SettleTenantCompletion(db, cancelled, run.Claim?.Generation, ct).ConfigureAwait(false);
        }
        var next = TenantSessionMutations.RetireDue(current, now);
        SqliteMemoryStore.ApplySession(row, next);
        await TenantCoreEventProduction.StageSessionAsync(db, current, next, owner.Domain(), null, ct).ConfigureAwait(false);
        foreach (var entry in await db.Entries.Where(e => e.SessionId == owner.SessionId).ToArrayAsync(ct).ConfigureAwait(false)) RedactSessionEntry(entry);
        foreach (var memory in await db.Set<TenantLearnedMemoryRecord>().Where(m => m.TenantId == owner.TenantId && m.SessionId == owner.SessionId && m.Scope == 0 && m.Status != 2).ToArrayAsync(ct).ConfigureAwait(false))
            db.Entry(memory).CurrentValues.SetValues(memory with { Status = 2, Subject = "", SubjectKey = "", Content = "", Revision = checked(memory.Revision + 1), UpdatedAtUtc = Math.Max(memory.UpdatedAtUtc, now.ToUnixTimeMilliseconds()) });
        var state = (await ReadTenantAsync(db, owner.TenantId, ct).ConfigureAwait(false))!;
        var attachmentIds = (await db.Attachments.Where(a => a.TenantId == owner.TenantId && a.SessionId == owner.SessionId).Select(a => a.AttachmentId).ToArrayAsync(ct).ConfigureAwait(false)).Select(Guid.Parse).ToHashSet();
        var artifactIds = (await db.Artifacts.Where(a => a.TenantId == owner.TenantId && a.SessionId == owner.SessionId).Select(a => a.ArtifactId).ToArrayAsync(ct).ConfigureAwait(false)).Select(Guid.Parse).ToHashSet();
        var updated = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) }, Assignments = TenantSessionMutations.RetireAssignments(state.Assignments, target.Owner,
            allRuns.Select(r => r.AgentRunId).ToHashSet(), attachmentIds, artifactIds) };
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(updated.Tenant); await ApplyTenantChildrenAsync(db, updated, ct).ConfigureAwait(false);
        db.Add(new AuthorizationAudit(auditId, owner.TenantId, Guid.Empty, "sessions.retention.delete", owner.SessionId, "allowed", "system_due_session_retired", now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
}
