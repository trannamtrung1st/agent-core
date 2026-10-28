using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private sealed class RetentionDeadline
    {
        public string SessionId { get; set; } = "";
        public long DueAtUtc { get; set; }
    }
    private IQueryable<RetentionDeadline> DueSessionDeadlines(AgentCoreDbContext db, Guid? tenant = null, Guid? session = null)
    {
        var at = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(); var id = session?.ToString("D");
        // Metadata only. Inherited privacy expiry uses the incumbent quiescence,
        // effect-reconciliation and content-retirement owner, without a new runner.
        return db.Database.SqlQuery<RetentionDeadline>($"""
            WITH RECURSIVE lifetime(RootId,TenantId,SessionId,ParentId,Depth,DueAtUtc,FallbackAtUtc) AS (
                SELECT s.SessionId,o.TenantId,s.SessionId,
                    CASE WHEN json_extract(s.OriginJson,'$.kind') IN (5,6) THEN json_extract(s.OriginJson,'$.originatingSessionId') END,
                    0, CASE WHEN json_extract(s.OriginJson,'$.kind') IN (5,6)
                        AND json_extract(s.OriginJson,'$.originatingSessionId') IS NULL AND json_extract(s.OriginJson,'$.workflowExecutionId') IS NULL THEN s.CreatedAtUtc ELSE s.RetentionDueAtUtc END,
                    s.CreatedAtUtc
                FROM Sessions s JOIN TenantSessions o ON o.SessionId=s.SessionId
                WHERE s.DurablyDeletedAtUtc IS NULL AND ({tenant} IS NULL OR o.TenantId={tenant}) AND ({id} IS NULL OR s.SessionId={id})
                    AND (s.RetentionDueAtUtc<={at} OR json_extract(s.OriginJson,'$.kind') IN (5,6))
                UNION ALL
                SELECT a.RootId,a.TenantId,p.SessionId,
                    CASE WHEN po.SessionId IS NOT NULL AND json_extract(p.OriginJson,'$.kind') IN (5,6)
                        THEN json_extract(p.OriginJson,'$.originatingSessionId') END,
                    a.Depth+1,
                    CASE WHEN po.SessionId IS NULL OR a.Depth=63 AND json_extract(p.OriginJson,'$.kind') IN (5,6)
                            OR json_extract(p.OriginJson,'$.kind') IN (5,6) AND json_extract(p.OriginJson,'$.originatingSessionId') IS NULL AND json_extract(p.OriginJson,'$.workflowExecutionId') IS NULL
                        THEN a.FallbackAtUtc
                        WHEN p.DurablyDeletedAtUtc IS NULL THEN p.RetentionDueAtUtc
                        WHEN p.RetentionDueAtUtc IS NULL THEN p.DurablyDeletedAtUtc
                        ELSE min(p.RetentionDueAtUtc,p.DurablyDeletedAtUtc) END,
                    a.FallbackAtUtc
                FROM lifetime a LEFT JOIN Sessions p ON p.SessionId=a.ParentId
                    LEFT JOIN TenantSessions po ON po.SessionId=p.SessionId AND po.TenantId=a.TenantId
                WHERE a.ParentId IS NOT NULL AND a.Depth<64
            )
            SELECT RootId AS SessionId,min(DueAtUtc) AS DueAtUtc FROM lifetime GROUP BY RootId HAVING min(DueAtUtc)<={at}
            """);
    }
    public async ValueTask<TenantSessionRetentionTarget?> FindExpiredSessionAsync(Guid tenant, Guid session, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var due = await DueSessionDeadlines(db, tenant, session).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (due is null) return null;
        var owner = await db.Set<TenantSessionRecord>().AsNoTracking().SingleAsync(s => s.TenantId == tenant && s.SessionId == due.SessionId, ct).ConfigureAwait(false);
        var revision = await db.Sessions.Where(s => s.SessionId == due.SessionId).Select(s => s.Revision).SingleAsync(ct).ConfigureAwait(false);
        return new(owner.Domain(), revision, DateTimeOffset.FromUnixTimeMilliseconds(due.DueAtUtc));
    }
    public async ValueTask<IReadOnlyList<TenantSessionRetentionTarget>> ListDueSessionsAsync(TenantSessionRetentionTarget? after, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var due = after?.DueAtUtc.ToUnixTimeMilliseconds(); var key = after?.Owner.SessionId.ToString("D");
        var deadlines = await DueSessionDeadlines(db).Where(s => due == null || s.DueAtUtc > due || s.DueAtUtc == due && string.Compare(s.SessionId, key) > 0)
            .OrderBy(s => s.DueAtUtc).ThenBy(s => s.SessionId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<TenantSessionRetentionTarget>();
        foreach (var deadline in deadlines)
        {
            var owner = await db.Set<TenantSessionRecord>().AsNoTracking().SingleAsync(s => s.SessionId == deadline.SessionId, ct).ConfigureAwait(false);
            var revision = await db.Sessions.Where(s => s.SessionId == deadline.SessionId).Select(s => s.Revision).SingleAsync(ct).ConfigureAwait(false);
            result.Add(new(owner.Domain(), revision, DateTimeOffset.FromUnixTimeMilliseconds(deadline.DueAtUtc)));
        }
        return result;
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
        var deadline = await DueSessionDeadlines(db, owner.TenantId, target.Owner.SessionId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (current.Revision != target.Revision || deadline?.DueAtUtc != target.DueAtUtc.ToUnixTimeMilliseconds() || current.DurablyDeletedAt is not null) return false;
        var runRows = await db.AgentRuns.Where(r => r.TenantId == owner.TenantId && r.SessionId == owner.SessionId).ToArrayAsync(ct).ConfigureAwait(false);
        var allRuns = runRows.Select(AgentRunStoreMapping.ToDomain).ToArray();
        var approvalRows = await db.Set<TenantApprovalRecord>().Where(a => a.TenantId == owner.TenantId && a.SessionId == owner.SessionId).ToArrayAsync(ct).ConfigureAwait(false);
        var requestIds = approvalRows.Select(a => a.RequestId).ToArray();
        foreach (var grant in await db.Set<TenantOperationGrant>().Where(g => g.TenantId == owner.TenantId && requestIds.Contains(g.RequestId) && g.Status == OperationGrantStatus.Available).ToArrayAsync(ct).ConfigureAwait(false))
            db.Entry(grant).CurrentValues.SetValues(grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
        foreach (var approval in approvalRows.Where(a => a.Status == (int)TenantApprovalStatus.Pending))
            db.Entry(approval).CurrentValues.SetValues(TenantApprovalRecord.From(approval.Domain().Decide(now >= approval.Domain().ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, approval.Revision, now)));
        // Unknown effects remain recoverable. Persist expiry and factual cancellation,
        // but never redact their evidence or report successful execution.
        if (allRuns.Any(r => r.RequiresEffectReconciliation))
        {
            foreach (var runRow in runRows)
            {
                var run = AgentRunStoreMapping.ToDomain(runRow);
                if (!run.IsTerminal)
                {
                    var requested = TenantRunCancellationRules.Cancel(run, now);
                    AgentRunStoreMapping.Apply(runRow, requested); await TenantCoreEventProduction.StageRunAsync(db, run, requested, ct).ConfigureAwait(false);
                    await SettleTenantCompletion(db, requested, run.Claim?.Generation, ct).ConfigureAwait(false);
                }
            }
            if (!SessionLifecycle.IsTerminal(current.LifecycleStatus))
            {
                var expired = (SessionRetention.IsDue(current, now) ? TenantSessionMutations.ExpireDue(current, now) : TenantSessionMutations.ExpireInheritedSource(current, now)) with { Revision = checked(current.Revision + 1) };
                SqliteMemoryStore.ApplySession(row, expired);
                await TenantCoreEventProduction.StageSessionAsync(db, current, expired, owner.Domain(), null, ct).ConfigureAwait(false);
                db.Add(new AuthorizationAudit(auditId, owner.TenantId, Guid.Empty, "sessions.retention.expire", owner.SessionId, "allowed", "system_due_effect_reconciliation_pending", now));
            }
            await RetireRunContent(db, owner.TenantId, owner.SessionId, now, ct).ConfigureAwait(false);
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
        var next = SessionRetention.IsDue(current, now) ? TenantSessionMutations.RetireDue(current, now) : TenantSessionMutations.RetireInheritedSource(current, now);
        SqliteMemoryStore.ApplySession(row, next);
        await RetireRunContent(db, owner.TenantId, owner.SessionId, now, ct).ConfigureAwait(false);
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
