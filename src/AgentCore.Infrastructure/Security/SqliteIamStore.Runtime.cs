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
        var producerRow = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.TenantId == frame.Owner.TenantId && r.AgentRunId == frame.RunId.ToString("D"), ct).ConfigureAwait(false);
        var producer = AgentRunStoreMapping.ToDomain(producerRow);
        if (TenantHttpReferences.Read(producer).Count > 0 || TenantPublicWebReadService.HasStored(producer) || TenantAutomationConversationRules.IsConversationRoot(producer) || AgentRunToolCallCheckpoint.ReadExecutionState(producer.Checkpoint).TenantProfileSource is not null
            || TenantMemoryOperationReferences.Read(producer.Checkpoint) is { Count: > 0 } || TenantHistoricalReferences.Read(producer.Checkpoint) is { Count: > 0 }
            || TenantExperienceReferences.Sources(producer.Checkpoint) is { Count: > 0 } || TenantExperienceReferences.Facts(producer.Checkpoint) is { Count: > 0 })
        {
            var rows = await db.Entries.AsNoTracking().Where(e => e.SessionId == producerRow.SessionId && e.ResponseId == producer.ResponseId!.Value.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false);
            var saved = rows.Select(SqliteMemoryStore.ToEntry).ToDictionary(e => e.EntryId);
            if (TenantRuntimeRules.PublishesResponse(producer, snapshot, id => saved.GetValueOrDefault(id)))
            {
                await RequirePublicWebSources(db, producer, ct).ConfigureAwait(false);
                await RequireHttpSources(db, producer, ct).ConfigureAwait(false);
                await RequireStoredProfileSource(db, producer, ct).ConfigureAwait(false);
                await RequireStoredMemorySources(db, producer, ct).ConfigureAwait(false);
                if (TenantAutomationConversationRules.IsConversationRoot(producer)) await RequireTenantEventRead(db, producer, ct).ConfigureAwait(false);
            }
        }
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
        if ((TenantContextContinuationRules.InputBoundary(run) ?? TenantConfigurationContinuationRules.InputBoundary(run) ?? TenantBackgroundCompletionFactory.InputBoundary(run) ?? TenantAutomationConversationRules.InputBoundary(run)) is { } reportBoundary) return reportBoundary;
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
        var query = UnblockedTenantInput(db).AsNoTracking().Where(r => r.TenantId != null && !db.Set<TenantApprovalRecord>().Any(a => a.TenantId == r.TenantId && a.RunId == r.AgentRunId && a.Status == (int)TenantApprovalStatus.Pending)
            && db.Set<Tenant>().Any(t => t.TenantId == r.TenantId && t.Status == IdentityStatus.Active)
            && db.Set<TenantSessionRecord>().Any(s => s.SessionId == r.SessionId && s.TenantId == r.TenantId)
            && db.Sessions.Any(s => s.SessionId == r.SessionId && s.DurablyDeletedAtUtc == null && s.ArchivedAtUtc == null && (s.RetentionDueAtUtc == null || s.RetentionDueAtUtc > millis))
            && (r.Status == (int)AgentRunStatus.Queued || r.Status == (int)AgentRunStatus.WaitingToRetry && r.NextRetryAtUtc <= millis
                || r.Status == (int)AgentRunStatus.Running && r.LeaseExpiresAtUtc <= millis || r.Status == (int)AgentRunStatus.WaitingForSignal))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => db.ActivationSourceEntries.Where(e => e.ActivationId == r.ActivationId).Join(db.Entries, source => source.EntryId, entry => entry.EntryId, (source, entry) => (long?)entry.EntrySequence).Min() ?? long.MaxValue)
            .ThenBy(r => r.AgentRunId);
        var ready = new List<AgentRun>();
        // Apply signal readiness before the result limit. Unready waits cannot hide later runnable work.
        for (var offset = 0; ready.Count < Math.Clamp(limit, 1, 100); offset += 32)
        {
            var rows = await query.Skip(offset).Take(32).ToArrayAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var run = AgentRunStoreMapping.ToDomain(row);
                if (run.Admission.Activation.Kind is not (ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ConfigurationContinuation or ActivationKind.ContextContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted or ActivationKind.ScheduledWork or ActivationKind.ManualBackground or ActivationKind.CoreEvent or ActivationKind.ApplicationEvent)) continue;
                if (run.Status != AgentRunStatus.WaitingForSignal || AgentRunWaitExecution.IsReady(run, at,
                    await TenantWaitChildren(db, run, run.Wait!, false, ct).ConfigureAwait(false))) ready.Add(run);
                if (ready.Count == Math.Clamp(limit, 1, 100)) break;
            }
            if (rows.Length < 32) break;
        }
        return ready;
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
            if (run.Admission.Activation.Kind is not (ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ConfigurationContinuation or ActivationKind.ContextContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted or ActivationKind.ScheduledWork or ActivationKind.ManualBackground or ActivationKind.CoreEvent or ActivationKind.ApplicationEvent)) throw AgentCoreErrors.Conflict("Canonical tenant continuation service required.");
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
            if (!await UnblockedTenantInput(db).AnyAsync(r => r.AgentRunId == row.AgentRunId, ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Session owns an active execution.");
        }
        if (command is AgentRunCommand.Checkpoint profileCheckpoint)
        {
            await RequireMemoryCheckpoint(db, run, profileCheckpoint.Value, ct).ConfigureAwait(false);
            var proposedProfile = AgentRunToolCallCheckpoint.ReadExecutionState(profileCheckpoint.Value).TenantProfileSource;
            if (AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is not null && proposedProfile is null)
                throw new AccessDeniedException("profile_source_unavailable");
            if (proposedProfile is not null) await RequireProfileSource(db, run, proposedProfile, ct).ConfigureAwait(false);
        }
        if (command is AgentRunCommand.Complete) await RequireTenantEventRead(db, run, ct).ConfigureAwait(false);
        if (command is AgentRunCommand.Complete { Learning: not null }) throw AgentCoreErrors.Conflict("Learning requires the atomic response outcome transaction.");
        if (command is AgentRunCommand.Complete { OutcomeEntryId: not null })
        {
            await RequireTenantOutputSources(db, run, ct).ConfigureAwait(false);
            var inboxRows = await db.BackgroundCompletionReceipts.AsNoTracking().Where(i => i.ClaimRunId == row.AgentRunId && i.Status == (int)CompletionInboxStatus.Claimed).ToArrayAsync(ct).ConfigureAwait(false);
            if (inboxRows.Select(CompletionInboxMapping.Read).Any(i => i.Owner == owner && i.Acknowledgment is not null))
                throw AgentCoreErrors.Conflict("Acknowledgment requires the atomic outcome transaction.");
        }
        if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId }
            && !await db.Entries.AnyAsync(e => e.SessionId == row.SessionId && e.EntryId == entryId.ToString("D") && e.Role == nameof(ConversationRole.Assistant)
                && e.Status == nameof(EntryStatus.Completed) && e.ResponseId == run.ResponseId!.Value.ToString("D"), ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Outcome must reference the saved response.");

        if (run.Claim is { } claim && claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()
            && command is not (AgentRunCommand.Recover or AgentRunCommand.RequestCancellation or AgentRunCommand.DeadlineExceeded)) throw AgentCoreErrors.Conflict("Execution claim expired.");
        AgentRun next;
        try
        {
            if (command is AgentRunCommand.SuspendWait suspend)
            {
                await RequireTenantWait(db, run, suspend, ct).ConfigureAwait(false);
                next = AgentRunWaitExecution.Apply(run, command, await TenantWaitChildren(db, run, suspend.Wait, true, ct).ConfigureAwait(false));
            }
            else if (command is AgentRunCommand.ResumeWait)
                next = AgentRunWaitExecution.Apply(run, command, await TenantWaitChildren(db, run, run.Wait ?? throw AgentCoreErrors.Conflict("Run is not waiting."), false, ct).ConfigureAwait(false));
            else next = command.Apply(run, () => diagnosticId);
        }
        catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(ex); }
        if (command is AgentRunCommand.Fail { Code: "protected-output-unavailable" })
            await SettleRefusedTenantOutput(db, run, next, ct).ConfigureAwait(false);
        if (command is AgentRunCommand.Checkpoint coverageCheckpoint) await AcknowledgeTenantExperienceCoverage(db, run, coverageCheckpoint.Value, ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(row, next); await TenantCoreEventProduction.StageRunAsync(db, run, next, ct).ConfigureAwait(false);
        await SettleTenantCompletion(db, next, run.Claim?.Generation, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return next; }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Stale Run revision."); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw AgentCoreErrors.Conflict("Execution ownership changed."); }
    }
}
