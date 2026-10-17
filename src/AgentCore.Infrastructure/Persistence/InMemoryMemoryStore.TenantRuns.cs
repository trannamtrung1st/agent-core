using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal AgentRun? CancelTenantRun(Guid tenant, Guid runId, long expectedRunRevision, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(tenant, runId); if (run is null || run.Revision != expectedRunRevision) return null;
            if (_sessions[run.SessionId].Status is SessionStatus.Attached or SessionStatus.Ending) throw new SecurityConflictException("session_in_use");
            if (run.Status != AgentRunStatus.Queued) throw new SecurityConflictException("run_in_use");
            var saved = run.RequestCancellation(run.Revision, null, at); AgentRuns.Runs[runId] = saved; return saved;
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
