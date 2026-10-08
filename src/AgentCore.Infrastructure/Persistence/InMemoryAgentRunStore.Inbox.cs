using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentRunStore
{
    public ValueTask<CompletionInboxItem?> GetCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, Guid childRunId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            var item = State.CompletionReceipts.TryGetValue(childRunId, out var row) ? CompletionInboxMapping.Read(row) : null;
            return ValueTask.FromResult(item?.Owner == owner && item.ParentSessionId == parentSessionId ? item : null);
        }
    }

    public ValueTask<bool> HasCompletionClaimAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate) return ValueTask.FromResult(State.CompletionReceipts.Values.Select(CompletionInboxMapping.Read)
            .Any(i => i.Owner == owner && i.Status == CompletionInboxStatus.Claimed && i.ClaimRunId == parentRunId && i.ClaimGeneration == generation && i.ClaimExpiresAtUtc > now));
    }

    public ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundPageAsync(AgentRunOwner owner, Guid parentSessionId, Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        lock (sessions.AdmissionGate)
        {
            var children = WaitChildren().Where(c => c.Run.Owner == owner && c.Session.Origin.OriginatingSessionId == parentSessionId && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId).ToArray();
            var anchor = cursor is Guid id ? children.SingleOrDefault(c => c.Run.AgentRunId == id)?.Run : null;
            if (cursor is not null && anchor is null) throw AgentCoreErrors.NotFound("Background cursor was not found.");
            return ValueTask.FromResult<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>>(children.Where(c =>
                (anchor is null || c.Run.CreatedAtUtc < anchor.CreatedAtUtc || c.Run.CreatedAtUtc == anchor.CreatedAtUtc && string.CompareOrdinal(c.Run.AgentRunId.ToString("D"), anchor.AgentRunId.ToString("D")) < 0)
                && (!pendingOnly || State.CompletionReceipts.TryGetValue(c.Run.AgentRunId, out var row) && row.Status == (int)CompletionInboxStatus.Pending))
                .OrderByDescending(c => c.Run.CreatedAtUtc).ThenByDescending(c => c.Run.AgentRunId.ToString("D"), StringComparer.Ordinal).Take(limit).ToArray());
        }
    }

    public ValueTask<bool> HasCompletionAcknowledgmentAsync(AgentRunOwner owner, Guid parentRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate) return ValueTask.FromResult(State.CompletionReceipts.Values.Select(CompletionInboxMapping.Read)
            .Any(i => i.Owner == owner && i.Status == CompletionInboxStatus.Claimed && i.ClaimRunId == parentRunId && i.Acknowledgment is not null));
    }

    public ValueTask<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Background limit must be 1–100.");
        lock (sessions.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate>>(WaitChildren()
                .Where(c => c.Run.Owner == owner && c.Session.Origin.OriginatingSessionId == parentSessionId && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId)
                .OrderByDescending(c => c.Run.CreatedAtUtc).ThenByDescending(c => c.Run.AgentRunId).Take(limit).ToArray());
    }

    private IReadOnlyList<AgentCore.Application.Ports.BackgroundCompletionCandidate> WaitChildren(IReadOnlyList<Guid>? sessionIds = null) => State.Runs.Values.Where(r => sessionIds is null || sessionIds.Contains(r.SessionId)).Select(r =>
        new AgentCore.Application.Ports.BackgroundCompletionCandidate(r, sessions.LoadMetadataAsync(r.SessionId).GetAwaiter().GetResult()!)).Where(c => c.Session is not null).ToArray();

    private void RepairInbox(DateTimeOffset now)
    {
        foreach (var run in State.Runs.Values.Where(r => r.IsTerminal))
        {
            if (State.CompletionReceipts.ContainsKey(run.AgentRunId)) continue;
            var child = sessions.LoadMetadataAsync(run.SessionId).GetAwaiter().GetResult();
            if (child is not null && child.Origin.MayReportCompletion(run.AgentRunId))
                State.CompletionReceipts.Add(run.AgentRunId, CompletionInboxMapping.Create(run, child));
        }
        foreach (var row in State.CompletionReceipts.Values)
            CompletionInboxMapping.Write(row, CompletionInboxMapping.Refresh(CompletionInboxMapping.Read(row), State.Runs.Values.ToArray(), now));
    }

    private void SettleInbox(AgentRun updated, Guid? generation)
    {
        foreach (var row in State.CompletionReceipts.Values)
            CompletionInboxMapping.Write(row, CompletionInboxMapping.Read(row).Settle(updated, generation));
        RepairInbox(updated.UpdatedAtUtc);
    }

    public ValueTask<IReadOnlyList<CompletionInboxItem>> ListCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Inbox limit must be 1–100.");
        lock (sessions.AdmissionGate)
        {
            var parent = sessions.LoadMetadataAsync(parentSessionId, ct).GetAwaiter().GetResult();
            if (parent?.AgentInstanceId != owner.AgentInstanceId || parent.ProfileId != owner.ProfileId) throw AgentCoreErrors.NotFound("Parent Session was not found.");
            return ValueTask.FromResult<IReadOnlyList<CompletionInboxItem>>(State.CompletionReceipts.Values.Select(CompletionInboxMapping.Read)
                .Where(i => i.Owner == owner && i.ParentSessionId == parentSessionId).OrderBy(i => i.SourceFinishedAtUtc).ThenBy(i => i.ChildAgentRunId).Take(limit).ToArray());
        }
    }

    public ValueTask<CompletionInboxItem> TakeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, string toolCallId, Guid token, DateTimeOffset now, CancellationToken ct = default) =>
        MutateInbox(owner, parentRunId, generation, childRunId, now, (i, p) => i.Take(p, expectedRevision, toolCallId, token, now), ct);

    public ValueTask<CompletionInboxItem> AcknowledgeCompletionAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, long expectedRevision, Guid token, string usage, DateTimeOffset now, CancellationToken ct = default) =>
        MutateInbox(owner, parentRunId, generation, childRunId, now, (i, p) => i.Acknowledge(p, expectedRevision, token, usage, now), ct);

    private ValueTask<CompletionInboxItem> MutateInbox(AgentRunOwner owner, Guid parentRunId, Guid generation, Guid childRunId, DateTimeOffset now, Func<CompletionInboxItem, AgentRun, CompletionInboxItem> apply, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            State.Runs.TryGetValue(parentRunId, out var parent);
            CompletionInboxMapping.RequireParent(parent, owner, generation, now);
            RepairInbox(now);
            if (!State.CompletionReceipts.TryGetValue(childRunId, out var row)) throw AgentCoreErrors.NotFound("Completion was not found.");
            var item = CompletionInboxMapping.Read(row);
            if (item.Owner != owner || item.ParentSessionId != parent!.SessionId) throw AgentCoreErrors.NotFound("Completion was not found.");
            try { item = apply(item, parent); }
            catch (Exception e) when (e is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(e); }
            CompletionInboxMapping.Write(row, item);
            return ValueTask.FromResult(item);
        }
    }
}
