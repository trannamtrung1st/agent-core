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
        TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, ConversationEntry? response = null)
    {
        if (audit.ActorPrincipalId != initiator.PrincipalId || audit.Action != "agent-runs.cancel" || audit.Target != runId.ToString("D"))
            throw new ArgumentException("Invalid Run cancellation transaction.");
        AgentRun? saved = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            await RequireHumanSessionAsync(db, next.Tenant.TenantId, initiator, ct).ConfigureAwait(false);
            var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || row.Revision != expectedRunRevision) return false;
            var run = AgentRunStoreMapping.ToDomain(row);
            saved = TenantRunCancellationRules.Cancel(run, at);
            var approval = run.ApprovalRequestId is { } id ? await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RequestId == id, ct).ConfigureAwait(false) : null;
            var request = approval?.Domain();
            var sourceRow = request is not null && request.RunId != runId ? await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false) : null;
            var closed = TenantRunCancellationRules.Approval(request, run, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow), at);
            if (closed is not null && closed != request)
            {
                db.Entry(approval!).CurrentValues.SetValues(TenantApprovalRecord.From(closed));
                if (closed.Status == TenantApprovalStatus.Cancelled)
                    db.Add(new TenantApprovalDecision(audit.AuditId, closed.RequestId, closed.TenantId, initiator.PrincipalId,
                        closed.Status, closed.Operation.ActionHash, closed.Revision, at));
            }
            if (run.ApprovalRequestId is { } approvalId)
            {
                var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == next.Tenant.TenantId && g.RequestId == approvalId, ct).ConfigureAwait(false);
                if (grant is { Status: OperationGrantStatus.Available }) db.Entry(grant).CurrentValues.SetValues(grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
            }
            var responseEntryId = response?.EntryId.ToString("D"); var responseSequence = response?.Sequence;
            var entries = await db.Entries.Where(e => e.SessionId == row.SessionId && (e.ResponseId == run.ResponseId!.Value.ToString("D")
                || responseEntryId != null && (e.EntryId == responseEntryId || e.EntrySequence == responseSequence))).ToArrayAsync(ct).ConfigureAwait(false);
            var beforeEntries = entries.Select(SqliteMemoryStore.ToEntry).ToArray();
            var values = TenantRunCancellationRules.ResponseEntries(beforeEntries, run, response, at);
            if (!values.SequenceEqual(beforeEntries))
            {
                foreach (var entry in values)
                {
                    var prior = entries.SingleOrDefault(e => e.EntryId == entry.EntryId.ToString("D"));
                    var mapped = SqliteMemoryStore.ToRecord(row.SessionId, entry);
                    if (prior is null) db.Entries.Add(mapped); else db.Entry(prior).CurrentValues.SetValues(mapped);
                }
                var session = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == row.SessionId, ct).ConfigureAwait(false);
                var snapshot = SqliteMemoryStore.ToSnapshot(session, []);
                SqliteMemoryStore.ApplySession(session, snapshot with { Revision = checked(snapshot.Revision + 1), UpdatedAt = at,
                    LastEntrySequence = Math.Max(snapshot.DurableLastEntrySequence, values.Max(e => e.Sequence)) });
            }
            AgentRunStoreMapping.Apply(row, saved); return true;
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
        admission = TenantAttachmentBindingRules.Freeze(admission); var run = admission.Run;
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
            var sources = admission.Attachments ?? [];
            var currentState = (await ReadTenantAsync(db, next.Tenant.TenantId, ct).ConfigureAwait(false))!;
            await TenantAttachmentBindingRules.PermissionsAsync(currentState, initiator.PrincipalId, run.SessionId, sources, new IamAuthorizer(this), ct).ConfigureAwait(false);
            var key = run.Admission.Activation.DedupeKey;
            var duplicate = await db.Activations.AsNoTracking().SingleOrDefaultAsync(a => a.SessionId == root.SessionId && a.DedupeKey == key, ct).ConfigureAwait(false);
            if (duplicate is not null)
            {
                var saved = AgentRunStoreMapping.ToDomain(await OwnedRuns(db, next.Tenant.TenantId).AsNoTracking().SingleAsync(r => r.ActivationId == duplicate.ActivationId, ct).ConfigureAwait(false));
                var inputId = saved.Admission.Activation.SourceEntryIds.Single().ToString("D");
                var savedInput = await db.Entries.AsNoTracking().SingleAsync(e => e.EntryId == inputId && e.SessionId == root.SessionId, ct).ConfigureAwait(false);
                if (!TenantRunRules.SameMessage(saved, SqliteMemoryStore.ToEntry(savedInput), admission))
                    throw new SecurityConflictException("message_id_conflict");
                result = new(false, saved, Guid.Parse(inputId), current.Revision); return false;
            }
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == root.SessionId && r.Status == (int)AgentRunStatus.Running, ct).ConfigureAwait(false)) throw new SecurityConflictException("session_in_use");
            if (await db.AgentRuns.CountAsync(r => r.SessionId == root.SessionId && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct).ConfigureAwait(false) >= 100)
                throw new SecurityConflictException("session_queue_full");
            var attachmentRows = new List<AttachmentRecordRow>(sources.Count);
            foreach (var source in sources)
                attachmentRows.Add(await SqliteAttachmentStore.Owned(db, next.Tenant.TenantId, run.SessionId)
                    .SingleOrDefaultAsync(a => a.AttachmentId == source.AttachmentId.ToString("D") && a.ContentDeletedAtUtc == null, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("attachment_unavailable"));
            TenantAttachmentBindingRules.Current(admission, attachmentRows.Select(SqliteAttachmentStore.Domain).ToArray(), (clock ?? TimeProvider.System).GetUtcNow());
            SqliteMemoryStore.ApplySession(row, value); db.Entries.Add(SqliteMemoryStore.ToRecord(root.SessionId, admission.Input));
            db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(value, run)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
            db.ActivationSourceEntries.Add(new() { ActivationId = run.ActivationId.ToString("D"), SessionId = root.SessionId, EntryId = admission.Input.EntryId.ToString("D"), Ordinal = 0 });
            if (attachmentRows.Count > 0)
            {
                // Entry must exist before the conditional same-Session bind trigger runs. Both saves share the outer IAM transaction.
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                foreach (var attachment in attachmentRows)
                {
                    attachment.State = nameof(AttachmentState.Bound); attachment.EntryId = admission.Input.EntryId.ToString("D");
                    attachment.ExpiresAtUtc = null; attachment.BoundAtUtc = admission.Input.CreatedAt.ToUnixTimeMilliseconds(); attachment.StageForNextTurn = false;
                    db.MessageAttachments.Add(new() { SessionId = root.SessionId, EntryId = attachment.EntryId, AttachmentId = attachment.AttachmentId, Ordinal = attachmentRows.IndexOf(attachment) });
                }
            }
            result = new(true, run, admission.Input.EntryId, value.Revision); return true;
        }, ct).ConfigureAwait(false);
        return written || result is { Created: false } ? result : null;
    }
}
