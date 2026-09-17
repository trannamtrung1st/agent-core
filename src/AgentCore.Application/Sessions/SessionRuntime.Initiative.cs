using AgentCore.Application.Events;
using AgentCore.Application.Ports;
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
        while (_environmentQueue.Count > 0 && IsOutputQuiet())
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
        if (IsExpired(queued) || !HasTrigger(TriggerName(queued.Kind)) || _activeResponseId is not null)
        {
            return;
        }

        _pendingInitiativeExpiresAt = queued.ReceivedAt + EnvironmentTtl;
        _outputActivity = OutputActivity.WaitingForAgent;
        var responseId = _ids.NewId();
        var turn = ++_turnGeneration;
        var trigger = new AgentTrigger(context.EventId, queued.Kind, queued.Text, queued.Event.Kind);
        LaunchBrain(context, trigger, responseId, turn);
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDeactivateAsync(DeactivateReceived input, CancellationToken cancellationToken)
    {
        await ApplyDeactivateAsync(input.Context, cancellationToken, input.Persisted).ConfigureAwait(false);
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
        TaskCompletionSource<bool>? persisted = null)
    {
        if (_deactivated)
        {
            persisted?.TrySetResult(true);
            return;
        }

        _deactivated = true;
        _timerGeneration++;
        _turnGeneration++;
        _epoch = _ids.NewId();
        _environmentQueue.Clear();
        AbandonLiveSpeech(rotateEpoch: true);
        _input = InputActivity.Idle;
        await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        InvalidateSpeechJobs();
        _ttsCts?.Cancel();
        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(context, live, cancellationToken, "deactivated").ConfigureAwait(false);
        }

        _outputActivity = OutputActivity.Idle;
        _initiativeHeld = false;
        _snapshot = _snapshot with
        {
            Status = SessionStatus.Paused,
            PendingMode = null,
            RuntimeEpoch = _snapshot.RuntimeEpoch + 1
        };
        RequestPersist(
            _snapshot,
            PersistKind.Pause,
            then: async ct =>
            {
                await PublishStateAsync(context, ct).ConfigureAwait(false);
                persisted?.TrySetResult(true);
            },
            ended: persisted);
    }

    private void NoteUserActivity()
    {
        _helpOfferedDuringSilence = false;
        _consecutiveProactiveSpeaks = 0;
        _proactiveSpeaksThisSilence = 0;
        _silentEvaluations = 0;
        _lastMeaningfulActivityAt = _time.GetUtcNow();
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
            && policy.ConsecutiveCap > 0
            && _consecutiveProactiveSpeaks < policy.ConsecutiveCap
            && _proactiveSpeaksThisSilence < policy.MaxPerSilencePeriod;
    }

    private bool InactivityExceeded() =>
        (_time.GetUtcNow() - _lastMeaningfulActivityAt).TotalMilliseconds
        > _snapshot.Definition.InitiativePolicy.InactivityLimitMs;

    private bool NeedsTerminalDeactivate() =>
        !_deactivated
        && (_consecutiveProactiveSpeaks >= _snapshot.Definition.InitiativePolicy.ConsecutiveCap
            && _snapshot.Definition.InitiativePolicy.ConsecutiveCap > 0
            || _silentEvaluations >= _snapshot.Definition.InitiativePolicy.SilentEvaluationCap
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
        && _input is InputActivity.Idle or InputActivity.Listening
        && RemainingCooldown() == TimeSpan.Zero;

    private bool ShouldRetryIdleAfterCooldown() =>
        CanArmIdleTimer()
        && IsOutputQuiet()
        && RemainingCooldown() > TimeSpan.Zero;

    private bool CanArmIdleTimer() =>
        !_deactivated
        && _snapshot.Status == SessionStatus.Attached
        && _snapshot.Definition.InitiativePolicy.Enabled
        && HasTrigger("longSilence")
        && !_initiativeHeld
        && !_pendingUploadHold
        && (_silentEvaluations < _snapshot.Definition.InitiativePolicy.SilentEvaluationCap
            || NeedsTerminalDeactivate());

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
                && !_pendingUploadHold,
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
        _activeResponseId is null && _outputActivity == OutputActivity.Idle;

    private bool HasTrigger(string trigger) =>
        _snapshot.Definition.InitiativePolicy.Triggers.Contains(trigger, StringComparer.Ordinal);

    private TimeSpan SilenceThreshold() =>
        TimeSpan.FromMilliseconds(_snapshot.Definition.InitiativePolicy.SilenceThresholdMs);

    private TimeSpan Cooldown() =>
        TimeSpan.FromMilliseconds(_snapshot.Definition.InitiativePolicy.CooldownMs);

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
