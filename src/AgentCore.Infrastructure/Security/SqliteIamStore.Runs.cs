using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantSessionRoot?> ResolveTenantSessionOwnerAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == sessionId.ToString("D"), ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<AgentRun?> TryCancelTenantRunAsync(TenantSecurityState next, long expectedSecurityRevision, Guid runId, long expectedRunRevision,
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (audit.ActorPrincipalId != initiator.PrincipalId || audit.Action != "agent-runs.cancel" || audit.Target != runId.ToString("D"))
            throw new ArgumentException("Invalid Run cancellation transaction.");
        AgentRun? saved = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            await RequireHumanSessionAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false);
            var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || row.Revision != expectedRunRevision) return false;
            if (await db.Sessions.AnyAsync(s => s.SessionId == row.SessionId && (s.Status == nameof(SessionStatus.Attached) || s.Status == nameof(SessionStatus.Ending)), ct).ConfigureAwait(false))
                throw new SecurityConflictException("session_in_use");
            var run = AgentRunStoreMapping.ToDomain(row);
            if (run.Status != AgentRunStatus.Queued) throw new SecurityConflictException("run_in_use");
            saved = run.RequestCancellation(run.Revision, null, at); AgentRunStoreMapping.Apply(row, saved); return true;
        }, ct).ConfigureAwait(false);
        return written ? saved : null;
    }
    private static IQueryable<AgentRunRecord> OwnedRuns(AgentCoreDbContext db, Guid tenant) => db.AgentRuns.Where(r => r.TenantId == tenant
        && db.Set<TenantSessionRecord>().Any(s => s.TenantId == tenant && s.SessionId == r.SessionId && s.AgentInstanceId == r.AgentInstanceId && s.ExecutorPrincipalId == r.ExecutorPrincipalId)
        && db.Sessions.Any(s => s.SessionId == r.SessionId && s.DurablyDeletedAtUtc == null));
    public async ValueTask<AgentRun?> FindTenantRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }
    public async ValueTask<IReadOnlyList<Guid>> ListTenantRunIdsAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var ids = await OwnedRuns(db, tenantId).Where(r => r.SessionId == sessionId.ToString("D")).Select(r => r.AgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
        return ids.Select(Guid.Parse).ToArray();
    }
    public async ValueTask<TenantRunAdmissionResult?> TryAdmitTenantHumanRunAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantHumanRunAdmission admission, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var run = admission.Run;
        if (run.Owner.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.Action != "agent-runs.admit" || audit.Target != run.AgentRunId.ToString("D")) throw new ArgumentException("Invalid Run admission transaction.");
        TenantRunAdmissionResult? result = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            await RequireHumanSessionAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false);
            var root = await OwnedSessions(db, next.Tenant.TenantId).SingleOrDefaultAsync(s => s.SessionId == run.SessionId.ToString("D"), ct).ConfigureAwait(false);
            var parent = await OwnedAgents(db, next.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (root is null || parent is null || parent.Instance.Revision != admission.ExpectedInstanceRevision) return false;
            var agent = Owned(parent);
            if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
            TenantSessionRules.RequireExecutor((await ReadTenantAsync(db, next.Tenant.TenantId, ct).ConfigureAwait(false))!, agent);
            var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
            var current = SqliteMemoryStore.ToSnapshot(row, []);
            var value = TenantRunRules.Admit(new(root.Domain(), current), admission, agent, initiator);
            var key = run.Admission.Activation.DedupeKey;
            var duplicate = await db.Activations.AsNoTracking().SingleOrDefaultAsync(a => a.SessionId == root.SessionId && a.DedupeKey == key, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                var saved = AgentRunStoreMapping.ToDomain(await OwnedRuns(db, next.Tenant.TenantId).AsNoTracking().SingleAsync(r => r.ActivationId == duplicate.ActivationId, ct).ConfigureAwait(false));
                var inputId = saved.Admission.Activation.SourceEntryIds.Single().ToString("D");
                var savedInput = await db.Entries.AsNoTracking().SingleAsync(e => e.EntryId == inputId && e.SessionId == root.SessionId, ct).ConfigureAwait(false);
                if (saved.Owner != run.Owner || saved.Admission.ExecutionContext?.OriginalInitiatorPrincipalId != initiator.PrincipalId || savedInput.Text != admission.Input.Text)
                    throw new SecurityConflictException("message_id_conflict");
                result = new(false, saved, Guid.Parse(inputId), current.Revision); return false;
            }
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status == (int)AgentRunStatus.Running, ct).ConfigureAwait(false)) throw new SecurityConflictException("session_in_use");
            if (await db.AgentRuns.CountAsync(r => r.SessionId == root.SessionId && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct).ConfigureAwait(false) >= 100)
                throw new SecurityConflictException("session_queue_full");
            SqliteMemoryStore.ApplySession(row, value); db.Entries.Add(SqliteMemoryStore.ToRecord(root.SessionId, admission.Input));
            db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(value, run)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
            db.ActivationSourceEntries.Add(new() { ActivationId = run.ActivationId.ToString("D"), SessionId = root.SessionId, EntryId = admission.Input.EntryId.ToString("D"), Ordinal = 0 });
            result = new(true, run, admission.Input.EntryId, value.Revision); return true;
        }, ct).ConfigureAwait(false);
        return written || result is { Created: false } ? result : null;
    }
}
