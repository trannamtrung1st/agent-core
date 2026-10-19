using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryMemoryStore
{
    private HashSet<Guid> FutureTenantEntries(AgentRun run)
    {
        if (TenantBackgroundCompletionFactory.InputBoundary(run) is { } reportBoundary)
            return _entries.GetValueOrDefault(run.SessionId, []).Where(e => e.Sequence > reportBoundary && e.ResponseId != run.ResponseId).Select(e => e.EntryId).ToHashSet();
        var inputs = TenantApprovalContinuationRules.Inputs(run).Select(id => FindEntry(run.SessionId, id)).ToArray();
        if (inputs.Length == 0 || inputs.Any(e => e is not { Role: ConversationRole.User, Status: EntryStatus.Completed })) throw new AccessDeniedException("tenant_runtime_inputs_unavailable");
        var boundary = inputs.Max(e => e!.Sequence);
        return _entries.GetValueOrDefault(run.SessionId, []).Where(e => e.Sequence > boundary && e.ResponseId != run.ResponseId).Select(e => e.EntryId).ToHashSet();
    }
    internal SessionSnapshot? LoadTenantRuntime(TenantRuntimeFrame frame, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(frame.Owner.TenantId!.Value, frame.RunId);
            if (run is null || !tenantSessions.TryGetValue(frame.SessionId, out var root)) return null;
            TenantRuntimeRules.Require(frame, root, run, at);
            var hidden = FutureTenantEntries(run);
            var entries = _entries.GetValueOrDefault(frame.SessionId, []).Where(e => !hidden.Contains(e.EntryId)).OrderByDescending(e => e.Sequence).Take(40)
                .Concat(_entries.GetValueOrDefault(frame.SessionId, []).Where(e => TenantApprovalContinuationRules.Inputs(run).Contains(e.EntryId))).DistinctBy(e => e.EntryId).OrderBy(e => e.Sequence).ToArray();
            return _sessions[frame.SessionId] with { Entries = entries };
        }
    }
    internal void SaveTenantRuntime(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, DateTimeOffset at)
    {
        lock (_gate)
        {
            var run = ReadTenantRun(frame.Owner.TenantId!.Value, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            TenantRuntimeRules.Require(frame, tenantSessions[frame.SessionId], run, at);
            var current = _sessions[frame.SessionId]; TenantRuntimeRules.ValidateSnapshot(frame, current, snapshot);
            var window = CloneForCompare(current, frame.SessionId, snapshot.Entries);
            if (current.Revision == snapshot.Revision && SameContent(window, snapshot)) return;
            if (current.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1) throw AgentCoreErrors.Conflict("Stale Session revision.");
            Store(snapshot);
        }
    }
    internal ConversationHistoryPage? ReadTenantRuntimeHistory(TenantRuntimeFrame frame, long? after, long? before, int limit, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (LoadTenantRuntime(frame, at) is null) return null;
            var hidden = FutureTenantEntries(ReadTenantRun(frame.Owner.TenantId!.Value, frame.RunId)!);
            var entries = _entries.GetValueOrDefault(frame.SessionId, []).Where(e => !hidden.Contains(e.EntryId));
            return after is { } sequence ? HistoryPaging.FromForward(entries.Where(e => e.Sequence > sequence).OrderBy(e => e.Sequence).Take(limit).ToArray(), sequence, limit)
                : HistoryPaging.FromNewestFirst(entries.Where(e => before is null || e.Sequence < before).OrderByDescending(e => e.Sequence).Take(limit).ToArray(), limit);
        }
    }
}
