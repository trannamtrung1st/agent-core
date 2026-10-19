using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Diagnostics;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record BrainEvaluationFinished(EventContext Context, CancellationTokenSource Evaluation) : SessionInput(Context);

    private async Task HandleBrainFailedAsync(BrainFailed input, CancellationToken ct)
    {
        if (_deactivated || input.TurnGeneration != _turnGeneration || input.Context.Epoch != _epoch) return;
        if (_boundAgentRun is { } bound && _snapshot.Entries.Any(e => e.Role == ConversationRole.Assistant
            && e.ResponseId == input.ResponseId && e.Status == EntryStatus.Streaming))
        {
            if (bound.ResponseId != input.ResponseId || bound.Claim?.Generation != input.Context.AgentRunGeneration
                || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false)) return;
            var diagnostic = input.DiagnosticId ?? _diagnostics.NewId();
            var failed = input with { DiagnosticId = diagnostic };
            var at = _time.GetUtcNow();
            _snapshot = _snapshot with { UpdatedAt = at, Entries = _snapshot.Entries.Select(e => e.Role == ConversationRole.Assistant
                && e.ResponseId == input.ResponseId && e.Status == EntryStatus.Streaming
                ? e with { Status = EntryStatus.Failed, CompletedAt = e.CompletedAt ?? at, FinishReason = null, InterruptReason = null,
                    Failure = new FailureReference(diagnostic, "Session", "BrainFailed", CorrelationOrNull(input.Context)) } : e).ToArray() };
            // A resumed approval may already own visible/durable progress. Close that entry through
            // the incumbent noncoalesced writer before settling the Run; never fabricate a new answer.
            RequestPersist(_snapshot, then: token => FinishBrainFailureAsync(failed, token));
            return;
        }
        await FinishBrainFailureAsync(input, ct).ConfigureAwait(false);
    }

    private async Task QueueBrainFailureAsync(EventContext cause, AgentTrigger trigger, Guid responseId, int turn,
        bool recoverable, Guid? diagnosticId = null, bool authorityUnavailable = false)
    {
        if (_lifetime.IsCancellationRequested) return;
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new BrainFailed(WorkerContext(cause), turn, responseId, trigger, recoverable, null, processed,
            diagnosticId, authorityUnavailable), urgent: true)) return;
        try { await processed.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
}
