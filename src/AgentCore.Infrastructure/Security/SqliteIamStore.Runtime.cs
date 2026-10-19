using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantRuntimeStore
{
    private async Task<(TenantSessionRecord Root, AgentRun Run)> RequireRuntimeFrame(AgentCoreDbContext db, TenantRuntimeFrame frame, CancellationToken ct)
    {
        TenantRuntimeRules.RequireOwner(frame.Owner);
        var row = await OwnedRuns(db, frame.Owner.TenantId!.Value).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == frame.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("run_unavailable");
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == frame.SessionId.ToString("D") && s.TenantId == frame.Owner.TenantId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("session_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row); TenantRuntimeRules.Require(frame, root.Domain(), run, (clock ?? TimeProvider.System).GetUtcNow());
        var state = await ReadTenantAsync(db, root.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        var agent = await OwnedAgents(db, root.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == root.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var value = Owned(agent); if (value.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, value); return (root, run);
    }
    public async ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (root, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var row = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleAsync(s => s.SessionId == root.SessionId, ct).ConfigureAwait(false);
        var sources = TenantApprovalContinuationRules.Inputs(run).Select(id => id.ToString("D")).ToArray();
        var boundary = await TenantInputBoundaryAsync(db, run, ct).ConfigureAwait(false);
        var entries = await db.Entries.AsNoTracking().Where(e => e.SessionId == root.SessionId && (e.EntrySequence <= boundary || e.ResponseId == run.ResponseId!.Value.ToString("D"))).OrderByDescending(e => e.EntrySequence).Take(40).ToArrayAsync(ct).ConfigureAwait(false);
        var current = await db.Entries.AsNoTracking().Where(e => e.SessionId == root.SessionId && sources.Contains(e.EntryId)).ToArrayAsync(ct).ConfigureAwait(false);
        return SqliteMemoryStore.ToSnapshot(row, entries.Concat(current).DistinctBy(e => e.EntryId).OrderBy(e => e.EntrySequence).ToArray());
    }
    public async ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, snapshot, expectedRevision, ct).ConfigureAwait(false);
        await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Stale Session revision."); }
    }
    public async ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (root, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false); limit = Math.Clamp(limit, 1, 1000);
        var boundary = await TenantInputBoundaryAsync(db, run, ct).ConfigureAwait(false);
        var query = db.Entries.AsNoTracking().Where(e => e.SessionId == root.SessionId && (e.EntrySequence <= boundary || e.ResponseId == run.ResponseId!.Value.ToString("D")));
        if (after is { } sequence) return HistoryPaging.FromForward((await query.Where(e => e.EntrySequence > sequence).OrderBy(e => e.EntrySequence).Take(limit).ToArrayAsync(ct).ConfigureAwait(false)).Select(SqliteMemoryStore.ToEntry).ToArray(), sequence, limit);
        var rows = await query.Where(e => before == null || e.EntrySequence < before).OrderByDescending(e => e.EntrySequence).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        return HistoryPaging.FromNewestFirst(rows.Select(SqliteMemoryStore.ToEntry).ToArray(), limit);
    }
    private static async Task<long> TenantInputBoundaryAsync(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        if (TenantBackgroundCompletionFactory.InputBoundary(run) is { } reportBoundary) return reportBoundary;
        var sources = TenantApprovalContinuationRules.Inputs(run).Select(id => id.ToString("D")).ToArray();
        var inputs = await db.Entries.Where(e => e.SessionId == run.SessionId.ToString("D") && sources.Contains(e.EntryId))
            .Select(e => new { e.EntrySequence, e.Role, e.Status }).ToArrayAsync(ct).ConfigureAwait(false);
        if (inputs.Length == 0 || inputs.Length != sources.Length || inputs.Any(e => e.Role != nameof(ConversationRole.User) || e.Status != nameof(EntryStatus.Completed)))
            throw new AccessDeniedException("tenant_runtime_inputs_unavailable");
        return inputs.Max(e => e.EntrySequence);
    }
    public async ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var millis = at.ToUnixTimeMilliseconds();
        var rows = await db.AgentRuns.AsNoTracking().Where(r => r.TenantId != null && !db.Set<TenantApprovalRecord>().Any(a => a.TenantId == r.TenantId && a.RunId == r.AgentRunId && a.Status == (int)TenantApprovalStatus.Pending)
            && db.Set<Tenant>().Any(t => t.TenantId == r.TenantId && t.Status == IdentityStatus.Active)
            && db.Set<TenantSessionRecord>().Any(s => s.SessionId == r.SessionId && s.TenantId == r.TenantId)
            && db.Sessions.Any(s => s.SessionId == r.SessionId && s.DurablyDeletedAtUtc == null && s.ArchivedAtUtc == null)
            && (r.Status == (int)AgentRunStatus.Queued || r.Status == (int)AgentRunStatus.WaitingToRetry && r.NextRetryAtUtc <= millis
                || r.Status == (int)AgentRunStatus.Running && r.LeaseExpiresAtUtc <= millis))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => db.ActivationSourceEntries.Where(e => e.ActivationId == r.ActivationId).Join(db.Entries, source => source.EntryId, entry => entry.EntryId, (source, entry) => (long?)entry.EntrySequence).Min() ?? long.MaxValue)
            .ThenBy(r => r.AgentRunId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).Where(r => r.Admission.Activation.Kind is ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted).ToArray();
    }
    public async ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null)
    {
        TenantRuntimeRules.RequireOwner(owner);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, owner.TenantId!.Value).SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Run was not found.");
        var run = AgentRunStoreMapping.ToDomain(row); if (run.Owner != owner) throw AgentCoreErrors.NotFound("Run was not found.");
        if (runtimeGeneration is { } generation && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        if (command is AgentRunCommand.Claim)
        {
            if (run.Admission.Activation.Kind is not (ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted)) throw AgentCoreErrors.Conflict("Canonical tenant continuation service required.");
            _ = await TenantExecutionLineage.RootAsync(run, this, ct).ConfigureAwait(false);
            if (run.ApprovalRequestId is { } approvalId)
            {
                var request = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(a => a.RequestId == approvalId && a.TenantId == owner.TenantId, ct).ConfigureAwait(false);
                var parentRow = run.Admission.ApprovalResume is { } resume ? await OwnedRuns(db, owner.TenantId!.Value).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == resume.SourceRunId.ToString("D"), ct).ConfigureAwait(false) : null;
                TenantRunContinuationRules.Require(run, request?.Domain(), parentRow is null ? null : AgentRunStoreMapping.ToDomain(parentRow));
            }
            if (!await db.Set<Tenant>().AnyAsync(t => t.TenantId == owner.TenantId && t.Status == IdentityStatus.Active, ct).ConfigureAwait(false)
                || !await db.Set<Principal>().AnyAsync(p => p.PrincipalId == owner.ExecutorPrincipalId && p.TenantId == owner.TenantId && p.Status == IdentityStatus.Active, ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Executor unavailable.");
            if (run.Admission.OutputContract == AgentRunOutputContract.CompletionReport && await db.AgentRuns.AnyAsync(r => r.SessionId == row.SessionId && r.AgentRunId != row.AgentRunId
                && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Failed && r.Status != (int)AgentRunStatus.Cancelled, ct).ConfigureAwait(false))
                throw AgentCoreErrors.Conflict("Completion waits for pending user work.");
            if (await db.AgentRuns.AnyAsync(r => r.SessionId == row.SessionId && r.AgentRunId != row.AgentRunId && (r.Status == (int)AgentRunStatus.Running || r.Status == (int)AgentRunStatus.WaitingForSignal), ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Session owns an active execution.");
        }
        if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId }
            && !await db.Entries.AnyAsync(e => e.SessionId == row.SessionId && e.EntryId == entryId.ToString("D") && e.Role == nameof(ConversationRole.Assistant)
                && e.Status == nameof(EntryStatus.Completed) && e.ResponseId == run.ResponseId!.Value.ToString("D"), ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Outcome must reference the saved response.");
        if (command is AgentRunCommand.SuspendWait or AgentRunCommand.ResumeWait or AgentRunCommand.BeginApproval or AgentRunCommand.DecideApproval or AgentRunCommand.ExpireApproval) throw AgentCoreErrors.Conflict("Canonical tenant continuation service required.");
        if (run.Claim is { } claim && claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()
            && command is not (AgentRunCommand.Recover or AgentRunCommand.RequestCancellation or AgentRunCommand.DeadlineExceeded)) throw AgentCoreErrors.Conflict("Execution claim expired.");
        AgentRun next; try { next = command.Apply(run, () => diagnosticId); } catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(ex); }
        AgentRunStoreMapping.Apply(row, next);
        await SettleTenantCompletion(db, next, run.Claim?.Generation, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return next; }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Stale Run revision."); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw AgentCoreErrors.Conflict("Execution ownership changed."); }
    }
}
