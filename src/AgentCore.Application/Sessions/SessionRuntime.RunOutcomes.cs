using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record AgentRunOutcomeCommit(AgentRunOwner Owner, Guid AgentRunId,
        AgentRunCommand.Complete Completion, Guid? DraftEntryId);
    private sealed record AgentRunOutcomeReceived(EventContext Context, Guid ResponseId, Guid AgentRunId,
        Guid Generation, AgentRunOutcomeKind Kind, string Summary, TaskCompletionSource<bool> Accepted) : SessionInput(Context);

    private bool IsInitialBackgroundRun => _boundAgentRun is { } run
        && run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome;

    private async Task<bool> RequestRunOutcomeAsync(EventContext cause, Guid responseId,
        AgentRunOutcomeKind kind, string summary, CancellationToken ct)
    {
        var run = _boundAgentRun;
        if (run?.Claim is null) return false;
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new AgentRunOutcomeReceived(WorkerContext(cause), responseId, run.AgentRunId,
            cause.AgentRunGeneration ?? run.Claim.Generation, kind, summary, accepted), urgent: true)) return false;
        return await accepted.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleRunOutcomeAsync(AgentRunOutcomeReceived input, CancellationToken ct)
    {
        try
        {
            if (_activeResponseId != input.ResponseId || _responseTerminal
                || input.Context.Epoch != _epoch || _boundAgentRun?.AgentRunId != input.AgentRunId)
            { input.Accepted.TrySetResult(false); return; }
            var run = await _agentRuns.GetAsync(RunOwner, input.AgentRunId, ct).ConfigureAwait(false);
            if (run is not { Status: AgentRunStatus.Running, Claim: { } claim, CancellationRequested: false }
                || claim.Generation != input.Generation || claim.LeaseExpiresAtUtc <= _time.GetUtcNow())
            { input.Accepted.TrySetResult(false); return; }
            await CommitRunOutcomeFromMailboxAsync(input.Context, run, input.Kind, input.Summary, ct).ConfigureAwait(false);
            input.Accepted.TrySetResult(true);
        }
        catch (Exception exception) { input.Accepted.TrySetException(exception); throw; }
    }

    private async Task CommitRunOutcomeFromMailboxAsync(EventContext cause, AgentRun run,
        AgentRunOutcomeKind kind, string summary, CancellationToken ct)
    {
        var draft = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == run.ResponseId);
        Guid? draftId = null;
        ConversationEntry? outcomeEntry = null;
        if (kind == AgentRunOutcomeKind.NoAction)
        {
            draftId = draft?.EntryId;
            _snapshot = _snapshot with { Entries = _snapshot.Entries.Where(entry => entry.EntryId != draftId).ToArray() };
        }
        else
        {
            var now = _time.GetUtcNow();
            outcomeEntry = new ConversationEntry(draft?.EntryId ?? _ids.NewId(), draft?.Sequence ?? NextSequence(), null,
                ConversationRole.Assistant, summary, run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, summary.Length,
                draft?.CreatedAt ?? now, Envelope: new ResponseEnvelope(summary, null, [], ResponseSpeechMode.None),
                ModelProvenance: new(run.PinnedModel.CatalogKey, run.PinnedModel.ProviderAlias, run.PinnedModel.ModelId, run.PinnedModel.ReasoningEffort));
            _snapshot = draft is null ? Append(outcomeEntry)
                : _snapshot with { Entries = _snapshot.Entries.Select(entry => entry.EntryId == draft.EntryId ? outcomeEntry : entry).ToArray() };
        }
        var completion = new AgentRunCommand.Complete(run.Revision, _time.GetUtcNow(), run.Claim!.Generation,
            summary, kind, outcomeEntry?.EntryId);
        _responseTerminal = true;
        _responseLifecycle = ResponseLifecycle.Completed;
        DiscardStagedMemory();
        await FinishOwnedProgressAsync(cause, ResponseProgressState.Completed, ct, run.ResponseId).ConfigureAwait(false);
        await PublishOutputIdleAsync(cause, ct).ConfigureAwait(false);
        RequestPersist(_snapshot, userConversationTerminal: true,
            runOutcome: new(run.Owner, run.AgentRunId, completion, draftId), then: async token =>
            {
                if (outcomeEntry is not null)
                    await PublishAsync(new SessionOutput(cause, run.ResponseId, new HistoryEntryUpsertOutput(PublicHistory.FromEntry(outcomeEntry))), token).ConfigureAwait(false);
                else if (draftId is { } removed)
                    await PublishAsync(new SessionOutput(cause, run.ResponseId, new HistoryEntryRemovedOutput(removed)), token).ConfigureAwait(false);
                await PublishAsync(new SessionOutput(cause, run.ResponseId, new ResponseCompletedOutput(false, HeardTextEndExclusive: 0)), token).ConfigureAwait(false);
                if (_boundAgentRun?.AgentRunId == run.AgentRunId) _boundAgentRun = null;
                ClearAgentRunTerminalPending();
                if (_activeResponseId == run.ResponseId) ClearActive();
                await AfterResponseTerminalizedAsync(cause, token).ConfigureAwait(false);
            });
    }
}
