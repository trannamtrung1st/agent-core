using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    internal ModelReferenceCandidate[] ModelReferences(Guid tenant, ModelReferenceKind kind)
    {
        lock (_gate)
        {
            var rows = new List<ModelReferenceCandidate>();
            foreach (var owner in tenantSessions.Values.Where(o => o.TenantId == tenant))
            {
                var session = _sessions[owner.SessionId]; if (session.DurablyDeletedAt is not null) continue;
                if (kind == ModelReferenceKind.Runs)
                {
                    foreach (var run in AgentRuns.Runs.Values.Where(r => r.SessionId == session.SessionId && r.Owner.TenantId == tenant))
                        if (Guid.TryParse(run.PinnedModel.OfferingId, out var id)) rows.Add(new(new(run.AgentRunId, run.AgentRunId.ToString("D"), id,
                            session.Title, "Admitted Run pin", run.Revision, run.Status.ToString()), run.AgentInstanceId, run.SessionId));
                    continue;
                }
                if (Guid.TryParse(session.ModelSelection?.OfferingId, out var selected)) rows.Add(new(new(session.SessionId, "selection", selected,
                    session.Title, "Conversation selection", session.Revision, session.Status.ToString())));
                var history = (_entries.GetValueOrDefault(session.SessionId) ?? []).Select(e => e.ModelProvenance?.OfferingId)
                    .Append(session.SummaryModel?.OfferingId).Where(id => id is not null).Distinct();
                foreach (var value in history)
                    if (Guid.TryParse(value, out var id)) rows.Add(new(new(session.SessionId, "provenance", id, session.Title,
                        "Retained summary/message provenance", session.Revision, session.Status.ToString())));
            }
            return rows.ToArray();
        }
    }
}
