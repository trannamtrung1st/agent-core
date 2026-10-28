using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (audit.ActorPrincipalId != initiator.PrincipalId || audit.Target != change.SessionId.ToString("D")
            || audit.Action != TenantSessionMutations.AuditAction(change.Action)) throw new ArgumentException("Invalid Session mutation transaction.");
        TenantSessionRules.ValidateModelChange(next.Tenant.TenantId, change);
        TenantSession? saved = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            await RequireHumanSessionAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false);
            var root = await OwnedSessions(db, next.Tenant.TenantId).SingleOrDefaultAsync(s => s.SessionId == change.SessionId.ToString("D"), ct).ConfigureAwait(false);
            if (root is null) return false;
            var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
            if (row.Revision != change.ExpectedSessionRevision) return false;
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct).ConfigureAwait(false)) throw new SecurityConflictException("session_in_use");
            if (change.Action == TenantSessionAction.Delete)
            {
                var retainedRuns = await db.AgentRuns.Where(r => r.TenantId == root.TenantId && r.SessionId == root.SessionId).ToArrayAsync(ct).ConfigureAwait(false);
                if (retainedRuns.Select(AgentRunStoreMapping.ToDomain).Any(r => r.RequiresEffectReconciliation))
                    throw new SecurityConflictException("session_effect_reconciliation_pending");
                var runIds = await db.AgentRuns.Where(r => r.TenantId == root.TenantId && r.SessionId == root.SessionId).Select(r => r.AgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
                var attachmentIds = await db.Attachments.Where(a => a.TenantId == root.TenantId && a.SessionId == root.SessionId).Select(a => a.AttachmentId).ToArrayAsync(ct).ConfigureAwait(false);
                var artifactIds = await db.Artifacts.Where(a => a.TenantId == root.TenantId && a.SessionId == root.SessionId).Select(a => a.ArtifactId).ToArrayAsync(ct).ConfigureAwait(false);
                if (next.Assignments.Any(a => a.Scope == TenantSessionRules.Scope(root.TenantId, change.SessionId)
                    || TenantWorkspaceRules.BelongsToSession(a.Scope, change.SessionId)
                    || TenantRunRules.TryId(a.Scope, out var runId) && runIds.Contains(runId.ToString("D"))
                    || TenantAttachmentRules.TryId(a.Scope, out var attachment) && attachmentIds.Contains(attachment.ToString("D"))
                    || TenantArtifactRules.TryId(a.Scope, out var artifact) && artifactIds.Contains(artifact.ToString("D"))))
                    throw new ArgumentException("Deleting a Session must retire its direct, workspace, Run, Artifact and attachment grants.");
            }
            if (change.Action == TenantSessionAction.Resume)
            {
                var parent = await OwnedAgents(db, root.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false);
                if (parent is null) return false;
                if (parent.Instance.Lifecycle != "Active") throw new AccessDeniedException("agent_archived");
                TenantSessionRules.RequireExecutor((await ReadTenantAsync(db, root.TenantId, ct).ConfigureAwait(false))!, Owned(parent));
            }
            var previous = SqliteMemoryStore.ToSnapshot(row, []);
            var value = TenantSessionMutations.Apply(previous, change, at);
            SqliteMemoryStore.ApplySession(row, value);
            await TenantCoreEventProduction.StageSessionAsync(db, previous, value, root.Domain(), null, ct).ConfigureAwait(false);
            if (change.Action == TenantSessionAction.Delete)
            {
                await RetireRunContent(db, root.TenantId, root.SessionId, at, ct).ConfigureAwait(false);
                foreach (var entry in await db.Entries.Where(e => e.SessionId == root.SessionId).ToArrayAsync(ct).ConfigureAwait(false)) RedactSessionEntry(entry);
                var memories = await db.Set<TenantLearnedMemoryRecord>().Where(i => i.TenantId == root.TenantId && i.SessionId == root.SessionId && i.Scope == 0 && i.Status != 2).ToArrayAsync(ct).ConfigureAwait(false);
                foreach (var memory in memories) db.Entry(memory).CurrentValues.SetValues(memory with { Status = 2, Subject = "", Content = "", SubjectKey = "",
                    Revision = checked(memory.Revision + 1), UpdatedAtUtc = Math.Max(memory.UpdatedAtUtc, at.ToUnixTimeMilliseconds()) });
            }
            saved = new(root.Domain(), value); return true;
        }, ct).ConfigureAwait(false);
        return written ? saved : null;
    }
    private static void RedactSessionEntry(EntryRecord entry)
    {
        entry.Text = ""; entry.UserPartsJson = null; entry.EnvelopeJson = null; entry.FailureReferenceJson = null; entry.AttachmentRefsJson = null;
        entry.SourceAdmissionFingerprint = null; entry.FinishReason = null; entry.InterruptReason = null;
        entry.ModelOfferingId = null; entry.ModelConnectionId = null; entry.ExternalModelId = null; entry.ModelReasoningEffort = null;
        entry.HeardTextEndExclusive = 0; entry.ReceivedTextEndExclusive = 0;
        if (entry.Status == nameof(EntryStatus.Streaming)) entry.Status = nameof(EntryStatus.Interrupted);
    }
    private IQueryable<TenantSessionRecord> OwnedSessions(AgentCoreDbContext db, Guid tenant, string? rootSession = null)
    {
        var at = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        // Filter inherited privacy lifetimes before any page/count or source projection.
        // Only metadata is traversed; this grants no source Session read authority.
        return db.Set<TenantSessionRecord>().FromSqlInterpolated($"""
            WITH RECURSIVE lifetime(RootSessionId,SessionId,ParentId,Depth,Available) AS (
                SELECT o.SessionId,s.SessionId,
                    CASE WHEN json_extract(s.OriginJson,'$.kind') IN (5,6) THEN json_extract(s.OriginJson,'$.originatingSessionId') END,
                    0, CASE WHEN s.DurablyDeletedAtUtc IS NULL AND (s.RetentionDueAtUtc IS NULL OR s.RetentionDueAtUtc>{at})
                        AND (json_extract(s.OriginJson,'$.kind') NOT IN (5,6) OR json_extract(s.OriginJson,'$.originatingSessionId') IS NOT NULL
                            OR (json_extract(s.OriginJson,'$.kind')=5 AND EXISTS(SELECT 1 FROM TenantDelegatedTasks workflowTask
                                WHERE workflowTask.TenantId={tenant} AND workflowTask.WorkerSessionId=s.SessionId
                                    AND lower(workflowTask.WorkflowExecutionId)=json_extract(s.OriginJson,'$.workflowExecutionId')
                                    AND json_extract(workflowTask.Json,'$.Source.Workflow.OperationId')=json_extract(s.OriginJson,'$.workflowOperationId'))))
                        THEN 1 ELSE 0 END
                FROM TenantSessions o JOIN Sessions s ON s.SessionId=o.SessionId WHERE o.TenantId={tenant}
                    AND ({rootSession} IS NULL OR o.SessionId={rootSession})
                UNION ALL
                SELECT a.RootSessionId,p.SessionId,
                    CASE WHEN json_extract(p.OriginJson,'$.kind') IN (5,6) THEN json_extract(p.OriginJson,'$.originatingSessionId') END,
                    a.Depth+1, CASE WHEN po.SessionId IS NOT NULL AND p.DurablyDeletedAtUtc IS NULL
                        AND (p.RetentionDueAtUtc IS NULL OR p.RetentionDueAtUtc>{at})
                        AND (json_extract(p.OriginJson,'$.kind') NOT IN (5,6) OR json_extract(p.OriginJson,'$.originatingSessionId') IS NOT NULL
                            OR (json_extract(p.OriginJson,'$.kind')=5 AND EXISTS(SELECT 1 FROM TenantDelegatedTasks workflowTask
                                WHERE workflowTask.TenantId={tenant} AND workflowTask.WorkerSessionId=p.SessionId
                                    AND lower(workflowTask.WorkflowExecutionId)=json_extract(p.OriginJson,'$.workflowExecutionId')
                                    AND json_extract(workflowTask.Json,'$.Source.Workflow.OperationId')=json_extract(p.OriginJson,'$.workflowOperationId'))))
                        THEN 1 ELSE 0 END
                FROM lifetime a LEFT JOIN Sessions p ON p.SessionId=a.ParentId
                    LEFT JOIN TenantSessions po ON po.SessionId=p.SessionId AND po.TenantId={tenant}
                WHERE a.Available=1 AND a.ParentId IS NOT NULL AND a.Depth<64
            )
            SELECT o.* FROM TenantSessions o WHERE o.TenantId={tenant}
                AND EXISTS (SELECT 1 FROM lifetime a WHERE a.RootSessionId=o.SessionId AND a.Depth=0)
                AND NOT EXISTS (SELECT 1 FROM lifetime a WHERE a.RootSessionId=o.SessionId
                    AND (a.Available=0 OR a.Depth=64 AND a.ParentId IS NOT NULL))
            """);
    }
    public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, tenantId, sessionId.ToString("D")).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId.ToString("D"), ct).ConfigureAwait(false);
        return root is null ? null : await SessionView(db, root, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null, Guid? instanceId = null, bool recentFirst = false)
    {
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        if (surface is not null and not SessionSurface.ChatList and not SessionSurface.BackgroundWork) throw new ArgumentOutOfRangeException(nameof(surface));
        if (instanceId == Guid.Empty) throw new ArgumentException("Agent Instance is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedSessions(db, tenantId).AsNoTracking();
        if (allowedIds is not null) { var ids = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(s => ids.Contains(s.SessionId)); }
        if (!includeArchived) query = query.Where(s => db.Sessions.Any(row => row.SessionId == s.SessionId && row.ArchivedAtUtc == null));
        if (surface is { } selected) query = query.Where(s => db.Sessions.Any(row => row.SessionId == s.SessionId && (row.Surfaces & (int)selected) != 0
            && (selected != SessionSurface.ChatList || row.RetentionKind != (int)SessionRetentionKind.Temporary)));
        if (instanceId is { } instance) query = query.Where(s => s.AgentInstanceId == instance.ToString("D"));
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var ordered = recentFirst ? query.OrderByDescending(s => db.Sessions.Where(row => row.SessionId == s.SessionId).Select(row => row.UpdatedAtUtc).Single()).ThenBy(s => s.SessionId)
            : query.OrderBy(s => s.SessionId);
        var roots = await ordered.Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        var items = new List<TenantSession>(); foreach (var root in roots) items.Add(await SessionView(db, root, ct).ConfigureAwait(false));
        return new(items, total, page, pageSize);
    }
    public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner,
        SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (owner.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid tenant Session ownership.");
            await RequireHumanSessionAsync(db, owner.TenantId, initiator, ct).ConfigureAwait(false);
            var agent = await OwnedAgents(db, owner.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == owner.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (agent is null || agent.Instance.Revision != expectedInstanceRevision) return false;
            TenantSessionRules.RequireExecutor((await ReadTenantAsync(db, owner.TenantId, ct).ConfigureAwait(false))!, Owned(agent));
            if (snapshot.RetentionKind == SessionRetentionKind.Saved && snapshot.RetainAfterTerminalDays != next.Tenant.SavedChatRetentionDays)
                throw new ArgumentException("Saved Chat retention must pin the current tenant policy.");
            TenantSessionRules.ValidateCreation(owner, snapshot, Owned(agent), initiator, audit, (await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == owner.TenantId && s.OwnerInstanceId == owner.AgentInstanceId.ToString("D") && s.DeletedAt == null, ct).ConfigureAwait(false))?.Domain() ?? throw new AccessDeniedException("private_space_unavailable"));
            if (await db.Sessions.AnyAsync(s => s.SessionId == snapshot.SessionId.ToString("D"), ct).ConfigureAwait(false)
                || await db.Set<TenantSessionRecord>().AnyAsync(s => s.SessionId == snapshot.SessionId.ToString("D"), ct).ConfigureAwait(false)) return false;
            db.Sessions.Add(SqliteMemoryStore.ToRecord(snapshot));
            db.Add(TenantSessionRecord.Row(owner)); return true;
        }, ct);
    private static async Task<TenantSession> SessionView(AgentCoreDbContext db, TenantSessionRecord root, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
        return new(root.Domain(), SqliteMemoryStore.ToSnapshot(row, []));
    }
}
