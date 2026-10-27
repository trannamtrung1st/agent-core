using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentRunStore
{
    public ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default)
    {
        if (owner.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>>(WaitChildren()
                .Where(c => c.Run.Owner == owner && c.Session.Origin.OriginatingSessionId == parentSessionId && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId)
                .OrderByDescending(c => c.Run.CreatedAtUtc).ThenByDescending(c => c.Run.AgentRunId).Take(limit).ToArray());
    }

    private IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate> WaitChildren(IReadOnlyList<Guid>? sessionIds = null) => State.Runs.Values.Where(r => r.Owner.TenantId is null).Where(r => sessionIds is null || sessionIds.Contains(r.SessionId)).Select(r =>
        new AgentCore.Application.Ports.BackgroundCompletionCandidate(r, sessions.LoadMetadataAsync(r.SessionId).GetAwaiter().GetResult()!)).Where(c => c.Session is not null).ToArray();

    private void RepairInbox(DateTimeOffset now)
    {
        foreach (var run in State.Runs.Values.Where(r => r.Owner.TenantId is null).Where(r => r.IsTerminal))
        {
            if (State.CompletionReceipts.ContainsKey(run.AgentRunId)) continue;
            var child = sessions.LoadMetadataAsync(run.SessionId).GetAwaiter().GetResult();
            if (child is not null && child.Origin.MayReportCompletion(run.AgentRunId))
                State.CompletionReceipts.Add(run.AgentRunId, CompletionInboxMapping.Create(run, child));
        }
    }

    private void SettleInbox(AgentRun updated, Guid? generation)
    {
        foreach (var row in State.CompletionReceipts.Values.Where(acLegacyReceipt => acLegacyReceipt.ProfileId is not null))
            CompletionInboxMapping.Write(row, CompletionInboxMapping.Read(row).Settle(updated));
        RepairInbox(updated.UpdatedAtUtc);
    }

}
