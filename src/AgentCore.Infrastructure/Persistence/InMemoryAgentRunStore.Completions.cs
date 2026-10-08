using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentRunStore
{
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportBatchAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, IReadOnlyList<Guid> childRunIds, CancellationToken ct = default)
    {
        if (childRunIds.Count is < 1 or > 2 || childRunIds.Distinct().Count() != childRunIds.Count
            || childRunIds[0] != report.Admission.Activation.SourceAgentRunId) throw AgentCoreErrors.Validation("Completion batch requires one or two unique sources and its primary identity.");
        return ValueTask.FromResult(AdmitCore(parent, expectedRevision, report, ct, completionSourceRunId: childRunIds[0], additionalCompletionSources: childRunIds.Skip(1).ToArray()));
    }

    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, Guid childRunId, CancellationToken ct = default) =>
        ValueTask.FromResult(AdmitCore(parent, expectedRevision, report, ct, completionSourceRunId: childRunId));

    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListDeliveryCandidatesAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        lock (sessions.AdmissionGate)
        {
            RepairInbox(now);
            var candidates = State.CompletionReceipts.Values.Select(CompletionInboxMapping.Read).Where(i => i.Status == CompletionInboxStatus.Pending)
                .OrderBy(i => i.SourceFinishedAtUtc).ThenBy(i => i.ChildAgentRunId)
                .Select(i => new BackgroundCompletionCandidate(State.Runs[i.ChildAgentRunId], sessions.LoadMetadataAsync(i.ChildSessionId, ct).GetAwaiter().GetResult()!))
                .Where(c => c.Run.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction ||
                    !State.Runs.Values.Any(r => r.SessionId == c.Session.Origin.OriginatingSessionId && !r.IsTerminal)
                    && sessions.LoadMetadataAsync(c.Session.Origin.OriginatingSessionId!.Value, ct).GetAwaiter().GetResult()?.PendingAgentInputIds.Count is not > 0)
                .Take(limit).ToArray();
            return ValueTask.FromResult<IReadOnlyList<BackgroundCompletionCandidate>>(candidates);
        }
    }

    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        lock (sessions.AdmissionGate)
        {
            RepairInbox(DateTimeOffset.MinValue);
            var result = State.Runs.Values.Where(run => run.IsTerminal
                && State.CompletionReceipts.TryGetValue(run.AgentRunId, out var inbox) && CompletionInboxMapping.Read(inbox).Status == CompletionInboxStatus.Pending).OrderBy(run => run.UpdatedAtUtc).ThenBy(run => run.AgentRunId)
                .Select(run => new BackgroundCompletionCandidate(run, sessions.LoadMetadataAsync(run.SessionId, ct).GetAwaiter().GetResult()!))
                .Where(candidate => candidate.Session is not null && candidate.Session.Origin.MayReportCompletion(candidate.Run.AgentRunId))
                .Take(limit).ToArray();
            return ValueTask.FromResult<IReadOnlyList<BackgroundCompletionCandidate>>(result);
        }
    }

    public ValueTask<CompletionDeliveryState> GetCompletionDeliveryAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(childRunId, out var child) || child.Owner != owner) throw AgentCoreErrors.NotFound("Child run was not found.");
            var session = sessions.LoadMetadataAsync(child.SessionId, ct).GetAwaiter().GetResult() ?? throw AgentCoreErrors.NotFound("Child Session was not found.");
            State.CompletionReceipts.TryGetValue(childRunId, out var receipt);
            var report = receipt?.ParentActivationId is { } id ? State.Runs.Values.SingleOrDefault(r => r.ActivationId.ToString("D") == id && r.Owner == owner) : null;
            if (receipt is not null)
            {
                var item = CompletionInboxMapping.Read(receipt);
                if (item.Status == CompletionInboxStatus.Handled) return ValueTask.FromResult(new CompletionDeliveryState("handled", item.ParentSessionId, item.HandledByRunId, null));
                if (item.Status == CompletionInboxStatus.Claimed) return ValueTask.FromResult(new CompletionDeliveryState("claimed", item.ParentSessionId, item.ClaimRunId, null));
                if (item.Status == CompletionInboxStatus.Pending) return ValueTask.FromResult(new CompletionDeliveryState("pending", item.ParentSessionId, null, null));
            }
            return ValueTask.FromResult(CompletionDeliveryProjection.Build(session, report, receipt is not null, receipt?.SkipReason));
        }
    }

    public ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate) return ValueTask.FromResult(State.CompletionReceipts.TryGetValue(childRunId, out var receipt)
            && CompletionInboxMapping.Read(receipt).IsAccounted && receipt.AgentInstanceId == owner.AgentInstanceId.ToString("D") && receipt.ProfileId == owner.ProfileId.ToString("D"));
    }
    public ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(childRunId, out var child) || child.Owner != owner || !child.IsTerminal)
                throw AgentCoreErrors.NotFound("Completed child run was not found.");
            RepairInbox(now);
            var inserted = State.CompletionReceipts.TryGetValue(childRunId, out var row) && CompletionInboxMapping.Read(row).Status == CompletionInboxStatus.Pending;
            if (inserted)
            {
                var item = CompletionInboxMapping.Read(row!);
                CompletionInboxMapping.Write(row!, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = reason });
            }
            RuntimeTelemetry.RecordBackgroundSession(inserted ? "completion-skipped" : "completion-deduped");
        }
        return ValueTask.CompletedTask;
    }
}
