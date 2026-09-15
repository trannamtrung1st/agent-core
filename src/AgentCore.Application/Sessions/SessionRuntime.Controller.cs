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

    private async Task HandleAttachAsync(AttachReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        _snapshot = _snapshot with
        {
            Status = SessionStatus.Attached,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        _input = _snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        if (_snapshot.Mode == SessionMode.Voice)
        {
            _streamId ??= _ids.NewId();
        }

        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await PublishAsync(new SessionOutput(input.Context, null, new ReadyOutput(BuildReady())), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleDetachAsync(DetachReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(input.Context, live, cancellationToken, "disconnected").ConfigureAwait(false);
        }

        _snapshot = _snapshot with
        {
            Status = SessionStatus.Paused,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        _input = InputActivity.Idle;
        _streamId = null;
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetModeAsync(SetModeReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new ErrorOutput("Session", "ValidationError", "Session has ended.", false, null)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (input.Mode == SessionMode.Voice && !_snapshot.Definition.Voice.Enabled)
        {
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new ErrorOutput("Session", "VoiceUnavailable", "Voice is not available for this agent.", false, null)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (input.Mode == SessionMode.Text && _snapshot.PendingMode == SessionMode.Voice)
        {
            _timerGeneration++;
            _snapshot = _snapshot with { PendingMode = null, UpdatedAt = _time.GetUtcNow() };
            await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (input.Mode == SessionMode.Voice && _activeResponseId is not null)
        {
            _snapshot = _snapshot with { PendingMode = SessionMode.Voice, UpdatedAt = _time.GetUtcNow() };
            _pendingVoiceGeneration = ++_timerGeneration;
            await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            var generation = _pendingVoiceGeneration;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_policy.PendingVoiceTimeoutMs), _time, _lifetime.Token)
                        .ConfigureAwait(false);
                    BeginWork();
                    if (!_mailbox.Writer.TryWrite(new TimerElapsedReceived(NewContext(), "pendingVoice", generation, null)))
                    {
                        EndWork();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
            return;
        }

        if (input.Mode == SessionMode.Text && _activeResponseId is { } live && _snapshot.Mode == SessionMode.Voice)
        {
            await SupersedeAsync(input.Context, live, cancellationToken, "modeChange").ConfigureAwait(false);
        }

        await ApplyModeAsync(input.Mode, cancellationToken).ConfigureAwait(false);
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyModeAsync(SessionMode mode, CancellationToken cancellationToken)
    {
        _snapshot = _snapshot with
        {
            Mode = mode,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        if (mode == SessionMode.Voice)
        {
            _input = InputActivity.Listening;
            _streamId = _ids.NewId();
        }
        else
        {
            _input = InputActivity.Idle;
            _streamId = null;
        }

        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    private SessionReadyProjection BuildReady()
    {
        var voice = _snapshot.Mode == SessionMode.Voice;
        var history = _snapshot.Entries.TakeLast(50).Select(PublicHistory.FromEntry).ToArray();
        return new SessionReadyProjection(
            _snapshot.Mode,
            _snapshot.PendingMode,
            _snapshot.Status,
            PublicHistory.FromDefinition(_snapshot.Definition, _snapshot.Definition.Voice.Enabled),
            voice ? _streamId : null,
            voice ? CanonicalAudio.Format : null,
            voice
                ? _recognition
                : new RecognitionCapabilities(false, false, false, false),
            voice
                ? new SynthesisCapabilities(false, false, false, false, false, [])
                : new SynthesisCapabilities(false, false, false, false, false, []),
            voice ? _policy.BargeInPolicy : "none",
            history.Length == 0 ? 0 : history[^1].Sequence,
            history,
            ActiveResponseId: null);
    }

    private async Task PublishStateAsync(EventContext context, CancellationToken cancellationToken) =>
        await PublishAsync(
                new SessionOutput(
                    context,
                    null,
                    new StateChangedOutput(
                        _snapshot.Status,
                        _snapshot.Mode,
                        _snapshot.PendingMode,
                        _input.ToString(),
                        _outputActivity.ToString(),
                        Muted: false,
                        _streamId)),
                cancellationToken)
            .ConfigureAwait(false);

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

        if (input.Kind == "pendingVoice")
        {
            if (_snapshot.PendingMode == SessionMode.Voice)
            {
                _snapshot = _snapshot with { PendingMode = null, Mode = SessionMode.Text, UpdatedAt = _time.GetUtcNow() };
                _streamId = null;
                _input = InputActivity.Idle;
                await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
                await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            }

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
            await SupersedeAsync(context, live, cancellationToken, "userBargeIn").ConfigureAwait(false);
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
