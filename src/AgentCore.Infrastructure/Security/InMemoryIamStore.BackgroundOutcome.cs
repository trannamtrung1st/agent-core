using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void SettleTenantCompletion(AgentRun next, Guid? generation)
    {
        var state = sessionBacking.AgentRuns;
        if (next.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome && next.IsTerminal && next.Result?.OutcomeKind != AgentRunOutcomeKind.Continued && !state.CompletionReceipts.ContainsKey(next.AgentRunId)
            && sessionBacking.ReadTenantSession(next.Owner.TenantId!.Value, next.SessionId) is { } child
            && child.Snapshot.Origin.ReportCompletionToOrigin && child.Snapshot.Origin.MayReportCompletion(TenantBackgroundLineage.InitialAsync(next, (id, _) => ValueTask.FromResult(sessionBacking.ReadRetainedTenantOperationRun(next.Owner.TenantId.Value, id)), CancellationToken.None).AsTask().GetAwaiter().GetResult().AgentRunId))
            state.CompletionReceipts.Add(next.AgentRunId, CompletionInboxMapping.Create(next, child.Snapshot));
        foreach (var row in state.CompletionReceipts.Values.Where(r => r.AgentInstanceId == next.AgentInstanceId.ToString("D") && r.ProfileId is null))
        {
            var item = CompletionInboxMapping.Read(row);
            if (item.Owner == next.Owner) CompletionInboxMapping.Write(row, TenantBackgroundCompletionRules.Settle(item, next, generation));
        }
    }
    public ValueTask<AgentRun> CommitTenantOutcomeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        AgentRunCommand.Complete completion, Guid? draftEntryId, Guid diagnosticId, CancellationToken ct = default, TenantConfigurationContinuationAdmission? configurationContinuation = null, TenantContextContinuationAdmission? contextContinuation = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireRuntimeOwner(frame.Owner);
            var run = sessionBacking.ReadTenantRun(frame.Owner.TenantId!.Value, frame.RunId) ?? throw new AccessDeniedException("run_unavailable");
            _ = sessionBacking.LoadTenantRuntime(frame, (clock ?? TimeProvider.System).GetUtcNow());
            var draft = draftEntryId is { } id ? sessionBacking.FindEntry(frame.SessionId, id) : null;
            if (draftEntryId is not null && draft is null) throw AgentCoreErrors.Conflict("Response draft was not found.");
            RequireTenantOutputSources(run, ct);
            var applyLearning = PrepareLearningCommit(frame, snapshot, completion.Learning, ct);
            AgentCore.Application.Tools.SessionCompletionIntent.ValidateOutcome(run, sessionBacking.ReadTenantSession(frame.Owner.TenantId!.Value, frame.SessionId)!.Snapshot, snapshot, (clock ?? TimeProvider.System).GetUtcNow());
            AgentRunStoreMapping.ValidateOutcome(snapshot, run, completion, draft);
            AgentRun next; try { next = completion.Apply(run, () => diagnosticId); }
            catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentRunStoreMapping.Map(ex); }
            if (configurationContinuation is not null && contextContinuation is not null) throw new SecurityConflictException("continuation_conflict");
            var admitConfiguration = PrepareConfigurationContinuation(next, snapshot, configurationContinuation, ct);
            var admitContext = PrepareContextContinuation(next, snapshot, contextContinuation, ct);
            SaveTenantRuntimeState(frame, snapshot, expectedRevision, (clock ?? TimeProvider.System).GetUtcNow());
            applyLearning();
            if (draftEntryId is { } removed) sessionBacking.RemoveResponseDraft(run.SessionId, removed);
            sessionBacking.SaveTenantOperationRun(next, run.Revision); AppendTenantCoreRun(run, next); SettleTenantCompletion(next, run.Claim?.Generation);
            admitConfiguration?.Invoke(); admitContext?.Invoke();
            return ValueTask.FromResult(next);
        }
    }
}
