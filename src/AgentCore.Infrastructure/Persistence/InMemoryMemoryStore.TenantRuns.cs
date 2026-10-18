using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    // Only factual completion/recovery may inspect retained ownership after logical deletion; this is not runtime admission.
    internal AgentRun? ReadRetainedTenantOperationRun(Guid tenant, Guid runId)
    {
        lock (_gate) return AgentRuns.Runs.TryGetValue(runId, out var run) && run.Owner.TenantId == tenant
            && tenantSessions.TryGetValue(run.SessionId, out var owner) && owner.TenantId == tenant
            && owner.AgentInstanceId == run.AgentInstanceId && owner.ExecutorPrincipalId == run.Admission.ExecutionContext?.AgentPrincipalId ? run : null;
    }
    internal TenantSessionRoot? ResolveTenantSessionOwner(Guid sessionId)
    { lock (_gate) return tenantSessions.GetValueOrDefault(sessionId); }
    internal void SaveRetainedTenantOperationRun(AgentRun run, long expectedRevision)
    {
        lock (_gate)
        {
            if (ReadRetainedTenantOperationRun(run.Owner.TenantId!.Value, run.AgentRunId)?.Revision != expectedRevision)
                throw new SecurityConflictException("stale_revision");
            AgentRuns.Runs[run.AgentRunId] = run;
        }
    }
    internal void SaveTenantOperationRun(AgentRun run, long expectedRevision)
    {
        lock (_gate)
        {
            if (ReadTenantRun(run.Owner.TenantId!.Value, run.AgentRunId)?.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
            AgentRuns.Runs[run.AgentRunId] = run;
        }
    }
    internal AgentRun? WaitTenantApproval(Guid tenant, Guid runId, long revision, Guid requestId, Guid? generation, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(tenant, runId); if (run is null || run.Revision != revision) return null;
            var next = run.WaitForDurableApproval(revision, requestId, generation, at); AgentRuns.Runs[runId] = next; return next;
        }
    }
    internal void AdmitTenantApprovalContinuation(AgentRun source, AgentRun next, Guid requestId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (ReadTenantRun(source.Owner.TenantId!.Value, source.AgentRunId)?.Revision != source.Revision
                || AgentRuns.Runs.ContainsKey(next.AgentRunId) || AgentRuns.Activations.ContainsKey(next.ActivationId)
                || AgentRuns.ByAdmission.ContainsKey((next.SessionId, next.Admission.Activation.DedupeKey))) throw new SecurityConflictException("stale_revision");
            var retired = source.HandoffApprovalContinuation(source.Revision, requestId, next.AgentRunId, at);
            var activation = AgentRunStoreMapping.ToActivationRecord(_sessions[source.SessionId], next);
            AgentRuns.Runs[source.AgentRunId] = retired; AgentRuns.Runs.Add(next.AgentRunId, next);
            AgentRuns.Activations.Add(next.ActivationId, activation); AgentRuns.ByAdmission.Add((next.SessionId, next.Admission.Activation.DedupeKey), next.AgentRunId);
        }
    }

    internal bool ResumeTenantApproval(Guid tenant, Guid runId, Guid requestId, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(tenant, runId); if (run is null || run.ApprovalRequestId != requestId) return false;
            if (run.IsTerminal) return true;
            if (run.Status != AgentRunStatus.WaitingForApproval) return false;
            AgentRuns.Runs[runId] = run.ResumeDurableApproval(run.Revision, requestId, at); return true;
        }
    }
    internal AgentRun? CancelTenantRun(Guid tenant, Guid runId, long expectedRunRevision, DateTimeOffset at, ConversationEntry? response = null)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(tenant, runId); if (run is null || run.Revision != expectedRunRevision) return null;
            var saved = TenantRunCancellationRules.Cancel(run, at);
            var entries = TenantRunCancellationRules.ResponseEntries(_entries.GetValueOrDefault(run.SessionId, []), run, response, at);
            if (!entries.SequenceEqual(_entries.GetValueOrDefault(run.SessionId, [])))
                Store(_sessions[run.SessionId] with { Revision = checked(_sessions[run.SessionId].Revision + 1), UpdatedAt = at, Entries = entries,
                    LastEntrySequence = Math.Max(_sessions[run.SessionId].DurableLastEntrySequence, entries.Max(e => e.Sequence)) });
            AgentRuns.Runs[runId] = saved; return saved;
        }
    }
    internal AgentRun? ReadTenantRun(Guid tenant, Guid runId)
    {
        lock (_gate) return AgentRuns.Runs.TryGetValue(runId, out var run) && run.Owner.TenantId == tenant && OwnsTenantSession(tenant, run.SessionId) ? run : null;
    }
    internal IReadOnlyList<Guid> TenantRunIds(Guid tenant, Guid session)
    {
        lock (_gate) return AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == tenant && r.SessionId == session).Select(r => r.AgentRunId).ToArray();
    }
    internal TenantRunAdmissionResult? AdmitTenantRun(TenantHumanRunAdmission admission, TenantAgent agent, TenantSessionInitiator initiator)
    {
        lock (_gate)
        {
            var run = admission.Run; var tenant = run.Owner.TenantId ?? throw new ArgumentException("Tenant execution is required.");
            var session = ReadTenantSession(tenant, run.SessionId); if (session is null) return null;
            var next = TenantRunRules.Admit(session, admission, agent, initiator);
            var key = (run.SessionId, run.Admission.Activation.DedupeKey);
            if (AgentRuns.ByAdmission.TryGetValue(key, out var existingId))
            {
                var saved = AgentRuns.Runs[existingId]; var input = _entries[run.SessionId].Single(e => e.EntryId == saved.Admission.Activation.SourceEntryIds.Single());
                if (!TenantRunRules.SameMessage(saved, input, admission)) throw new SecurityConflictException("message_id_conflict");
                return new(false, saved, input.EntryId, session.Snapshot.Revision);
            }
            if (AgentRuns.Runs.Values.Any(r => r.SessionId == run.SessionId && r.Status == AgentRunStatus.Running)) throw new SecurityConflictException("session_in_use");
            if (AgentRuns.Runs.Values.Count(r => r.SessionId == run.SessionId && !r.IsTerminal) >= 100) throw new SecurityConflictException("session_queue_full");
            if (AgentRuns.Runs.ContainsKey(run.AgentRunId) || AgentRuns.Activations.ContainsKey(run.ActivationId)
                || _entries.Values.Any(entries => entries.Any(e => e.EntryId == admission.Input.EntryId))) throw new SecurityConflictException("identity_conflict");
            var activation = AgentRunStoreMapping.ToActivationRecord(next, run);
            Store(next); AgentRuns.Runs.Add(run.AgentRunId, run); AgentRuns.Activations.Add(run.ActivationId, activation);
            AgentRuns.ByAdmission.Add(key, run.AgentRunId); AgentRuns.BySourceEntry.Add((run.SessionId, admission.Input.EntryId), run.ActivationId);
            return new(true, run, admission.Input.EntryId, next.Revision);
        }
    }
}
