using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal sealed class AgentRunMemoryState
{
    public Dictionary<Guid, AgentRun> Runs { get; } = [];
    public Dictionary<Guid, ActivationRecord> Activations { get; } = [];
    public Dictionary<(Guid SessionId, string Key), Guid> ByAdmission { get; } = [];
    public Dictionary<(AgentRunOwner Owner, string Key), Guid> ByBackgroundSource { get; } = [];
    public Dictionary<(Guid SessionId, Guid EntryId), Guid> BySourceEntry { get; } = [];
}

public sealed class InMemoryAgentRunStore(InMemoryMemoryStore sessions, IDiagnosticIdSource diagnostics) : IAgentRunStore
{
    private AgentRunMemoryState State => sessions.AgentRuns;

    public ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision,
        AgentRun run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRunStoreMapping.ValidateAdmission(snapshot, run);
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
                return ValueTask.FromResult(new AgentRunAdmissionResult(false, existing));
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
            sessions.SaveAsync(snapshot, expectedSessionRevision, cancellationToken).GetAwaiter().GetResult();
            State.Runs.Add(run.AgentRunId, run);
            State.Activations.Add(run.ActivationId, activation);
            State.ByAdmission.Add(key, run.AgentRunId);
            foreach (var id in run.Admission.Activation.SourceEntryIds)
                State.BySourceEntry.Add((run.SessionId, id), run.ActivationId);
            if (activation.BackgroundSourceKey is { } backgroundKey)
                State.ByBackgroundSource.Add((run.Owner, backgroundKey), run.AgentRunId);
            return ValueTask.FromResult(new AgentRunAdmissionResult(true, run));
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

    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (asOfUtc.Offset != TimeSpan.Zero || limit is < 1 or > AgentRunLimits.MaxListLimit)
            throw AgentCoreErrors.Validation("Runnable query requires UTC time and a bounded limit.");
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<AgentRun>>(State.Runs.Values.Where(run =>
                run.Status == AgentRunStatus.Queued || run.Status == AgentRunStatus.WaitingToRetry && run.NextRetryAtUtc <= asOfUtc
                || run.Status == AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= asOfUtc
                || run.Status == AgentRunStatus.WaitingForApproval && run.Approval!.ExpiresAtUtc <= asOfUtc)
                .OrderBy(run => run.CreatedAtUtc).ThenBy(run => run.AgentRunId).Take(limit).ToArray());
    }

    public ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(agentRunId, out var run) || run.Owner != owner)
                throw AgentCoreErrors.NotFound("AgentRun was not found.");
            if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
            {
                var entry = sessions.FindEntry(run.SessionId, entryId);
                if (entry is not { Role: ConversationRole.Assistant, Status: EntryStatus.Completed } || entry.ResponseId != run.ResponseId)
                    throw AgentCoreErrors.Conflict("Completed outcome must reference the durable Session response.");
            }
            try
            {
                var updated = command.Apply(run, diagnostics.NewId);
                State.Runs[agentRunId] = updated;
                return ValueTask.FromResult(updated);
            }
            catch (Exception e) when (e is AgentRunTransitionException or ArgumentException)
            {
                throw AgentRunStoreMapping.Map(e);
            }
        }
    }
}
