using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal TenantRunAdmissionResult? AcceptedTenantInput(TenantSessionRoot owner, Guid principal, Guid message, string text,
        IReadOnlyList<Guid> attachments, TenantInputInterruption? interruption, AgentCore.Domain.Conversation.SessionMode mode)
    {
        lock (_gate)
        {
            var session = ReadTenantSession(owner.TenantId, owner.SessionId);
            if (session?.Owner != owner || !AgentRuns.ByAdmission.TryGetValue((owner.SessionId, $"human:{principal:D}:{message:D}"), out var id)) return null;
            var run = AgentRuns.Runs[id];
            if (run.Owner.TenantId != owner.TenantId || run.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId || run.AgentInstanceId != owner.AgentInstanceId
                || run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId != principal) throw new AccessDeniedException("resource_unavailable");
            var entry = _entries[owner.SessionId].Single(e => e.EntryId == run.Admission.Activation.SourceEntryIds.Single());
            TenantInputReplayRules.Match(run, entry, text, attachments, interruption, mode);
            return new(false, run, entry.EntryId, session.Snapshot.Revision) { EntrySequence = entry.Sequence };
        }
    }
}
