using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal sealed class AgentRunMemoryState
{
    public Dictionary<Guid, BackgroundCompletionReceiptRecord> CompletionReceipts { get; } = [];
    public Dictionary<Guid, AgentRun> Runs { get; } = [];
    public Dictionary<Guid, ActivationRecord> Activations { get; } = [];
    public Dictionary<(Guid SessionId, string Key), Guid> ByAdmission { get; } = [];
    public Dictionary<(AgentRunOwner Owner, string Key), Guid> ByBackgroundSource { get; } = [];
    public Dictionary<(Guid SessionId, Guid EntryId), Guid> BySourceEntry { get; } = [];
}

public sealed partial class InMemoryAgentRunStore : IAgentRunStore
{
    private readonly InMemoryMemoryStore sessions;
    private readonly IDiagnosticIdSource diagnostics;
    private readonly InMemoryTriggerStore? triggers;

    public InMemoryAgentRunStore(InMemoryMemoryStore sessions, IDiagnosticIdSource diagnostics, InMemoryTriggerStore? triggers = null)
    {
        this.sessions = sessions;
        this.diagnostics = diagnostics;
        this.triggers = triggers;
        triggers?.BindAgentRuns(sessions);
    }

    private AgentRunMemoryState State => sessions.AgentRuns;

    public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run,
        Guid expectedParentGeneration, CancellationToken cancellationToken = default)
    {
        AgentRunStoreMapping.ValidateImmediateAdmission(run, expectedParentGeneration);
        return ValueTask.FromResult(AdmitCore(snapshot, 0, run, cancellationToken, expectedParentGeneration: expectedParentGeneration));
    }

    public ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRunOwner owner, Guid agentRunId, AgentRunCommand.Complete completion, Guid? draftEntryId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(agentRunId, out var run) || run.Owner != owner)
                throw AgentCoreErrors.NotFound("AgentRun was not found.");
            var draft = draftEntryId is { } id ? sessions.FindEntry(run.SessionId, id) : null;
            if (draftEntryId is not null && draft is null) throw AgentCoreErrors.Conflict("Response draft was not found.");
            AgentRunStoreMapping.ValidateOutcome(snapshot, run, completion, draft);
            AgentRun updated;
            try { updated = completion.Apply(run, diagnostics.NewId); }
            catch (Exception exception) when (exception is AgentRunTransitionException or ArgumentException)
            { throw AgentRunStoreMapping.Map(exception); }
            sessions.SaveAsync(snapshot, expectedSessionRevision, cancellationToken).GetAwaiter().GetResult();
            if (draftEntryId is { } removed) sessions.RemoveResponseDraft(run.SessionId, removed);
            State.Runs[agentRunId] = updated;
            SettleInbox(updated, run.Claim?.Generation);
            AgentRunStoreMapping.ObserveTransition(run, updated, completion);
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run,
        long expectedRoutingRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (triggers is null) throw AgentCoreErrors.Persistence("Atomic occurrence admission is not configured.");
        lock (triggers.AdmissionGate)
        lock (sessions.AdmissionGate)
        {
            var occurrence = triggers.FindAdmissionOccurrence(run.Admission.Activation.TriggerOccurrenceId ?? Guid.Empty)
                ?? throw AgentCoreErrors.NotFound("Occurrence was not found.");
            AgentRunStoreMapping.ValidateOccurrenceAdmission(snapshot, run, occurrence, expectedRoutingRevision);
            var next = occurrence.Disposition == AgentCore.Domain.Triggers.OccurrenceRoutingDisposition.AcceptedDurable
                ? occurrence : occurrence.WithExecutionAcceptance(snapshot.SessionId, run.AgentRunId,
                    expectedRoutingRevision, run.CreatedAtUtc);
            var existingTarget = occurrence.ExecutionTarget.Kind == AgentCore.Domain.Triggers.AutomationExecutionTargetKind.ExistingSession;
            if (existingTarget)
            {
                var current = sessions.LoadMetadataAsync(snapshot.SessionId, cancellationToken).GetAwaiter().GetResult()
                    ?? throw AgentCoreErrors.NotFound("Target Session was not found.");
                AgentRunStoreMapping.ValidateAdmission(current, run);
                if (current.Revision != snapshot.Revision) throw AgentCoreErrors.Conflict("Target Session revision is stale.");
                if (State.Runs.Values.Count(item => item.SessionId == run.SessionId && !item.IsTerminal) >= 100)
                    throw AgentCoreErrors.Conflict("Target Session queue is full.");
            }
            var result = AdmitCore(snapshot, 0, run, cancellationToken, occurrenceValidated: true, preserveSession: existingTarget);
            if (result.Run.SessionId != next.ExecutionSessionId || result.Run.AgentRunId != next.AcceptedAgentRunId)
                throw AgentCoreErrors.Persistence("Occurrence receipt disagrees with its execution graph.");
            triggers.CommitAdmissionOccurrence(next);
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default)
    {
        AgentRunStoreMapping.ValidateGenericAdmission(run);
        if (run.Admission.Activation.TriggerOccurrenceId is { } occurrenceId)
        {
            if (snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId)
                throw AgentCoreErrors.Validation("Background occurrence admission must commit its receipt atomically.");
            if (triggers is null) throw AgentCoreErrors.Persistence("Atomic live occurrence admission is not configured.");
            lock (triggers.AdmissionGate)
            lock (sessions.AdmissionGate)
            {
                var occurrence = triggers.FindAdmissionOccurrence(occurrenceId)
                    ?? throw AgentCoreErrors.NotFound("Occurrence was not found.");
                AgentRunStoreMapping.ValidateLiveOccurrenceAdmission(snapshot, run, occurrence);
                if (occurrence.AcceptedAgentRunId is { } accepted
                    && (!State.Runs.TryGetValue(accepted, out var linkedRun)
                        || linkedRun.Admission.Activation.TriggerOccurrenceId != occurrenceId || linkedRun.SessionId != snapshot.SessionId))
                    throw AgentCoreErrors.Persistence("Live receipt has no matching admitted Run.");
                var result = AdmitCore(snapshot, expectedSessionRevision, run, cancellationToken);
                if (occurrence.AcceptedAgentRunId is { } linked)
                {
                    if (linked != result.Run.AgentRunId) throw AgentCoreErrors.Persistence("Live receipt disagrees with its admitted Run.");
                }
                else triggers.CommitAdmissionOccurrence(occurrence.WithLiveEvaluation(snapshot.SessionId, result.Run.AgentRunId, run.CreatedAtUtc));
                return ValueTask.FromResult(result);
            }
        }
        return ValueTask.FromResult(AdmitCore(snapshot, expectedSessionRevision, run, cancellationToken));
    }

    private AgentRunAdmissionResult AdmitCore(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken, bool occurrenceValidated = false, Guid? expectedParentGeneration = null, Guid? completionSourceRunId = null, bool preserveSession = false, IReadOnlyList<Guid>? additionalCompletionSources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRunStoreMapping.ValidateAdmission(snapshot, run);
        snapshot = snapshot with { PendingAgentInputIds = snapshot.PendingAgentInputIds.Except(run.Admission.Activation.SourceEntryIds).ToArray() };
        if (!occurrenceValidated && run.Admission.Activation.TriggerOccurrenceId is not null
            && snapshot.Origin.InitialBackgroundAgentRunId == run.AgentRunId)
            throw AgentCoreErrors.Validation("Background occurrence admission must commit its receipt atomically.");
        var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, run);
        lock (sessions.AdmissionGate)
        {
            Guid existingId;
            var key = (run.SessionId, run.Admission.Activation.DedupeKey);
            var found = State.ByAdmission.TryGetValue(key, out existingId);
            if (!found && activation.BackgroundSourceKey is { } sourceKey)
                found = State.ByBackgroundSource.TryGetValue((run.Owner, sourceKey), out existingId);
            if (found)
            {
                var existing = State.Runs[existingId];
                if (existing.Owner != run.Owner || State.Activations[existing.ActivationId].AdmissionHash != activation.AdmissionHash)
                    throw AgentCoreErrors.Conflict("Admission key belongs to different input or execution pins.");
                AgentRunStoreMapping.ObserveAdmission(snapshot, existing, false);
                return new AgentRunAdmissionResult(false, existing);
            }
            if (State.Runs.ContainsKey(run.AgentRunId) || State.Activations.ContainsKey(run.ActivationId))
                throw AgentCoreErrors.Conflict("AgentRun or Activation identity already exists.");
            State.Runs.TryGetValue(run.Admission.Activation.SourceAgentRunId ?? Guid.Empty, out var source);
            var sourceSession = source is null ? null : sessions.LoadMetadataAsync(source.SessionId, cancellationToken).GetAwaiter().GetResult();
            AgentRunStoreMapping.ValidateImmediateSource(run, source, sourceSession is not null
                && sourceSession.DurablyDeletedAt is null && sourceSession.ArchivedAt is null
                && sourceSession.Status is not (SessionStatus.Ended or SessionStatus.Ending)
                && !SessionLifecycle.IsTerminal(sourceSession.LifecycleStatus)
                && sourceSession.AgentInstanceId == run.AgentInstanceId && sourceSession.ProfileId == run.ProfileId);
            if (run.Admission.Activation.SourceEntryIds.Any(id => State.BySourceEntry.ContainsKey((run.SessionId, id))))
                throw AgentCoreErrors.Conflict("Accepted input is already owned by another Activation.");
            // This synchronous store shares the gate. Validation completes before either graph is committed.
            if (run.Admission.Activation.Kind == ActivationKind.ImmediateBackground)
            {
                if (expectedParentGeneration is { } parentGeneration && source?.Claim?.Generation != parentGeneration)
                    throw AgentCoreErrors.Conflict("Background admission generation is stale.");
                AgentRunStoreMapping.ValidateBackgroundBudget(run, State.Runs.Values);
            }
            if (completionSourceRunId is { } completionSource)
            {
                foreach (var sourceId in new[] { completionSource }.Concat(additionalCompletionSources ?? []))
                {
                RepairInbox(run.CreatedAtUtc);
                CompletionInboxMapping.RequireReportAdmission(snapshot, State.Runs.Values);
                if (State.CompletionReceipts.TryGetValue(sourceId, out var existingReceipt)
                    && CompletionInboxMapping.Read(existingReceipt).Status != CompletionInboxStatus.Pending)
                    throw AgentCoreErrors.Conflict("Completion was already claimed, handled or reported.");
                State.Runs.TryGetValue(sourceId, out var child);
                var childSession = child is null ? null : sessions.LoadMetadataAsync(child.SessionId, cancellationToken).GetAwaiter().GetResult();
                AgentRunStoreMapping.ValidateCompletionSource(snapshot, run, child, childSession, sourceId == completionSource);
                }
            }
            if (!preserveSession) sessions.SaveAsync(snapshot, expectedSessionRevision, cancellationToken).GetAwaiter().GetResult();
            State.Runs.Add(run.AgentRunId, run);
            State.Activations.Add(run.ActivationId, activation);
            State.ByAdmission.Add(key, run.AgentRunId);
            foreach (var id in run.Admission.Activation.SourceEntryIds)
                State.BySourceEntry.Add((run.SessionId, id), run.ActivationId);
            if (activation.BackgroundSourceKey is { } backgroundKey)
                State.ByBackgroundSource.Add((run.Owner, backgroundKey), run.AgentRunId);
            if (completionSourceRunId is { } reported)
            {
                foreach (var sourceId in new[] { reported }.Concat(additionalCompletionSources ?? []))
                {
                var receipt = State.CompletionReceipts[sourceId];
                var item = CompletionInboxMapping.Read(receipt);
                CompletionInboxMapping.Write(receipt, item with { Revision = item.Revision + 1,
                    Status = CompletionInboxStatus.DeliveryQueued, ReportActivationId = run.ActivationId });
                }
            }
            AgentRunStoreMapping.ObserveAdmission(snapshot, run, true);
            return new AgentRunAdmissionResult(true, run);
        }
    }

    public ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid agentRunId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult(State.Runs.TryGetValue(agentRunId, out var run) && run.Owner == owner ? run : null);
    }

    public ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid activationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult(State.Runs.Values.SingleOrDefault(run => run.ActivationId == activationId && run.Owner == owner)?.Admission.Activation);
    }

    public ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<AgentRun>>(State.Runs.Values
                .Where(run => run.SessionId == sessionId && run.Owner == owner)
                .OrderBy(run => run.CreatedAtUtc).ThenBy(run => run.AgentRunId).ToArray());
    }

    public ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid automationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult(State.Runs.Values.Where(run => run.Owner == owner)
                .Where(run =>
                {
                    var origin = sessions.LoadMetadataAsync(run.SessionId, cancellationToken).GetAwaiter().GetResult()?.Origin;
                    return run.Admission.Activation.TriggerOccurrenceId is { } id
                        && triggers?.FindAdmissionOccurrence(id)?.AutomationId == automationId;
                })
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.AgentRunId).FirstOrDefault());
    }

    public ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? sessionId, Guid? before, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw AgentCoreErrors.Validation("AgentRun limit must be between 1 and 100.");
        lock (sessions.AdmissionGate)
        {
            var ordered = State.Runs.Values.Where(run => run.Owner == owner && (sessionId == null || run.SessionId == sessionId))
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.AgentRunId.ToString("D"), StringComparer.Ordinal).ToArray();
            var offset = before is null ? 0 : Array.FindIndex(ordered, run => run.AgentRunId == before) + 1;
            if (before is not null && offset == 0) throw AgentCoreErrors.Validation("AgentRun cursor does not belong to this scope.");
            var page = ordered.Skip(offset).Where(run =>
            {
                var session = sessions.LoadMetadataAsync(run.SessionId, cancellationToken).GetAwaiter().GetResult();
                return session is not null && session.AgentInstanceId == run.AgentInstanceId
                    && session.ProfileId == run.ProfileId && session.DurablyDeletedAt is null;
            }).Take(limit + 1).ToArray();
            var more = page.Length > limit;
            return ValueTask.FromResult(new AgentRunPage(page.Take(limit).ToArray(), more ? page[limit - 1].AgentRunId : null, more));
        }
    }

    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asOfUtc.Offset != TimeSpan.Zero || limit is < 1 or > AgentRunLimits.MaxListLimit)
            throw AgentCoreErrors.Validation("Runnable query requires UTC time and a bounded limit.");
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<AgentRun>>(State.Runs.Values.Where(run =>
                run.Status == AgentRunStatus.WaitingForSignal && AgentRunWaitExecution.IsReady(run, asOfUtc, WaitChildren(run.Wait!.BackgroundSessionIds)) || run.Status == AgentRunStatus.Queued || run.Status == AgentRunStatus.WaitingToRetry && run.NextRetryAtUtc <= asOfUtc
                || run.Status == AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= asOfUtc
                || run.Status == AgentRunStatus.WaitingForApproval && run.Approval!.ExpiresAtUtc <= asOfUtc)
                .Where(run => run.Status is AgentRunStatus.Running or AgentRunStatus.WaitingForApproval or AgentRunStatus.WaitingForSignal
                    || !State.Runs.Values.Any(other => other.SessionId == run.SessionId && other.AgentRunId != run.AgentRunId
                        && other.Status is AgentRunStatus.Running or AgentRunStatus.WaitingForApproval or AgentRunStatus.WaitingForSignal))
                .OrderBy(run => run.CreatedAtUtc).ThenBy(run => run.AgentRunId).GroupBy(run => run.SessionId).Select(group => group.First()).Take(limit).ToArray());
    }

    public ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Repair requires a bounded limit.");
        return ValueTask.FromResult(sessions.PendingInputSessions(limit));
    }

    public ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(agentRunId, out var run) || run.Owner != owner)
                throw AgentCoreErrors.NotFound("AgentRun was not found.");
            if (command is AgentRunCommand.Claim && State.Runs.Values.Any(other => other.SessionId == run.SessionId
                && other.AgentRunId != run.AgentRunId && other.Status is AgentRunStatus.Running or AgentRunStatus.WaitingForApproval or AgentRunStatus.WaitingForSignal))
                throw AgentCoreErrors.Conflict("Session already owns an active execution.");
            if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
            {
                var entry = sessions.FindEntry(run.SessionId, entryId);
                if (entry is not { Role: ConversationRole.Assistant, Status: EntryStatus.Completed } || entry.ResponseId != run.ResponseId)
                    throw AgentCoreErrors.Conflict("Completed outcome must reference the durable Session response.");
            }
            try
            {
                var updated = command is AgentRunCommand.SuspendWait or AgentRunCommand.ResumeWait
                    ? AgentRunWaitExecution.Apply(run, command, WaitChildren(command is AgentRunCommand.SuspendWait suspend ? suspend.Wait.BackgroundSessionIds : run.Wait?.BackgroundSessionIds ?? [])) : command.Apply(run, diagnostics.NewId);
                State.Runs[agentRunId] = updated;
            SettleInbox(updated, run.Claim?.Generation);
                AgentRunStoreMapping.ObserveTransition(run, updated, command);
                return ValueTask.FromResult(updated);
            }
            catch (Exception e) when (e is AgentRunTransitionException or ArgumentException)
            {
                throw AgentRunStoreMapping.Map(e);
            }
        }
    }
}
