using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private static readonly TimeSpan EnvironmentTtl = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> OrderStatuses = new(StringComparer.Ordinal)
    {
        "shipped",
        "delayed",
        "delivered"
    };

    private async Task HandleEnvironmentAsync(EnvironmentReceived input, CancellationToken cancellationToken)
    {
        if (_deactivated || _snapshot.Status != SessionStatus.Attached)
        {
            return;
        }

        if (!TryAcceptEnvironment(input.Event, out var kind, out var text, out var topic))
        {
            return;
        }

        if (!_environmentIds.Add(input.Event.EventId))
        {
            return;
        }

        if (kind == TriggerKind.UnfinishedInteraction)
        {
            _snapshot = _snapshot with
            {
                PendingTopic = string.IsNullOrEmpty(topic) ? null : topic,
                UpdatedAt = _time.GetUtcNow()
            };
            RequestPersist(_snapshot);
            if (string.IsNullOrEmpty(topic))
            {
                return;
            }
        }

        if (!HasTrigger(TriggerName(kind)))
        {
            return;
        }

        var queued = new QueuedEnvironment(input.Event, input.Context.Timestamp, kind, text);
        if (IsExpired(queued))
        {
            return;
        }

        if (!IsOutputQuiet())
        {
            _environmentQueue.Enqueue(queued);
            return;
        }

        await LaunchQueuedEnvironmentAsync(input.Context, queued, cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainEnvironmentAsync(EventContext context, CancellationToken cancellationToken)
    {
        while (_environmentQueue.Count > 0 && IsOutputQuiet() && !HasPendingUserBatch())
        {
            var queued = _environmentQueue.Dequeue();
            if (IsExpired(queued) || !HasTrigger(TriggerName(queued.Kind)))
            {
                continue;
            }

            await LaunchQueuedEnvironmentAsync(context, queued, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task LaunchQueuedEnvironmentAsync(
        EventContext context,
        QueuedEnvironment queued,
        CancellationToken cancellationToken)
    {
        if (IsExpired(queued) || !HasTrigger(TriggerName(queued.Kind)) || _activeResponseId is not null || HasPendingUserBatch())
        {
            return;
        }

        _pendingInitiativeExpiresAt = queued.ReceivedAt + EnvironmentTtl;
        var responseId = _ids.NewId();
        var turn = ++_turnGeneration;
        var trigger = new AgentTrigger(context.EventId, queued.Kind, queued.Text, queued.Event.Kind);
        LaunchBrain(context, trigger, responseId, turn);
    }

    private Task HandleRenameAsync(RenameReceived input, CancellationToken cancellationToken)
    {
        if (string.Equals(_snapshot.Title, input.Title, StringComparison.Ordinal))
        {
            input.Persisted.TrySetResult(true);
            return Task.CompletedTask;
        }

        _snapshot = _snapshot with
        {
            Title = input.Title,
            UpdatedAt = _time.GetUtcNow()
        };
        RequestPersist(
            _snapshot,
            then: _ =>
            {
                input.Persisted.TrySetResult(true);
                return Task.CompletedTask;
            },
            ended: input.Persisted);
        return Task.CompletedTask;
    }

    private async Task HandleSpeechLocaleAsync(SpeechLocaleReceived input, CancellationToken cancellationToken)
    {
        if (string.Equals(_snapshot.SpeechLocaleOverride, input.Locale, StringComparison.Ordinal))
        {
            input.Persisted.TrySetResult(true);
            return;
        }

        _snapshot = _snapshot with
        {
            SpeechLocaleOverride = input.Locale,
            UpdatedAt = _time.GetUtcNow()
        };

        var effective = SpeechLocale.Resolve(_snapshot).Effective;
        if (_snapshot.Mode == SessionMode.Voice && !_voice.IsAvailable(_snapshot.Definition, effective))
        {
            _snapshot = _snapshot with
            {
                Mode = SessionMode.Text,
                PendingMode = null,
                UpdatedAt = _time.GetUtcNow()
            };
            await PublishAsync(
                    new SessionOutput(
                        input.Context,
                        null,
                        new ErrorOutput("Session", "VoiceUnavailable", "Voice is not available for this speech locale.", false, null)),
                    cancellationToken)
                .ConfigureAwait(false);
            SpeechTelemetry.RecordError("VoiceUnavailable");
            await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        }

        RequestPersist(
            _snapshot,
            then: async ct =>
            {
                input.Persisted.TrySetResult(true);
                await PublishAsync(new SessionOutput(input.Context, null, new ReadyOutput(BuildReady())), ct)
                    .ConfigureAwait(false);
            },
            ended: input.Persisted);
    }

    private void HandleReopenedSnapshot(ReopenedSnapshotReceived input)
    {
        ResetForExplicitResume(input.Snapshot);
        input.Applied.TrySetResult();
    }

    private void HandleTransportResumedSnapshot(TransportResumedSnapshotReceived input)
    {
        ResetForTransportResume(input.Snapshot);
        input.Applied.TrySetResult();
    }

    private void ResetForTransportResume(SessionSnapshot snapshot)
    {
        CancelBrainEvaluation();
        _proactiveBrainInFlight = false;
        _deactivated = false;
        _durableRevision = snapshot.Revision;
        _durableSnapshot = snapshot;
        _snapshot = snapshot;
        _lastMeaningfulActivityAt = snapshot.LastUserActivityAt ?? _lastMeaningfulActivityAt;
        _input = snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
    }

    private void RecoverProactiveHandleFailure(BrainReturned brain)
    {
        if (brain.Trigger.Kind == TriggerKind.UserTurn || _activeResponseId != brain.ResponseId)
        {
            return;
        }

        var entryId = _activeEntryId;
        _responseCts?.Cancel();
        _responseCts?.Dispose();
        _responseCts = null;
        _activeResponseId = null;
        _activeEntryId = null;
        if (entryId is not null)
        {
            _activeEntryId = entryId;
            UpdateAssistant(EntryStatus.Failed);
            _activeEntryId = null;
        }

        _lastInitiativeAt = null;
        _pendingInitiativeExpiresAt = null;
        _accumulator.Reset();
        _responseTerminal = true;
        _responseLifecycle = ResponseLifecycle.Failed;
        _outputActivity = OutputActivity.Idle;
        if (brain.Trigger.Kind == TriggerKind.LongSilence)
        {
            _helpOfferedDuringSilence = false;
            _consecutiveProactiveSpeaks = Math.Max(0, _consecutiveProactiveSpeaks - 1);
            _proactiveSpeaksThisSilence = Math.Max(0, _proactiveSpeaksThisSilence - 1);
        }

        if (CanEvaluateIdle())
        {
            ScheduleIdleTimer(SilenceThreshold());
        }
    }

    private void ResetForExplicitResume(SessionSnapshot snapshot)
    {
        CancelBrainEvaluation();
        _deactivated = false;
        _activeResponseId = null;
        _outputActivity = OutputActivity.Idle;
        _initiativeHeld = false;
        _proactiveBrainInFlight = false;
        _silentEvaluations = 0;
        _helpOfferedDuringSilence = false;
        _consecutiveProactiveSpeaks = 0;
        _proactiveSpeaksThisSilence = 0;
        _lastInitiativeAt = null;
        _pendingInitiativeExpiresAt = null;
        _environmentQueue.Clear();
        var activityAt = snapshot.LastUserActivityAt ?? _time.GetUtcNow();
        _lastMeaningfulActivityAt = activityAt;
        _durableRevision = snapshot.Revision;
        _durableSnapshot = snapshot;
        _snapshot = snapshot;
        _input = snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        _timerGeneration++;
    }

    private void HandleInitiativeHold(InitiativeHoldReceived input)
    {
        _initiativeHeld = input.Held;
        if (!_initiativeHeld && CanEvaluateIdle())
        {
            ScheduleIdleTimer(SilenceThreshold());
        }
    }

    private async Task ApplyDeactivateAsync(
        EventContext context,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? persisted = null,
        string pauseReason = "manual",
        LifecycleTransitionSource source = LifecycleTransitionSource.System)
    {
        if (_deactivated)
        {
            persisted?.TrySetResult(true);
            return;
        }

        CancelBrainEvaluation();
        CancelCompletionEvaluation();
        _deactivated = true;
        _timerGeneration++;
        _turnGeneration++;
        _epoch = _ids.NewId();
        _environmentQueue.Clear();
        ClearPendingPostResponseIdleDelay();
        _lastArmedIdleDelay = null;
        AbandonLiveSpeech(rotateEpoch: true);
        _input = InputActivity.Idle;
        await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        InvalidateSpeechJobs();
        _ttsCts?.Cancel();
        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(context, live, cancellationToken, "deactivated").ConfigureAwait(false);
        }

        _deadlineTimerGeneration++;
        _outputActivity = OutputActivity.Idle;
        _initiativeHeld = false;
        _snapshot = LifecycleTransition.Apply(
            _snapshot,
            SessionLifecycleStatus.Paused,
            source,
            _time.GetUtcNow(),
            pauseReason);
        SessionPauseTelemetry.Record(pauseReason);
        RequestPersist(
            _snapshot,
            PersistKind.Pause,
            then: async ct =>
            {
                await PublishStateAsync(context, pauseReason, ct).ConfigureAwait(false);
                persisted?.TrySetResult(true);
            },
            ended: persisted);
    }

    private void CancelBrainEvaluation()
    {
        var cts = _brainEvaluationCts;
        if (cts is null)
        {
            return;
        }

        _brainEvaluationCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private void NoteUserActivity()
    {
        CancelBrainEvaluation();
        CancelCompletionEvaluation();
        var now = _time.GetUtcNow();
        _helpOfferedDuringSilence = false;
        _consecutiveProactiveSpeaks = 0;
        _proactiveSpeaksThisSilence = 0;
        _silentEvaluations = 0;
        _lastMeaningfulActivityAt = now;
        ClearPendingPostResponseIdleDelay();
        _snapshot = _snapshot with { LastUserActivityAt = now, UpdatedAt = now };
        _timerGeneration++;
    }

    private bool CanAcceptProactiveSpeak(AgentTrigger trigger)
    {
        if (trigger.Kind != TriggerKind.LongSilence)
        {
            return true;
        }

        var policy = _snapshot.Definition.InitiativePolicy;
        return !_initiativeHeld
            && !_pendingUploadHold
            && !HasPendingUserBatch()
            && policy.ConsecutiveCap > 0
            && _consecutiveProactiveSpeaks < policy.ConsecutiveCap
            && _proactiveSpeaksThisSilence < policy.MaxPerSilencePeriod;
    }

    private bool InactivityExceeded() =>
        (_time.GetUtcNow() - _lastMeaningfulActivityAt).TotalMilliseconds
        > _snapshot.Definition.InitiativePolicy.InactivityLimitMs;

    private bool NeedsTerminalDeactivate() =>
        !_deactivated
        && !HasPendingUserBatch()
        && (_silentEvaluations >= _snapshot.Definition.InitiativePolicy.SilentEvaluationCap
            || InactivityExceeded());

    private void ScheduleIdleTimer(TimeSpan delay)
    {
        if (!CanArmIdleTimer())
        {
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMilliseconds(1);
        }

        _lastArmedIdleDelay = delay;
        var generation = ++_timerGeneration;
        _ = WaitIdleAsync(delay, generation);
    }

    private async Task WaitIdleAsync(TimeSpan delay, int generation)
    {
        try
        {
            await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
            BeginWork();
            if (!TryMailbox(new TimerElapsedReceived(NewContext(), "idle", generation, null)))
            {
                EndWork();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecordInitiativeEvaluation()
    {
        _lastInitiativeAt = _time.GetUtcNow();
        _pendingInitiativeExpiresAt = null;
    }

    private TimeSpan IdleBackoff()
    {
        var factor = Math.Clamp(_silentEvaluations, 1, 4);
        return TimeSpan.FromMilliseconds(Cooldown().TotalMilliseconds * factor);
    }

    private bool CanEvaluateIdle() =>
        CanArmIdleTimer()
        && IsOutputQuiet()
        && !_initiativeHeld
        && !_pendingUploadHold
        && !HasPendingUserBatch()
        && _input is InputActivity.Idle or InputActivity.Listening
        && RemainingCooldown() == TimeSpan.Zero;

    private bool ShouldRetryIdleAfterCooldown() =>
        CanArmIdleTimer()
        && IsOutputQuiet()
        && RemainingCooldown() > TimeSpan.Zero;

    private bool CanArmIdleTimer() =>
        !_deactivated
        && _snapshot.Status == SessionStatus.Attached
        && !SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus)
        && _snapshot.Definition.InitiativePolicy.Enabled
        && HasTrigger("longSilence")
        && !_initiativeHeld
        && !_pendingUploadHold
        && !HasPendingUserBatch()
        && (_silentEvaluations < _snapshot.Definition.InitiativePolicy.SilentEvaluationCap
            || NeedsTerminalDeactivate());

    private bool IsStaleProactiveDecision(AgentTrigger trigger) =>
        trigger.Kind != TriggerKind.UserTurn
        && (_activeResponseId is not null
            || !InitiativeStillEligible(trigger)
            || (trigger.Kind == TriggerKind.LongSilence && !CanAcceptProactiveSpeak(trigger)));

    private bool InitiativeStillEligible(AgentTrigger trigger)
    {
        if (_deactivated || _snapshot.Status != SessionStatus.Attached || _activeResponseId is not null)
        {
            return false;
        }

        if (_input is not (InputActivity.Idle or InputActivity.Listening))
        {
            return false;
        }

        return trigger.Kind switch
        {
            TriggerKind.LongSilence => HasTrigger("longSilence")
                && _snapshot.Definition.InitiativePolicy.Enabled
                && !_initiativeHeld
                && !_pendingUploadHold
                && !HasPendingUserBatch(),
            TriggerKind.EnvironmentUpdate => HasTrigger("environmentUpdate") && !InitiativeExpired(),
            TriggerKind.UnfinishedInteraction => HasTrigger("unfinishedInteraction")
                && !string.IsNullOrEmpty(_snapshot.PendingTopic)
                && !InitiativeExpired(),
            _ => true
        };
    }

    private bool InitiativeExpired() =>
        _pendingInitiativeExpiresAt is { } deadline && _time.GetUtcNow() > deadline;

    private bool IsOutputQuiet() =>
        _activeResponseId is null
        && _outputActivity == OutputActivity.Idle
        && !_proactiveBrainInFlight;

    private bool HasTrigger(string trigger) =>
        _snapshot.Definition.InitiativePolicy.Triggers.Contains(trigger, StringComparer.Ordinal);

    private TimeSpan SilenceThreshold()
    {
        var policy = _snapshot.Definition.InitiativePolicy;
        var ms = policy.SilenceThresholdMs;
        if (_snapshot.Mode == SessionMode.Text)
        {
            ms = Math.Max(ms, 60_000);
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private TimeSpan Cooldown()
    {
        var policy = _snapshot.Definition.InitiativePolicy;
        var ms = policy.CooldownMs;
        if (_snapshot.Mode == SessionMode.Text)
        {
            ms = Math.Max(ms, 30_000);
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private TimeSpan RemainingCooldown()
    {
        if (_lastInitiativeAt is not { } last)
        {
            return TimeSpan.Zero;
        }

        var remaining = Cooldown() - (_time.GetUtcNow() - last);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool TryAcceptEnvironment(
        EnvironmentEvent input,
        out TriggerKind kind,
        out string? text,
        out string? topic)
    {
        kind = TriggerKind.EnvironmentUpdate;
        text = null;
        topic = null;
        if (string.Equals(input.Kind, "order_status_changed", StringComparison.Ordinal))
        {
            if (!input.Data.TryGetValue("orderReference", out var reference)
                || string.IsNullOrWhiteSpace(reference)
                || !input.Data.TryGetValue("status", out var status)
                || !OrderStatuses.Contains(status))
            {
                return false;
            }

            kind = TriggerKind.EnvironmentUpdate;
            text = $"orderReference={reference};status={status}";
            return true;
        }

        if (string.Equals(input.Kind, "unfinished_interaction", StringComparison.Ordinal))
        {
            if (!input.Data.TryGetValue("topic", out topic))
            {
                return false;
            }

            kind = TriggerKind.UnfinishedInteraction;
            text = topic;
            return true;
        }

        return false;
    }

    private static string TriggerName(TriggerKind kind) => kind switch
    {
        TriggerKind.LongSilence => "longSilence",
        TriggerKind.EnvironmentUpdate => "environmentUpdate",
        TriggerKind.UnfinishedInteraction => "unfinishedInteraction",
        _ => "userTurn"
    };

    private bool IsExpired(QueuedEnvironment queued) =>
        _time.GetUtcNow() > queued.ReceivedAt + EnvironmentTtl;

    private sealed record QueuedEnvironment(
        EnvironmentEvent Event,
        DateTimeOffset ReceivedAt,
        TriggerKind Kind,
        string? Text);
}
