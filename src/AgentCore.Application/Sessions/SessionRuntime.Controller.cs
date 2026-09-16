using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using System.Diagnostics;

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

    private bool HasLiveAssistantOutput =>
        _responseLifecycle is ResponseLifecycle.Live
        || _outputActivity is OutputActivity.AgentGenerating
            or OutputActivity.WaitingForAgent
            or OutputActivity.AgentSpeaking
        || _activeResponseId is not null && !_responseTerminal;

    private TimeSpan UtteranceDuration() =>
        _utteranceStarted is { } started
            ? _time.GetUtcNow() - started
            : TimeSpan.Zero;

    private async Task HandleAttachAsync(AttachReceived input, CancellationToken cancellationToken)
    {
        using var activity = RuntimeTelemetry.Activity.StartActivity("attach");
        var started = Stopwatch.GetTimestamp();
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            input.Attached.TrySetResult(false);
            return;
        }

        if (_snapshot.ProfileId is { } profileId)
        {
            _profile = await _store.LoadProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
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
            lock (_audioGate)
            {
                _streamId ??= _ids.NewId();
            }

            try
            {
                await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
                input.Attached.TrySetResult(false);
                return;
            }
        }

        RequestPersist(
            _snapshot,
            then: async ct =>
            {
                await PublishAsync(new SessionOutput(input.Context, null, new ReadyOutput(BuildReady())), ct)
                    .ConfigureAwait(false);
                RuntimeTelemetry.Record("attach", RuntimeTelemetry.ElapsedMs(started));
                ScheduleIdleTimer(SilenceThreshold());
            },
            ended: input.Attached);
    }

    private async Task HandleDetachAsync(DetachReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            AbandonLiveSpeech(rotateEpoch: true);
            _input = InputActivity.Idle;
            await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
            InvalidateSpeechJobs();
            _ttsCts?.Cancel();
            return;
        }

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
        _muted = false;
        _environmentQueue.Clear();
        AbandonLiveSpeech(rotateEpoch: true);
        _input = InputActivity.Idle;
        InvalidateSpeechJobs();
        _ttsCts?.Cancel();
        await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        RequestPersist(
            _snapshot,
            PersistKind.Pause,
            then: ct => PublishStateAsync(input.Context, ct));
    }

    private async Task HandleMuteAsync(MuteReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Mode != SessionMode.Voice)
        {
            return;
        }

        if (input.Muted == _muted)
        {
            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        _muted = input.Muted;
        if (_muted)
        {
            AbandonLiveSpeech(rotateEpoch: false);
        }
        else
        {
            lock (_audioGate)
            {
                SetStreamId(_ids.NewId());
                _expectedFrameSequence = 1;
                _expectedSampleOffset = 0;
            }
        }

        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetModeAsync(SetModeReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Paused)
        {
            if (input.Mode == SessionMode.Voice)
            {
                _snapshot = _snapshot with { PendingMode = SessionMode.Voice, UpdatedAt = _time.GetUtcNow() };
            }
            else
            {
                _snapshot = _snapshot with
                {
                    Mode = SessionMode.Text,
                    PendingMode = null,
                    UpdatedAt = _time.GetUtcNow()
                };
            }

            RequestPersist(_snapshot, then: ct => PublishStateAsync(input.Context, ct));
            return;
        }

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
            RequestPersist(_snapshot, then: ct => PublishStateAsync(input.Context, ct));
            return;
        }

        if (input.Mode == SessionMode.Voice && HasLiveAssistantOutput)
        {
            _snapshot = _snapshot with { PendingMode = SessionMode.Voice, UpdatedAt = _time.GetUtcNow() };
            _pendingVoiceGeneration = ++_timerGeneration;
            RequestPersist(_snapshot, then: ct => PublishStateAsync(input.Context, ct));
            var generation = _pendingVoiceGeneration;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_policy.PendingVoiceTimeoutMs), _time, _lifetime.Token)
                        .ConfigureAwait(false);
                    BeginWork();
                    if (!TryMailbox(new TimerElapsedReceived(NewContext(), "pendingVoice", generation, null)))
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
        RequestPersist(_snapshot, then: ct => PublishStateAsync(input.Context, ct));
    }

    private async Task ApplyModeAsync(SessionMode mode, CancellationToken cancellationToken)
    {
        _snapshot = _snapshot with
        {
            Mode = mode,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        if (_snapshot.Status is SessionStatus.Paused)
        {
            return;
        }

        if (mode == SessionMode.Voice)
        {
            AbandonLiveSpeech(rotateEpoch: true);
            try
            {
                await StartRecognitionAsync(cancellationToken, _ids.NewId()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await FailVoiceAsync(NewContext(), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            AbandonLiveSpeech(rotateEpoch: true);
            _muted = false;
            await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        }
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
                ? _synthesizer?.Capabilities ?? new SynthesisCapabilities(false, false, false, false, false, [])
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
                        Muted: _muted,
                        _streamId)),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task HandleSpeechAsync(SpeechEvidenceReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Status != SessionStatus.Attached || _snapshot.Mode != SessionMode.Voice)
        {
            return;
        }

        if (input.Epoch != _speechEpoch)
        {
            return;
        }

        if (input.Evidence is not SpeechStarted
            && input.Evidence.UtteranceId != _activeUtteranceId)
        {
            return;
        }

        _timerGeneration++;
        if (input.Evidence is SpeechStarted)
        {
            _activeUtteranceId = input.Evidence.UtteranceId;
            _utteranceStarted = _time.GetUtcNow();
            _activityScore = input.ActivityScore;
            _sttMark = Stopwatch.GetTimestamp();
            _recordedStt = false;
        }
        else if (input.ActivityScore is { } score)
        {
            _activityScore = score;
        }

        var duration = input.Evidence is SpeechStarted ? TimeSpan.Zero : UtteranceDuration();
        var evaluation = InteractionController.EvaluateSpeech(
            SnapshotController(),
            input.Evidence,
            _activityScore,
            duration,
            _ids.NewId());
        await ApplyEvaluationAsync(input.Context, evaluation, cancellationToken).ConfigureAwait(false);
        if (input.Evidence is SpeechStarted && _utteranceStarted is not null)
        {
            ScheduleMaxUtteranceTimer(input.Evidence.UtteranceId);
        }
        if (input.Evidence is SpeechStarted && evaluation.Decision is InteractionDecision.Queue && evaluation.Candidate is { } queued)
        {
            await PublishGainAsync(input.Context, 0.2, queued.CandidateId, cancellationToken).ConfigureAwait(false);
            if (!_recognition.PartialTranscripts || _policy.BargeInPolicy == "speechActivity")
            {
                ScheduleCandidateTimer(queued.UtteranceId);
            }
        }
        if (input.Evidence is SpeechPartial partial)
        {
            if (!_recordedStt)
            {
                _recordedStt = true;
                RuntimeTelemetry.Record("stt", RuntimeTelemetry.ElapsedMs(_sttMark));
            }
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new TranscriptPartialOutput(partial.UtteranceId, partial.Revision, partial.Text)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (input.Evidence is SpeechFinal final)
        {
            if (!_recordedStt)
            {
                _recordedStt = true;
                RuntimeTelemetry.Record("stt", RuntimeTelemetry.ElapsedMs(_sttMark));
            }
            var committed = _snapshot.Entries.LastOrDefault(entry =>
                entry.Role == ConversationRole.User && entry.Text == final.Text);
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new TranscriptFinalOutput(final.UtteranceId, final.Text, committed?.EntryId, committed?.Sequence)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

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
        if (input.Kind == "segment")
        {
            if (input.Generation != _segmentTimerGeneration || _segmenter is null || !UsesVoicePlayback)
            {
                return;
            }

            _segmentTimerArmed = false;
            EnqueueSegments(_segmenter.Tick(_time.GetUtcNow()));
            if (_segmenter.HasBuffered)
            {
                ScheduleSegmentTimer();
            }

            KickTts(input.Context);
            await TryCompleteVoiceAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (input.Kind == "pendingVoice")
        {
            if (input.Generation != _pendingVoiceGeneration)
            {
                return;
            }

            if (_snapshot.PendingMode == SessionMode.Voice)
            {
                _snapshot = _snapshot with { PendingMode = null, Mode = SessionMode.Text, UpdatedAt = _time.GetUtcNow() };
                SetStreamId(null);
                _input = InputActivity.Idle;
                RequestPersist(_snapshot, then: ct => PublishStateAsync(input.Context, ct));
            }

            return;
        }

        if (input.Kind == "idle")
        {
            if (!InteractionController.TimerMatches(_timerGeneration, input.Generation))
            {
                return;
            }

            _timerGeneration++;
            if (CanEvaluateIdle())
            {
                _silentEvaluations++;
                var responseId = _ids.NewId();
                var turn = ++_turnGeneration;
                _outputActivity = OutputActivity.WaitingForAgent;
                var trigger = new AgentTrigger(input.Context.EventId, TriggerKind.LongSilence, Text: null);
                LaunchBrain(input.Context, trigger, responseId, turn);
            }
            else if (ShouldRetryIdleAfterCooldown())
            {
                ScheduleIdleTimer(RemainingCooldown());
            }

            return;
        }

        if (input.Kind == "maxUtterance")
        {
            if (input.Generation != _maxUtteranceGeneration
                || input.UtteranceId is null
                || input.UtteranceId != _activeUtteranceId)
            {
                return;
            }

            var utteranceId = input.UtteranceId.Value;
            TryAdmitBoundary(utteranceId, SpeechBoundary.Ended, 0);
            AbandonLiveSpeech(rotateEpoch: true);
            await StopRecognitionAsync(_ids.NewId(), assignStreamId: true).ConfigureAwait(false);
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new ErrorOutput(
                            "Transport",
                            "MaxUtterance",
                            "Utterance exceeded 30 seconds and was closed.",
                            false,
                            null)),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
                return;
            }

            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!InteractionController.TimerMatches(_timerGeneration, input.Generation))
        {
            return;
        }

        if (input.Kind == "candidate")
        {
            var evaluation = InteractionController.EvaluateCandidateTimer(SnapshotController(), UtteranceDuration());
            await ApplyEvaluationAsync(input.Context, evaluation, cancellationToken).ConfigureAwait(false);
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

        if (evaluation.Decision is InteractionDecision.Continue or InteractionDecision.Ignore)
        {
            await PublishGainAsync(context, 1.0, evaluation.Candidate?.CandidateId, cancellationToken).ConfigureAwait(false);
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
        NoteUserActivity();
        _environmentQueue.Clear();
        _timerGeneration++;
        var turn = ++_turnGeneration;
        var now = _time.GetUtcNow();
        IReadOnlyList<Guid> staged = [];
        if (_attachments is not null)
        {
            var pending = await _attachments.ListStagedPendingAsync(SessionId, cancellationToken).ConfigureAwait(false);
            staged = pending.Select(item => item.AttachmentId).ToArray();
            if (staged.Count > 0)
            {
                await _attachments.ValidateBindableAsync(SessionId, staged, cancellationToken).ConfigureAwait(false);
            }
        }

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
        var titleHints = await AttachmentTitleHintsAsync(staged, cancellationToken).ConfigureAwait(false);
        _snapshot = Append(userEntry, titleHints) with { Status = _snapshot.Status };
        var cause = context;
        RequestPersist(
            _snapshot,
            then: async _ =>
            {
                if (staged.Count > 0 && _attachments is not null)
                {
                    await _attachments.BindToEntryAsync(SessionId, userEntry.EntryId, staged, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                var responseId = _ids.NewId();
                var trigger = new AgentTrigger(cause.EventId, TriggerKind.UserTurn, text);
                _outputActivity = OutputActivity.WaitingForAgent;
                LaunchPreparedTurn(cause, trigger, responseId, turn, staged);
            });
    }

    private string? LastInterruptedHeardText()
    {
        var interrupted = _snapshot.Entries.LastOrDefault(entry =>
            entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Interrupted);
        if (interrupted is null)
        {
            return null;
        }

        var text = PromptContextBuilder.EligibleAssistantText(interrupted);
        return text.Length == 0 ? null : text;
    }

    private void ScheduleCandidateTimer(Guid utteranceId)
    {
        var generation = _timerGeneration;
        var delay = _policy.BargeInPolicy == "semantic" && _recognition.PartialTranscripts
            ? TimeSpan.FromMilliseconds(_policy.SustainedInterruptMs)
            : TimeSpan.FromMilliseconds(_policy.DegradedInterruptMs);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
                BeginWork();
                if (!TryMailbox(new TimerElapsedReceived(NewContext(), "candidate", generation, utteranceId)))
                {
                    EndWork();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void ScheduleMaxUtteranceTimer(Guid utteranceId)
    {
        var generation = ++_maxUtteranceGeneration;
        var delay = TimeSpan.FromSeconds(Math.Max(1, _policy.MaxUtteranceSeconds));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
                BeginWork();
                if (!TryMailbox(new TimerElapsedReceived(NewContext(), "maxUtterance", generation, utteranceId)))
                {
                    EndWork();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task PublishGainAsync(
        EventContext context,
        double gain,
        Guid? candidateId,
        CancellationToken cancellationToken)
    {
        if (_snapshot.Mode != SessionMode.Voice || _activeResponseId is null && gain < 1.0)
        {
            return;
        }

        await PublishAsync(
                new SessionOutput(
                    context,
                    _activeResponseId,
                    new PlaybackGainOutput(gain, 20, candidateId)),
                cancellationToken)
            .ConfigureAwait(false);
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
                if (!TryMailbox(
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
