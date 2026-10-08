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
            var now = _time.GetUtcNow();
            var model = _snapshot.ModelSelection ?? throw AgentCoreErrors.Validation("Completion report requires a parent model.");
            var initial = await _store.ReadHistoryAsync(child.SessionId, 0, 1, ct).ConfigureAwait(false);
            var objective = initial.FirstOrDefault()?.Text ?? input.Source.Session.Title;
            var artifacts = await _tools.CompletionArtifactsAsync(child.SessionId, ct).ConfigureAwait(false);
            var evidence = BackgroundCompletionProjection.Build(child, objective, artifacts);
            var activation = new Activation(_ids.NewId(), SessionId, ActivationKind.BackgroundCompleted, [], child.AgentRunId,
                null, child.SessionId, child.AgentRunId, $"completion:{child.SessionId:D}:{child.AgentRunId:D}", now,
                evidence);
            var skills = await _tools.ResolveSkillCatalogAsync(_snapshot.AgentInstanceId, _snapshot.Definition, ct).ConfigureAwait(false);
            var report = AgentRun.Create(_ids.NewId(), RunOwner, new(activation, _snapshot.Definition.Id, _snapshot.Definition.Version,
                _snapshot.PinnedPersona ?? _snapshot.Definition.Identity, _ids.NewId()), new(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort),
                AgentRunLimits.DefaultMaxAttempts, now, skills, skills.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
            var current = _runAuthority is null ? _snapshot.Definition : await _runAuthority.CurrentDefinitionAsync(report, ct).ConfigureAwait(false);
            if (_deactivated || _snapshot.ArchivedAt is not null || _snapshot.DurablyDeletedAt is not null || SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus)
                || _snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
                || _snapshot.Status == SessionStatus.Paused && !SessionPauseSemantics.IsTransportResumable(_snapshot.PauseReason)
                || current?.InitiativePolicy.Enabled != true)
            {
                await _agentRuns.SkipCompletionReportAsync(child.Owner, child.AgentRunId, "parent-policy-unavailable", now, ct).ConfigureAwait(false);
                input.Committed.TrySetResult(true);
                return;
            }
            // The persistence queue belongs to this mailbox. Admission does not cancel active user work.
            RequestPersist(_snapshot, admittedRun: report, completionSourceRunId: child.AgentRunId, ended: input.Committed);
        }
        catch (Exception exception) when (exception is AgentCoreException or ArgumentException)
        { input.Committed.TrySetResult(false); }
    }
}
