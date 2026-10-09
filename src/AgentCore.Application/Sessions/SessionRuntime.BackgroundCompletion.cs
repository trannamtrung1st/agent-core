using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record BackgroundCompletionReceived(EventContext Context, BackgroundCompletionCandidate Source,
        TaskCompletionSource<bool> Committed) : SessionInput(Context);

    public async Task<bool> AdmitBackgroundCompletionAsync(BackgroundCompletionCandidate source, CancellationToken ct = default)
    {
        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new BackgroundCompletionReceived(NewContext(), source, committed), urgent: false)) return false;
        return await committed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleBackgroundCompletionAsync(BackgroundCompletionReceived input, CancellationToken ct)
    {
        try
        {
            var child = input.Source.Run;
            if (child.Owner != RunOwner || input.Source.Session.Origin.OriginatingSessionId != SessionId
                || !input.Source.Session.Origin.MayReportCompletion(child.AgentRunId))
            { input.Committed.TrySetResult(false); return; }
            if (await _agentRuns.HasCompletionReceiptAsync(child.Owner, child.AgentRunId, ct).ConfigureAwait(false))
            { input.Committed.TrySetResult(true); return; }
            if (_activeResponseId is not null || _brainEvaluationCts is not null || _agentRunAdmissionPending
                || _snapshot.PendingAgentInputIds.Count > 0 || (await _agentRuns.ListForSessionAsync(RunOwner, SessionId, ct).ConfigureAwait(false)).Any(r => !r.IsTerminal))
            { input.Committed.TrySetResult(false); return; }
            var now = _time.GetUtcNow();
            var model = _snapshot.ModelSelection;
            if (model is null)
            {
                await _agentRuns.SkipCompletionReportAsync(child.Owner, child.AgentRunId, "target-model-unavailable", now, ct).ConfigureAwait(false);
                input.Committed.TrySetResult(true);
                return;
            }
            var initial = await _store.ReadHistoryAsync(child.SessionId, 0, 1, ct).ConfigureAwait(false);
            var objective = initial.FirstOrDefault()?.Text ?? input.Source.Session.Title;
            var batch = new List<(AgentRun Run, string Objective)> { (child, objective) };
            var pending = await _agentRuns.ListCompletionInboxAsync(RunOwner, SessionId, AgentRunLimits.MaxListLimit, now, ct).ConfigureAwait(false);
            foreach (var item in pending.Where(i => i.Status == CompletionInboxStatus.Pending && i.ChildAgentRunId != child.AgentRunId))
            {
                var other = await _agentRuns.GetAsync(RunOwner, item.ChildAgentRunId, ct).ConfigureAwait(false);
                if (other is null || other.Result?.OutcomeKind == AgentRunOutcomeKind.NoAction
                    || _runAuthority is not null && await _runAuthority.CurrentDefinitionAsync(other, ct).ConfigureAwait(false) is null) continue;
                var first = await _store.ReadHistoryAsync(other.SessionId, 0, 1, ct).ConfigureAwait(false);
                batch.Add((other, first.FirstOrDefault()?.Text ?? "Background task"));
                break;
            }
            batch = batch.OrderBy(b => b.Run.UpdatedAtUtc).ThenBy(b => b.Run.AgentRunId).ToList();
            var primary = batch[0].Run;
            var evidence = batch.Count == 1 ? BackgroundCompletionProjection.Build(primary, batch[0].Objective,
                await _tools.CompletionArtifactsAsync(primary.SessionId, ct).ConfigureAwait(false)) : BackgroundCompletionProjection.BuildBatch(batch);
            var activation = new Activation(_ids.NewId(), SessionId, ActivationKind.BackgroundCompleted, [], primary.AgentRunId,
                null, primary.SessionId, primary.AgentRunId, "completion:" + string.Join(":", batch.Select(b => b.Run.AgentRunId.ToString("D"))), now, evidence);
            var skills = await _tools.ResolveSkillCatalogAsync(_snapshot.AgentInstanceId, _snapshot.Definition, ct).ConfigureAwait(false);
            var report = AgentRun.Create(_ids.NewId(), RunOwner, new(activation, _snapshot.Definition.Id, _snapshot.Definition.Version,
                _snapshot.PinnedPersona ?? _snapshot.Definition.Identity, _ids.NewId(), AgentRunOutputContract.CompletionReport,
                    await _tools.ResolveExecutionBudgetAsync(_snapshot.AgentInstanceId, _snapshot.Definition, false, ct)), new(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort),
                AgentRunLimits.DefaultMaxAttempts, now, skills, skills.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
            var current = _runAuthority is null ? _snapshot.Definition : await _runAuthority.CurrentDefinitionAsync(report, ct).ConfigureAwait(false);
            if (_deactivated || _snapshot.ArchivedAt is not null || _snapshot.DurablyDeletedAt is not null || SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus)
                || _snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
                || _snapshot.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(_snapshot.PauseReason)
                || current is null)
            {
                await _agentRuns.SkipCompletionReportAsync(child.Owner, child.AgentRunId, "parent-policy-unavailable", now, ct).ConfigureAwait(false);
                input.Committed.TrySetResult(true);
                return;
            }
            // The persistence queue belongs to this mailbox. Admission does not cancel active user work.
            RequestPersist(_snapshot, admittedRun: report, completionSourceRunIds: batch.Select(b => b.Run.AgentRunId).ToArray(), ended: input.Committed);
        }
        catch (Exception exception) when (exception is AgentCoreException or ArgumentException)
        { input.Committed.TrySetResult(false); }
    }
}
