using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantRequestIsolationStore
{
    public async ValueTask<bool> HasTenantRequestBoundaryAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await OwnedSessions(db, owner.TenantId).AsNoTracking().SingleOrDefaultAsync(o => o.SessionId == owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if (root?.Domain() != owner) return false;
        var session = await SessionView(db, root, ct).ConfigureAwait(false);
        var query = OwnedRuns(db, owner.TenantId).AsNoTracking().Where(r => r.SessionId == root.SessionId);
        if (AgentCore.Application.Sessions.RequestIsolation.Required(session.Snapshot.Definition)) return await query.AnyAsync(ct).ConfigureAwait(false);
        // Only the rare Session created before a stricter Instance pin needs retained policy inspection.
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
            if (AgentRunStoreMapping.ToDomain(row).Admission.Configuration is { } pin && AgentCore.Application.Sessions.RequestIsolation.Required(pin.Definition)) return true;
        return false;
    }
    public async ValueTask<TenantRequestIsolationPage> ListTenantRequestIsolationAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var cursor = after?.ToString("D");
        var roots = await db.Set<TenantSessionRecord>().AsNoTracking().Where(o => db.Sessions.Any(s => s.SessionId == o.SessionId
            && s.DurablyDeletedAtUtc == null && s.Snapshot!.LifecycleStatus == "Active") && (cursor == null || string.Compare(o.SessionId, cursor) > 0))
            .OrderBy(o => o.SessionId).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        return new(roots.Take(limit).Select(o => o.Domain()).ToArray(), roots.Length > limit ? Guid.Parse(roots[limit - 1].SessionId) : null);
    }
    private async ValueTask<(TenantSessionSettlement Graph, SandboxLease? Lease)?> SessionSettlement(AgentCoreDbContext db, TenantSessionRoot owner, CancellationToken ct)
    {
        var id = owner.SessionId.ToString("D"); var tenant = owner.TenantId;
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == tenant && o.SessionId == id, ct).ConfigureAwait(false);
        if (root?.Domain() != owner) return null;
        var session = await SessionView(db, root, ct).ConfigureAwait(false);
        var runs = (await OwnedRuns(db, tenant).AsNoTracking().Where(r => r.SessionId == id).OrderBy(r => r.AgentRunId).Take(101).ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain).ToArray();
        // This bounded metadata prefilter is deliberately broader than the canonical
        // origin comparison below; a full page remains incomplete, never "no children".
        var childIds = db.Sessions.Where(s => s.OriginJson.Contains(id)).Select(s => s.SessionId);
        var childRows = await db.Set<TenantSessionRecord>().AsNoTracking().Where(o => o.TenantId == tenant && childIds.Contains(o.SessionId)).OrderBy(o => o.SessionId).Take(101).ToArrayAsync(ct).ConfigureAwait(false);
        var children = new List<TenantSession>(); foreach (var child in childRows)
        {
            var view = await SessionView(db, child, ct).ConfigureAwait(false);
            if (view.Snapshot.Origin.OriginatingSessionId == owner.SessionId) children.Add(view);
        }
        var selected = children.Select(c => c.Owner.SessionId.ToString("D")).ToArray();
        var childRuns = (await OwnedRuns(db, tenant).AsNoTracking().Where(r => selected.Contains(r.SessionId)).OrderBy(r => r.AgentRunId).Take(1001).ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain).ToArray();
        var receipts = (await db.BackgroundCompletionReceipts.AsNoTracking().Where(r => r.ParentSessionId == id && r.ProfileId == null
            && db.AgentRuns.Any(run => run.TenantId == tenant && run.AgentRunId == r.ChildAgentRunId)).OrderBy(r => r.ChildAgentRunId).Take(1001).ToArrayAsync(ct).ConfigureAwait(false)).Select(CompletionInboxMapping.Read).ToArray();
        var leaseRow = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.SessionId == id, ct).ConfigureAwait(false);
        var lease = leaseRow is null ? null : SandboxRuntimePersistence.Value(leaseRow);
        if (lease is not null && !TenantRequestIsolationRules.LeaseOwner(owner, lease)) throw new AccessDeniedException("sandbox_owner_mismatch");
        var tasks = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == tenant && t.SourceSessionId == id).OrderBy(t => t.TaskId).Take(101).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        var notices = (await db.Set<TaskNotificationRow>().AsNoTracking().Where(n => n.TenantId == tenant && n.RecipientSessionId == id).OrderBy(n => n.NotificationId).Take(1001).ToArrayAsync(ct))
            .Select(n => System.Text.Json.JsonSerializer.Deserialize<TaskNotification>(n.Json)!).ToArray();
        return (new TenantSessionSettlement(session, runs, children, childRuns, receipts, childRows.Length <= 100) { Tasks = tasks, TaskNotifications = notices }, lease);
    }
    private async ValueTask<TenantRequestIsolationState?> RequestIsolation(AgentCoreDbContext db, TenantSessionRoot owner, CancellationToken ct)
    {
        var state = await SessionSettlement(db, owner, ct).ConfigureAwait(false);
        if (state is null) return null;
        var graph = state.Value.Graph;
        return new(graph.Session, graph.Complete && TenantRequestIsolationRules.Ready(graph.Session, graph.Runs, graph.Children, graph.ChildRuns, graph.Receipts, graph.Tasks, graph.TaskNotifications), state.Value.Lease);
    }
    public async ValueTask<TenantRequestIsolationState?> ReadTenantRequestIsolationAsync(TenantSessionRoot owner, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        return await RequestIsolation(db, owner, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> RequestTenantIsolationRetirementAsync(TenantSessionRoot owner, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await RequestIsolation(db, owner, ct).ConfigureAwait(false);
        if (state?.Ready != true || state.Session.Snapshot.Revision != expectedRevision || state.Lease is not { } lease) return false;
        if (lease.State is not (SandboxLeaseState.Terminated or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation))
        {
            var row = await db.Set<SandboxLeaseRow>().SingleAsync(l => l.SessionId == owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
            db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Terminating, CleanupReason = "requestCompleted" }));
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true;
    }
    public async ValueTask<bool> CompleteTenantIsolatedRequestAsync(TenantSessionRoot owner, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await RequestIsolation(db, owner, ct).ConfigureAwait(false);
        if (state?.Ready != true || state.Session.Snapshot.Revision != expectedRevision || state.Lease is { State: not SandboxLeaseState.Terminated }) return false;
        var next = LifecycleTransition.Apply(state.Session.Snapshot, SessionLifecycleStatus.Completed, LifecycleTransitionSource.System, at, "request_completed")
            with { Status = SessionStatus.Ended, PendingMode = null, Revision = checked(expectedRevision + 1), UpdatedAt = at };
        var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == owner.SessionId.ToString("D"), ct).ConfigureAwait(false);
        SqliteMemoryStore.ApplySession(row, next); await TenantCoreEventProduction.StageSessionAsync(db, state.Session.Snapshot, next, owner, null, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
}
