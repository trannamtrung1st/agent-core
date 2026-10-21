using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _inputStreamFailed;

    public bool TryAdmitAudio(AudioFrame frame, Guid? streamId = null)
    {
        lock (_audioGate)
            return !_inputStreamFailed && _snapshot.Mode == SessionMode.Voice && !_muted && _streamId is { } stream
                && _voice.EffectivePlan.InputTransport != SpeechTransport.ClientTranscript
                && _speechIngress?.TryAdmitAudio(frame, streamId ?? stream) == true;
    }

    public bool TryAdmitBoundary(Guid utteranceId, SpeechBoundary boundary, double? activityScore,
        long sampleOffset = 0, double durationMs = 0, Guid? streamId = null)
    {
        lock (_audioGate)
            return !_inputStreamFailed && _snapshot.Mode == SessionMode.Voice && !_muted && _streamId is { } stream
                && _speechIngress?.TryAdmitBoundary(utteranceId, boundary, activityScore, sampleOffset, durationMs, streamId ?? stream) == true;
    }

    private void LatchInputStreamFailure(string code, string message, int epoch, Guid stream)
    {
        if (_inputStreamFailed || epoch != _speechEpoch || stream != _streamId)
        {
            return;
        }

        _inputStreamFailed = true;
        EnqueueFault(code, message, epoch, stream);
    }

    private void EnqueueFault(string code, string message, int epoch, Guid stream)
    {
        BeginWork();
        Enqueue(new AudioIngressFaultReceived(NewContext(), code, message, epoch, stream), urgent: true);
    }

    private async Task StartRecognitionAsync(CancellationToken cancellationToken, Guid? rotateStreamId = null)
    {
        await StopRecognitionAsync(rotateStreamId, assignStreamId: rotateStreamId.HasValue).ConfigureAwait(false);
        lock (_audioGate)
        {
            _inputStreamFailed = false;
            _streamId ??= _ids.NewId();
        }
        if (_voice.EffectivePlan.InputTransport == SpeechTransport.ClientTranscript)
        {
            _input = InputActivity.Listening;
            return;
        }
        if (_recognizer is null) return;
        var session = await _recognizer.OpenAsync(new RecognitionOptions(CanonicalAudio.Format, SpeechLocale.Resolve(_snapshot).Effective), cancellationToken).ConfigureAwait(false);
        var epoch = _speechEpoch;
        var stream = _streamId!.Value;
        var ingress = new SpeechRecognitionIngress(session, stream,
            (evidence, score) => EnqueueSpeech(evidence, score, epoch),
            (code, message) => LatchInputStreamFailure(code, message, epoch, stream), _lifetime.Token);
        lock (_audioGate) _speechIngress = ingress;
        ingress.Start();
    }

    private Task StopRecognitionAsync() => StopRecognitionAsync(null, assignStreamId: false);

    private async Task StopRecognitionAsync(Guid? streamId, bool assignStreamId)
    {
        SpeechRecognitionIngress? ingress;
        lock (_audioGate)
        {
            ingress = _speechIngress; _speechIngress = null;
            if (assignStreamId) _streamId = streamId;
        }
        if (ingress is not null) await ingress.DisposeAsync().ConfigureAwait(false);
    }

    private void EnqueueSpeech(SpeechRecognitionEvent evidence, double? activityScore, int epoch)
    {
        BeginWork();
        Enqueue(new SpeechEvidenceReceived(NewContext(), evidence, activityScore, epoch), urgent: false);
    }

    private void AbandonLiveSpeech(bool rotateEpoch)
    {
        _timerGeneration++;
        if (rotateEpoch)
        {
            _speechEpoch++;
        }

        _maxUtteranceGeneration++;
        _candidate = null;
        _activeUtteranceId = null;
        _speechPartialRevision = -1;
        _utteranceStarted = null;
        _input = _snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
    }

    private async Task HandleAudioFaultAsync(AudioIngressFaultReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Mode != SessionMode.Voice || _muted || input.SpeechEpoch != _speechEpoch || input.StreamId != _streamId)
        {
            return;
        }

        lock (_audioGate)
        {
            if (!_inputStreamFailed)
            {
                return;
            }
        }

        AbandonLiveSpeech(rotateEpoch: true);
        await StopRecognitionAsync(_ids.NewId(), assignStreamId: true).ConfigureAwait(false);

        if (input.Code == "SpeechRecognitionFailed")
        {
            // A broken provider must not create an unbounded open/fail/reopen loop.
            await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    null,
                    new ErrorOutput("Transport", input.Code, input.Message, false, null)),
                cancellationToken)
            .ConfigureAwait(false);
        SpeechTelemetry.RecordError(input.Code);
        try
        {
            await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandledRuntimeFailure(ex, "speech.start", "recognition_start_failed");
            await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailVoiceAsync(EventContext context, CancellationToken cancellationToken)
    {
        if (_tenantVoiceBinding is { } binding) { SignalTenantVoice(new TenantVoiceUnavailable(binding.Lease)); return; }
        if (_snapshot.Mode == SessionMode.Voice && _activeResponseId is { } response)
            await SupersedeAsync(context, response, cancellationToken, "modeChange").ConfigureAwait(false);
        AbandonLiveSpeech(rotateEpoch: true);
        await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        _snapshot = _snapshot with
        {
            Mode = SessionMode.Text,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        _input = InputActivity.Idle;
        _muted = false;
        RequestPersist(
            _snapshot,
            then: async ct =>
            {
                await PublishAsync(
                        new SessionOutput(
                            context,
                            null,
                            new ErrorOutput("Session", "VoiceUnavailable", "Voice capture could not start.", false, null)),
                        ct)
                    .ConfigureAwait(false);
                SpeechTelemetry.RecordError("VoiceUnavailable");
                await PublishStateAsync(context, ct).ConfigureAwait(false);
            });
    }

    private void SetStreamId(Guid? streamId)
    {
        lock (_audioGate)
        {
            _streamId = streamId;
        }
    }

    private async Task ApplyPendingVoiceIfIdleAsync(EventContext context, CancellationToken cancellationToken)
    {
        if (_snapshot.PendingMode != SessionMode.Voice
            || _snapshot.Status is not SessionStatus.Attached
            || HasLiveAssistantOutput)
        {
            return;
        }

        _timerGeneration++;
        _pendingVoiceGeneration++;
        await ApplyModeAsync(SessionMode.Voice, cancellationToken).ConfigureAwait(false);
        RequestPersist(_snapshot, then: ct => PublishStateAsync(context, ct));
    }
}
