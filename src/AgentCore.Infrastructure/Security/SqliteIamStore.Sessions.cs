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
                var runIds = await db.AgentRuns.Where(r => r.TenantId == root.TenantId && r.SessionId == root.SessionId).Select(r => r.AgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
                var artifactIds = await db.Artifacts.Where(a => a.TenantId == root.TenantId && a.SessionId == root.SessionId).Select(a => a.ArtifactId).ToArrayAsync(ct).ConfigureAwait(false);
                if (next.Assignments.Any(a => a.Scope == TenantSessionRules.Scope(root.TenantId, change.SessionId)
                    || TenantRunRules.TryId(a.Scope, out var runId) && runIds.Contains(runId.ToString("D"))
                    || TenantArtifactRules.TryId(a.Scope, out var artifact) && artifactIds.Contains(artifact.ToString("D"))))
                    throw new ArgumentException("Deleting a Session must retire its direct, Run and Artifact grants.");
            }
            if (change.Action == TenantSessionAction.Resume)
            {
                var parent = await OwnedAgents(db, root.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false);
                if (parent is null) return false;
                if (parent.Instance.Lifecycle != "Active") throw new AccessDeniedException("agent_archived");
                TenantSessionRules.RequireExecutor((await ReadTenantAsync(db, root.TenantId, ct).ConfigureAwait(false))!, Owned(parent));
            }
            var value = TenantSessionMutations.Apply(SqliteMemoryStore.ToSnapshot(row, []), change, at);
            SqliteMemoryStore.ApplySession(row, value);
            if (change.Action == TenantSessionAction.Delete)
                foreach (var entry in await db.Entries.Where(e => e.SessionId == root.SessionId).ToArrayAsync(ct).ConfigureAwait(false)) RedactSessionEntry(entry);
            saved = new(root.Domain(), value); return true;
        }, ct).ConfigureAwait(false);
        return written ? saved : null;
    }
    private static void RedactSessionEntry(EntryRecord entry)
    {
        entry.Text = ""; entry.UserPartsJson = null; entry.EnvelopeJson = null; entry.FailureReferenceJson = null; entry.AttachmentRefsJson = null;
        entry.SourceAdmissionFingerprint = null; entry.FinishReason = null; entry.InterruptReason = null;
        entry.ModelCatalogKey = null; entry.ModelProviderAlias = null; entry.ModelId = null; entry.ModelReasoningEffort = null;
        entry.HeardTextEndExclusive = 0; entry.ReceivedTextEndExclusive = 0;
        if (entry.Status == nameof(EntryStatus.Streaming)) entry.Status = nameof(EntryStatus.Interrupted);
    }
    private static IQueryable<TenantSessionRecord> OwnedSessions(AgentCoreDbContext db, Guid tenant) => db.Set<TenantSessionRecord>().Where(s => s.TenantId == tenant
        && db.Sessions.Any(row => row.SessionId == s.SessionId && row.DurablyDeletedAtUtc == null));
    public async ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, tenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId.ToString("D"), ct).ConfigureAwait(false);
        return root is null ? null : await SessionView(db, root, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true)
    {
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedSessions(db, tenantId).AsNoTracking();
        if (allowedIds is not null) { var ids = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(s => ids.Contains(s.SessionId)); }
        if (!includeArchived) query = query.Where(s => db.Sessions.Any(row => row.SessionId == s.SessionId && row.ArchivedAtUtc == null));
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var roots = await query.OrderBy(s => s.SessionId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
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
            TenantSessionRules.ValidateCreation(owner, snapshot, Owned(agent), initiator, audit);
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
