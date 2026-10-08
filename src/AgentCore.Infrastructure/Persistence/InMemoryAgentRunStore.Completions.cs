using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentRunStore
{
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision,
        AgentRun report, Guid childRunId, CancellationToken ct = default) =>
        ValueTask.FromResult(AdmitCore(parent, expectedRevision, report, ct, completionSourceRunId: childRunId));

    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (limit is < 1 or > AgentRunLimits.MaxListLimit) throw AgentCoreErrors.Validation("Completion query requires a bounded limit.");
        lock (sessions.AdmissionGate)
        {
            var result = State.Runs.Values.Where(run => run.IsTerminal && run.Admission.Activation.Kind == ActivationKind.ImmediateBackground
                && !State.CompletionReceipts.ContainsKey(run.AgentRunId)).OrderBy(run => run.UpdatedAtUtc).ThenBy(run => run.AgentRunId)
                .Select(run => new BackgroundCompletionCandidate(run, sessions.LoadMetadataAsync(run.SessionId, ct).GetAwaiter().GetResult()!))
                .Where(candidate => candidate.Session is not null && candidate.Session.Origin.MayReportCompletion(candidate.Run.AgentRunId))
                .Take(limit).ToArray();
            return ValueTask.FromResult<IReadOnlyList<BackgroundCompletionCandidate>>(result);
        }
    }

    public ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate) return ValueTask.FromResult(State.CompletionReceipts.TryGetValue(childRunId, out var receipt)
            && receipt.AgentInstanceId == owner.AgentInstanceId.ToString("D") && receipt.ProfileId == owner.ProfileId.ToString("D"));
    }
    public ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (sessions.AdmissionGate)
        {
            if (!State.Runs.TryGetValue(childRunId, out var child) || child.Owner != owner || !child.IsTerminal)
                throw AgentCoreErrors.NotFound("Completed child run was not found.");
            var inserted = State.CompletionReceipts.TryAdd(childRunId, new() { ChildAgentRunId = childRunId.ToString("D"),
                AgentInstanceId = owner.AgentInstanceId.ToString("D"), ProfileId = owner.ProfileId.ToString("D"),
                SkipReason = reason, CreatedAtUtc = now.ToUnixTimeMilliseconds() });
            RuntimeTelemetry.RecordBackgroundSession(inserted ? "completion-skipped" : "completion-deduped");
        }
        return ValueTask.CompletedTask;
    }
}
