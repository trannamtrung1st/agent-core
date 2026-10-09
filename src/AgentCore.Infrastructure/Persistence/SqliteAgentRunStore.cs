using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentRunStore(IDbContextFactory<AgentCoreDbContext> contexts,
    SqliteMemoryStore sessions, IDiagnosticIdSource diagnostics) : IAgentRunStore
{
    public async ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRunOwner owner, Guid agentRunId, AgentRunCommand.Complete completion, Guid? draftEntryId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRuns.SingleOrDefaultAsync(row => row.AgentRunId == agentRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"), cancellationToken)
            .ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");
        var run = AgentRunStoreMapping.ToDomain(row);
        var draftRow = draftEntryId is { } id ? await db.Entries.SingleOrDefaultAsync(entry => entry.SessionId == row.SessionId
            && entry.EntryId == id.ToString("D"), cancellationToken).ConfigureAwait(false) : null;
        if (draftEntryId is not null && draftRow is null) throw AgentCoreErrors.Conflict("Response draft was not found.");
        var draft = draftRow is null ? null : new ConversationEntry(Guid.Parse(draftRow.EntryId), draftRow.EntrySequence, null,
            Enum.Parse<ConversationRole>(draftRow.Role), draftRow.Text, draftRow.ResponseId is { } responseId ? Guid.Parse(responseId) : null,
            Enum.Parse<EntryStatus>(draftRow.Status), SessionMode.Text, 0, 0, DateTimeOffset.FromUnixTimeMilliseconds(draftRow.CreatedAtUtc));
        AgentRunStoreMapping.ValidateOutcome(snapshot, run, completion, draft);
        AgentRun updated;
        try { updated = completion.Apply(run, diagnostics.NewId); }
        catch (Exception exception) when (exception is AgentRunTransitionException or ArgumentException)
        { throw AgentRunStoreMapping.Map(exception); }
        await sessions.StageSaveAsync(db, snapshot, expectedSessionRevision, cancellationToken).ConfigureAwait(false);
        if (draftRow is not null) db.Entries.Remove(draftRow);
        AgentRunStoreMapping.Apply(row, updated);
        CoreEventPersistence.Stage(db, CoreEventPersistence.Run(run, updated));
        await SettleInboxAsync(db, updated, run.Claim?.Generation, cancellationToken).ConfigureAwait(false);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        AgentRunStoreMapping.ObserveTransition(run, updated, completion);
        return updated;
    }

    public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run,
        Guid expectedParentGeneration, CancellationToken cancellationToken = default)
    {
        AgentRunStoreMapping.ValidateImmediateAdmission(run, expectedParentGeneration);
        return AdmitCoreAsync(snapshot, 0, run, null, cancellationToken, expectedParentGeneration);
    }

    public ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default)
    {
        AgentRunStoreMapping.ValidateGenericAdmission(run);
        return AdmitCoreAsync(snapshot, expectedSessionRevision, run, null, cancellationToken);
    }

    public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run,
        long expectedRoutingRevision, CancellationToken cancellationToken = default) =>
        AdmitCoreAsync(snapshot, 0, run, expectedRoutingRevision, cancellationToken);

    private async ValueTask<AgentRunAdmissionResult> AdmitCoreAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, long? expectedRoutingRevision, CancellationToken cancellationToken, Guid? expectedParentGeneration = null, Guid? completionSourceRunId = null, IReadOnlyList<Guid>? additionalCompletionSources = null)
    {
        AgentRunStoreMapping.ValidateAdmission(snapshot, run);
        snapshot = snapshot with { PendingAgentInputIds = snapshot.PendingAgentInputIds.Except(run.Admission.Activation.SourceEntryIds).ToArray() };
        var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, run);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        TriggerOccurrenceRecord? occurrenceRow = null;
        TriggerOccurrence? occurrence = null;
        if (expectedRoutingRevision is { } routingRevision)
        {
            var key = run.Admission.Activation.TriggerOccurrenceId?.ToString("D");
            occurrenceRow = await db.TriggerOccurrences.SingleOrDefaultAsync(row => row.OccurrenceId == key,
                cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Occurrence was not found.");
            occurrence = TriggerStoreMapping.ToOccurrence(occurrenceRow);
            AgentRunStoreMapping.ValidateOccurrenceAdmission(snapshot, run, occurrence, routingRevision);
        }
        else if (run.Admission.Activation.TriggerOccurrenceId is not null
            && snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId)
            throw AgentCoreErrors.Validation("Background occurrence admission must commit its receipt atomically.");
        else if (run.Admission.Activation.TriggerOccurrenceId is { } liveOccurrenceId)
        {
            occurrenceRow = await db.TriggerOccurrences.SingleOrDefaultAsync(row => row.OccurrenceId == liveOccurrenceId.ToString("D"),
                cancellationToken).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Occurrence was not found.");
            occurrence = TriggerStoreMapping.ToOccurrence(occurrenceRow);
            AgentRunStoreMapping.ValidateLiveOccurrenceAdmission(snapshot, run, occurrence);
        }
        var existingAdmission = await db.Activations.AsNoTracking().SingleOrDefaultAsync(row =>
            row.SessionId == activation.SessionId && row.DedupeKey == activation.DedupeKey
            || activation.BackgroundSourceKey != null && row.BackgroundSourceKey == activation.BackgroundSourceKey
                && row.AgentInstanceId == activation.AgentInstanceId && row.ProfileId == activation.ProfileId,
            cancellationToken).ConfigureAwait(false);
        if (existingAdmission is not null)
        {
            if (existingAdmission.AdmissionHash != activation.AdmissionHash
                || existingAdmission.AgentInstanceId != activation.AgentInstanceId || existingAdmission.ProfileId != activation.ProfileId)
                throw AgentCoreErrors.Conflict("Admission key belongs to different input or execution pins.");
            var existing = await db.AgentRuns.AsNoTracking().SingleAsync(row => row.ActivationId == existingAdmission.ActivationId,
                cancellationToken).ConfigureAwait(false);
            if (occurrence is not null && ((expectedRoutingRevision is null ? occurrence.LiveSessionId : occurrence.ExecutionSessionId)?.ToString("D") != existing.SessionId
                || occurrence.AcceptedAgentRunId?.ToString("D") != existing.AgentRunId))
                throw AgentCoreErrors.Persistence("Occurrence receipt disagrees with its execution graph.");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            AgentRunStoreMapping.ObserveAdmission(snapshot, AgentRunStoreMapping.ToDomain(existing), false);
            return new AgentRunAdmissionResult(false, AgentRunStoreMapping.ToDomain(existing));
        }
        if (expectedRoutingRevision is null && occurrence?.AcceptedAgentRunId is not null)
            throw AgentCoreErrors.Persistence("Live receipt has no matching admitted Run.");
        if (run.Admission.Activation.Kind == ActivationKind.ImmediateBackground)
        {
            var sourceId = run.Admission.Activation.SourceAgentRunId!.Value.ToString("D");
            var source = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == sourceId,
                cancellationToken).ConfigureAwait(false);
            var sourceSession = source is null ? null : await db.Sessions.AsNoTracking().Include(row => row.Snapshot)
                .SingleOrDefaultAsync(row => row.SessionId == source.SessionId, cancellationToken).ConfigureAwait(false);
            AgentRunStoreMapping.ValidateImmediateSource(run, source is null ? null : AgentRunStoreMapping.ToDomain(source),
                sourceSession is not null && sourceSession.DurablyDeletedAtUtc is null && sourceSession.ArchivedAtUtc is null
                && sourceSession.Status is not (nameof(SessionStatus.Ended) or nameof(SessionStatus.Ending))
                && sourceSession.Snapshot?.LifecycleStatus is not (nameof(SessionLifecycleStatus.Completed)
                    or nameof(SessionLifecycleStatus.Cancelled) or nameof(SessionLifecycleStatus.Expired) or nameof(SessionLifecycleStatus.Ended))
                && sourceSession.AgentInstanceId == run.AgentInstanceId.ToString("D")
                && sourceSession.Snapshot?.ProfileId == run.ProfileId.ToString("D"));
        }
        if (run.Admission.Activation.Kind == ActivationKind.ImmediateBackground)
        {
            var rows = await db.AgentRuns.AsNoTracking().Where(row => row.AgentInstanceId == run.AgentInstanceId.ToString("D")
                && row.ProfileId == run.ProfileId.ToString("D")).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var owned = rows.Select(AgentRunStoreMapping.ToDomain).ToArray();
            var parent = owned.SingleOrDefault(item => item.AgentRunId == run.Admission.Activation.SourceAgentRunId);
            if (expectedParentGeneration is { } generation && parent?.Claim?.Generation != generation)
                throw AgentCoreErrors.Conflict("Background admission generation is stale.");
            AgentRunStoreMapping.ValidateBackgroundBudget(run, owned);
        }
        if (completionSourceRunId is { } completionSource)
        {
                foreach (var sourceId in new[] { completionSource }.Concat(additionalCompletionSources ?? []))
                {
            await RepairInboxAsync(db, run.CreatedAtUtc, cancellationToken).ConfigureAwait(false);
            var ownedParentRuns = await db.AgentRuns.AsNoTracking().Where(r => r.SessionId == snapshot.SessionId.ToString("D")).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            CompletionInboxMapping.RequireReportAdmission(snapshot, ownedParentRuns.Select(AgentRunStoreMapping.ToDomain));
            var receipt = db.BackgroundCompletionReceipts.Local.SingleOrDefault(r => r.ChildAgentRunId == sourceId.ToString("D"))
                ?? await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == sourceId.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Completion was not found.");
            var item = CompletionInboxMapping.Read(receipt);
            if (item.Status != CompletionInboxStatus.Pending) throw AgentCoreErrors.Conflict("Completion was already claimed, handled or reported.");
            var childRow = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == sourceId.ToString("D"), cancellationToken).ConfigureAwait(false);
            var child = childRow is null ? null : AgentRunStoreMapping.ToDomain(childRow);
            var childSession = child is null ? null : await sessions.LoadMetadataAsync(child.SessionId, cancellationToken).ConfigureAwait(false);
            AgentRunStoreMapping.ValidateCompletionSource(snapshot, run, child, childSession, sourceId == completionSource);
            CompletionInboxMapping.Write(receipt, item with { Revision = item.Revision + 1,
                Status = CompletionInboxStatus.DeliveryQueued, ReportActivationId = run.ActivationId });
                }
        }
        if (expectedRoutingRevision is not null && occurrence?.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession)
        {
            var currentRow = await db.Sessions.AsNoTracking().Include(row => row.Snapshot).SingleOrDefaultAsync(row => row.SessionId == snapshot.SessionId.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Target Session was not found.");
            var current = SqliteMemoryStore.ToSnapshot(currentRow, []);
            AgentRunStoreMapping.ValidateAdmission(current, run);
            if (current.Revision != snapshot.Revision) throw AgentCoreErrors.Conflict("Target Session revision is stale.");
            if (await db.AgentRuns.CountAsync(row => row.SessionId == currentRow.SessionId && row.Status != (int)AgentRunStatus.Completed
                && row.Status != (int)AgentRunStatus.Cancelled && row.Status != (int)AgentRunStatus.Failed, cancellationToken).ConfigureAwait(false) >= 100)
                throw AgentCoreErrors.Conflict("Target Session queue is full.");
        }
        else await sessions.StageSaveAsync(db, snapshot, expectedSessionRevision, cancellationToken).ConfigureAwait(false);
        db.Activations.Add(activation);
        db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
        db.ActivationSourceEntries.AddRange(run.Admission.Activation.SourceEntryIds.Select((id, ordinal) =>
            new ActivationSourceEntryRecord { SessionId = run.SessionId.ToString("D"), EntryId = id.ToString("D"),
                ActivationId = run.ActivationId.ToString("D"), Ordinal = ordinal }));
        if (occurrence is not null)
            TriggerStoreMapping.CopyRouting(occurrenceRow!, expectedRoutingRevision is { } revision
                ? occurrence.WithExecutionAcceptance(snapshot.SessionId, run.AgentRunId, revision, run.CreatedAtUtc)
                : occurrence.WithLiveEvaluation(snapshot.SessionId, run.AgentRunId, run.CreatedAtUtc));
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        AgentRunStoreMapping.ObserveAdmission(snapshot, run, true);
        return new AgentRunAdmissionResult(true, run);
    }

    public async ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid agentRunId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(row => row.AgentRunId == agentRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }

    public async ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid activationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Activations.AsNoTracking().SingleOrDefaultAsync(row => row.ActivationId == activationId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : JsonSerializer.Deserialize<Activation>(row.PayloadJson, AgentRunStoreMapping.Json);
    }

    public async ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentRuns.AsNoTracking().Where(row => row.SessionId == sessionId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"))
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.AgentRunId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).ToArray();
    }

    public async ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid automationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await (from run in db.AgentRuns.AsNoTracking()
            join occurrence in db.TriggerOccurrences.AsNoTracking() on run.AgentRunId equals occurrence.AcceptedAgentRunId
            where run.AgentInstanceId == owner.AgentInstanceId.ToString("D") && run.ProfileId == owner.ProfileId.ToString("D")
                && occurrence.AutomationId == automationId.ToString("D")
            orderby run.CreatedAtUtc descending, run.AgentRunId descending
            select run).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }

    public async ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? sessionId, Guid? before, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw AgentCoreErrors.Validation("AgentRun limit must be between 1 and 100.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.AgentRuns.AsNoTracking().Where(row => row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"));
        if (sessionId is { } session) query = query.Where(row => row.SessionId == session.ToString("D"));
        if (before is { } cursor)
        {
            var last = await query.SingleOrDefaultAsync(row => row.AgentRunId == cursor.ToString("D"), cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.Validation("AgentRun cursor does not belong to this scope.");
            query = query.Where(row => row.CreatedAtUtc < last.CreatedAtUtc || row.CreatedAtUtc == last.CreatedAtUtc && string.Compare(row.AgentRunId, last.AgentRunId) < 0);
        }
        // Retained Run evidence can outlive its Session. Filter before paging, but
        // resolve cursors above so a deletion between page reads stays navigable.
        query = query.Where(run => db.Sessions.Any(session => session.SessionId == run.SessionId
            && session.AgentInstanceId == run.AgentInstanceId && session.Snapshot!.ProfileId == run.ProfileId
            && session.DurablyDeletedAtUtc == null));
        var rows = await query.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.AgentRunId).Take(limit + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Length > limit;
        return new AgentRunPage(rows.Take(limit).Select(AgentRunStoreMapping.ToDomain).ToArray(), more ? Guid.Parse(rows[limit - 1].AgentRunId) : null, more);
    }

    public async ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        if (asOfUtc.Offset != TimeSpan.Zero || limit is < 1 or > AgentRunLimits.MaxListLimit)
            throw AgentCoreErrors.Validation("Runnable query requires UTC time and a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var utc = asOfUtc.ToUnixTimeMilliseconds();
        var waiting = await db.AgentRuns.AsNoTracking().Where(r => r.Status == (int)AgentRunStatus.WaitingForSignal).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var children = waiting.Length > 0 ? await WaitChildrenAsync(db, waiting.Select(AgentRunStoreMapping.ToDomain).SelectMany(r => r.Wait!.BackgroundSessionIds).Distinct().ToArray(), cancellationToken).ConfigureAwait(false) : [];
        var readyWaits = waiting.Select(AgentRunStoreMapping.ToDomain).Where(r => AgentRunWaitExecution.IsReady(r, asOfUtc, children)).ToArray();
        var rows = await db.AgentRuns.AsNoTracking().Where(row => row.Status == (int)AgentRunStatus.Queued
            || row.Status == (int)AgentRunStatus.WaitingToRetry && row.NextRetryAtUtc <= utc
            || row.Status == (int)AgentRunStatus.Running && row.LeaseExpiresAtUtc <= utc
            || row.Status == (int)AgentRunStatus.WaitingForApproval && row.ApprovalExpiresAtUtc <= utc)
            .Where(row => row.Status == (int)AgentRunStatus.Running || row.Status == (int)AgentRunStatus.WaitingForApproval
                || !db.AgentRuns.Any(other => other.SessionId == row.SessionId && other.AgentRunId != row.AgentRunId
                    && (other.Status == (int)AgentRunStatus.Running || other.Status == (int)AgentRunStatus.WaitingForApproval || other.Status == (int)AgentRunStatus.WaitingForSignal)))
            .Where(row => row.Status == (int)AgentRunStatus.Running || row.Status == (int)AgentRunStatus.WaitingForApproval
                || !db.AgentRuns.Any(earlier => earlier.SessionId == row.SessionId
                && (earlier.CreatedAtUtc < row.CreatedAtUtc || earlier.CreatedAtUtc == row.CreatedAtUtc && string.Compare(earlier.AgentRunId, row.AgentRunId) < 0)
                && (earlier.Status == (int)AgentRunStatus.Queued
                    || earlier.Status == (int)AgentRunStatus.WaitingToRetry && earlier.NextRetryAtUtc <= utc
                    || earlier.Status == (int)AgentRunStatus.Running && earlier.LeaseExpiresAtUtc <= utc
                    || earlier.Status == (int)AgentRunStatus.WaitingForApproval && earlier.ApprovalExpiresAtUtc <= utc)))
            .OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.AgentRunId).Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).Concat(readyWaits).OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.AgentRunId).Take(limit).ToArray();
    }

    public async ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Repair requires a bounded limit.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Sessions.AsNoTracking().Where(session => session.PendingAgentInputIdsJson != "[]"
                && session.DurablyDeletedAtUtc == null && session.ArchivedAtUtc == null
                && session.Status != nameof(SessionStatus.Ended) && session.Status != nameof(SessionStatus.Ending)
                && (session.Status != nameof(SessionStatus.Paused) || session.PauseReason == "disconnected" || session.PauseReason == "recovered")
                && session.Snapshot != null && (session.Snapshot.LifecycleStatus == null
                    || session.Snapshot.LifecycleStatus == nameof(SessionLifecycleStatus.Active)
                    || session.Snapshot.LifecycleStatus == nameof(SessionLifecycleStatus.Paused))
                && !db.AgentRuns.Any(run => run.SessionId == session.SessionId && run.Status != (int)AgentRunStatus.Completed
                    && run.Status != (int)AgentRunStatus.Failed && run.Status != (int)AgentRunStatus.Cancelled))
            .OrderBy(session => session.UpdatedAtUtc).ThenBy(session => session.SessionId).Take(limit)
            .Select(session => session.SessionId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Guid.Parse).ToArray();
    }

    public async ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRuns.SingleOrDefaultAsync(row => row.AgentRunId == agentRunId.ToString("D")
            && row.AgentInstanceId == owner.AgentInstanceId.ToString("D") && row.ProfileId == owner.ProfileId.ToString("D"),
            cancellationToken).ConfigureAwait(false);
        if (row is null) throw AgentCoreErrors.NotFound("AgentRun was not found.");
        var run = AgentRunStoreMapping.ToDomain(row);
        if (command is AgentRunCommand.Claim && await db.AgentRuns.AnyAsync(other => other.SessionId == row.SessionId
            && other.AgentRunId != row.AgentRunId && (other.Status == (int)AgentRunStatus.Running || other.Status == (int)AgentRunStatus.WaitingForApproval || other.Status == (int)AgentRunStatus.WaitingForSignal), cancellationToken).ConfigureAwait(false))
            throw AgentCoreErrors.Conflict("Session already owns an active execution.");
        if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
        {
            var entry = await db.Entries.AsNoTracking().SingleOrDefaultAsync(entry => entry.SessionId == row.SessionId
                && entry.EntryId == entryId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (entry is null || entry.Role != nameof(ConversationRole.Assistant) || entry.Status != nameof(EntryStatus.Completed)
                || entry.ResponseId != run.ResponseId?.ToString("D"))
                throw AgentCoreErrors.Conflict("Completed outcome must reference the durable Session response.");
        }
        AgentRun updated;
        try { updated = command is AgentRunCommand.SuspendWait or AgentRunCommand.ResumeWait
            ? AgentRunWaitExecution.Apply(run, command, await WaitChildrenAsync(db, command is AgentRunCommand.SuspendWait suspend ? suspend.Wait.BackgroundSessionIds : run.Wait?.BackgroundSessionIds ?? [], cancellationToken).ConfigureAwait(false)) : command.Apply(run, diagnostics.NewId); }
        catch (Exception e) when (e is AgentRunTransitionException or ArgumentException)
        { throw AgentRunStoreMapping.Map(e); }
        AgentRunStoreMapping.Apply(row, updated);
        CoreEventPersistence.Stage(db, CoreEventPersistence.Run(run, updated));
        await SettleInboxAsync(db, updated, run.Claim?.Generation, cancellationToken).ConfigureAwait(false);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        AgentRunStoreMapping.ObserveTransition(run, updated, command);
        return updated;
    }

    private static async Task SaveAsync(AgentCoreDbContext db, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("AgentRun revision is stale."); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteErrorCode: 19 })
        { throw AgentCoreErrors.Conflict("AgentRun admission or transition conflicts with existing state."); }
    }
}
