using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private ControllerSnapshot SnapshotController() =>
        new(
            _snapshot.Status,
            _snapshot.Mode,
            _input,
            _outputActivity,
            _candidate,
            _activeResponseId,
            _responseLifecycle,
            _timerGeneration,
            _turnGeneration,
            _recognition,
            _policy,
            _committedUtteranceId,
            _activityScore,
            _time.GetUtcNow());

    private TimeSpan UtteranceDuration() =>
        _utteranceStarted is { } started
            ? _time.GetUtcNow() - started
            : TimeSpan.Zero;

    private async Task HandleAttachAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        _snapshot = _snapshot with { Status = SessionStatus.Attached, UpdatedAt = _time.GetUtcNow() };
        _input = _snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSpeechAsync(SpeechEvidenceReceived input, CancellationToken cancellationToken)
    {
        _timerGeneration++;
        if (input.Evidence is SpeechStarted)
        {
            _activeUtteranceId = input.Evidence.UtteranceId;
            _utteranceStarted = _time.GetUtcNow();
            _activityScore = input.ActivityScore;
        }
        else if (input.ActivityScore is { } score)
        {
            _activityScore = score;
        }

        var evaluation = InteractionController.EvaluateSpeech(
            SnapshotController(),
            input.Evidence,
            _activityScore,
            UtteranceDuration(),
            _ids.NewId());
        await ApplyEvaluationAsync(input.Context, evaluation, cancellationToken).ConfigureAwait(false);
        if (input.Evidence is SpeechEnded && _committedUtteranceId == input.Evidence.UtteranceId)
        {
            _input = _snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        }
    }

    private async Task HandleClassifierAsync(ClassifierReturned input, CancellationToken cancellationToken)
    {
        if (_candidate is null
            || !InteractionController.ClassifierMatches(
                _candidate,
                input.CandidateId,
                input.UtteranceId,
                input.ResponseId,
                input.Revision))
        {
            return;
        }

        var evaluation = input.Decision switch
        {
            InteractionDecision.Interrupt => new ControllerEvaluation(
                InteractionDecision.Interrupt,
                _input,
                _candidate,
                false,
                null,
                false,
                null),
            InteractionDecision.Ignore => new ControllerEvaluation(
                InteractionDecision.Ignore,
                _input,
                null,
                false,
                null,
                false,
                null),
            _ => new ControllerEvaluation(
                InteractionDecision.Continue,
                _input,
                null,
                false,
                null,
                false,
                null)
        };
        await ApplyEvaluationAsync(input.Context, evaluation, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTimerAsync(TimerElapsedReceived input, CancellationToken cancellationToken)
    {
        if (!InteractionController.TimerMatches(_timerGeneration, input.Generation))
        {
            return;
        }

        if (input.Kind == "candidate")
        {
            var evaluation = InteractionController.EvaluateCandidateTimer(SnapshotController(), UtteranceDuration());
            await ApplyEvaluationAsync(input.Context, evaluation, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (input.Kind == "idle"
            && _snapshot.Status == SessionStatus.Attached
            && _input is InputActivity.Idle or InputActivity.Listening
            && _outputActivity == OutputActivity.Idle
            && _activeResponseId is null)
        {
            var responseId = _ids.NewId();
            var turn = ++_turnGeneration;
            var trigger = new AgentTrigger(input.Context.EventId, TriggerKind.LongSilence, Text: null);
            LaunchBrain(input.Context, trigger, responseId, turn);
        }
    }

    private async Task ApplyEvaluationAsync(
        EventContext context,
        ControllerEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        _input = evaluation.Input;
        _candidate = evaluation.Candidate;
        LastControllerDecision = evaluation.Decision;
        if (evaluation.DiscardUtterance)
        {
            _activeUtteranceId = null;
            _utteranceStarted = null;
        }

        if (evaluation.Decision is InteractionDecision.Interrupt && _activeResponseId is { } live)
        {
            await SupersedeAsync(context, live, cancellationToken).ConfigureAwait(false);
        }

        if (evaluation.CommitUserTurn && evaluation.UserText is { } text)
        {
            await CommitSpeechTurnAsync(context, text, cancellationToken).ConfigureAwait(false);
        }

        if (evaluation.Decision is InteractionDecision.RequestInterruptionClassification
            && evaluation.ClassifierContext is { } classifierContext
            && _candidate is { } candidate)
        {
            LaunchClassifier(context, candidate, classifierContext);
        }
    }

    private async Task CommitSpeechTurnAsync(EventContext context, string text, CancellationToken cancellationToken)
    {
        if (_committedUtteranceId == _activeUtteranceId && _activeUtteranceId is not null)
        {
            return;
        }

        _committedUtteranceId = _activeUtteranceId;
        var now = _time.GetUtcNow();
        var userEntry = new ConversationEntry(
            context.EventId,
            NextSequence(),
            context.EventId,
            ConversationRole.User,
            text,
            ResponseId: null,
            EntryStatus.Completed,
            _snapshot.Mode,
            text.Length,
            text.Length,
            now);
        await PersistAsync(Append(userEntry) with { Status = _snapshot.Status }, cancellationToken)
            .ConfigureAwait(false);
        var responseId = _ids.NewId();
        var trigger = new AgentTrigger(context.EventId, TriggerKind.UserTurn, text);
        var turn = ++_turnGeneration;
        _outputActivity = OutputActivity.WaitingForAgent;
        LaunchBrain(context, trigger, responseId, turn);
    }

    private void LaunchClassifier(EventContext cause, InterruptionCandidate candidate, InterruptionContext context)
    {
        var captured = candidate;
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                var decision = await _classifier.ClassifyAsync(context, _lifetime.Token).ConfigureAwait(false);
                BeginWork();
                if (!_mailbox.Writer.TryWrite(
                        new ClassifierReturned(
                            NewContext(cause.EventId),
                            captured.CandidateId,
                            captured.UtteranceId,
                            captured.ResponseId,
                            captured.Revision,
                            decision)))
                {
                    EndWork();
                }
            }
            finally
            {
                EndWork();
            }
        }, CancellationToken.None);
    }
}
