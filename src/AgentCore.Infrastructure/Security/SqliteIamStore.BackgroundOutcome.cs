using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async Task SettleTenantCompletion(AgentCoreDbContext db, AgentRun next, Guid? generation, CancellationToken ct)
    {
        var runId = next.AgentRunId.ToString("D");
        if (next.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome && next.IsTerminal && next.Result?.OutcomeKind != AgentRunOutcomeKind.Continued && !await db.BackgroundCompletionReceipts.AnyAsync(r => r.ChildAgentRunId == runId, ct).ConfigureAwait(false))
        {
            var row = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleOrDefaultAsync(s => s.SessionId == next.SessionId.ToString("D") && s.DurablyDeletedAtUtc == null, ct).ConfigureAwait(false);
            var initial = row is not null && SqliteMemoryStore.ToSnapshot(row, []).Origin.ReportCompletionToOrigin ? await TenantBackgroundLineage.InitialAsync(next, async (id, token) => {
                var previous = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Owner.TenantId && r.AgentRunId == id.ToString("D"), token).ConfigureAwait(false);
                return previous is null ? null : AgentRunStoreMapping.ToDomain(previous);
            }, ct).ConfigureAwait(false) : null;
            if (row is not null && initial is not null && SqliteMemoryStore.ToSnapshot(row, []).Origin.MayReportCompletion(initial.AgentRunId))
                db.BackgroundCompletionReceipts.Add(CompletionInboxMapping.Create(next, SqliteMemoryStore.ToSnapshot(row, [])));
        }
        var rows = await db.BackgroundCompletionReceipts.Where(r => r.AgentInstanceId == next.AgentInstanceId.ToString("D") && r.ProfileId == null
            && (r.ClaimRunId == runId || r.ParentActivationId == next.ActivationId.ToString("D"))).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var item = CompletionInboxMapping.Read(row);
            if (item.Owner == next.Owner) CompletionInboxMapping.Write(row, TenantBackgroundCompletionRules.Settle(item, next, generation));
        }
    }
    public async ValueTask<AgentRun> CommitTenantOutcomeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        AgentRunCommand.Complete completion, Guid? draftEntryId, Guid diagnosticId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, frame.Owner.TenantId!.Value).SingleAsync(r => r.AgentRunId == frame.RunId.ToString("D"), ct).ConfigureAwait(false);
        var draft = draftEntryId is { } id ? await db.Entries.SingleOrDefaultAsync(e => e.SessionId == frame.SessionId.ToString("D") && e.EntryId == id.ToString("D"), ct).ConfigureAwait(false) : null;
        if (draftEntryId is not null && draft is null) throw AgentCoreErrors.Conflict("Response draft was not found.");
        await RequireTenantOutputSources(db, run, ct).ConfigureAwait(false);
        AgentRunStoreMapping.ValidateOutcome(snapshot, run, completion, draft is null ? null : SqliteMemoryStore.ToEntry(draft));
        AgentRun next; try { next = completion.Apply(run, () => diagnosticId); }
        catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(ex); }
        await new SqliteMemoryStore(contexts, clock ?? TimeProvider.System).StageTenantRuntimeSaveAsync(db, frame, snapshot, expectedRevision, ct).ConfigureAwait(false);
        if (draft is not null) db.Entries.Remove(draft);
        AgentRunStoreMapping.Apply(row, next); await SettleTenantCompletion(db, next, run.Claim?.Generation, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return next; }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Stale Run or Session revision."); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw AgentCoreErrors.Conflict("Outcome ownership changed."); }
    }
}
