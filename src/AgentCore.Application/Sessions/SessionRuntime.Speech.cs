using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    public bool TryAdmitAudio(AudioFrame frame)
    {
        lock (_audioGate)
        {
            if (_snapshot.Mode != SessionMode.Voice || _recognitionSession is null || _muted)
            {
                return false;
            }

            try
            {
                PcmCodec.ValidateFrame(frame);
            }
            catch (ArgumentException)
            {
                EnqueueFault("AudioDiscontinuity", "PCM frame is invalid.");
                return false;
            }

            if (frame.FrameSequence != _expectedFrameSequence
                || !PcmCodec.IsContiguous(_expectedSampleOffset, frame))
            {
                EnqueueFault("AudioDiscontinuity", "Input audio sequence or sample offset gap.");
                return false;
            }

            if (!_ingress.TryWrite(new IngressAudio(frame)))
            {
                RuntimeTelemetry.RecordDropped("audio");
                EnqueueFault("AudioDiscontinuity", "Input audio queue exceeded 500 ms.");
                return false;
            }

            _expectedFrameSequence++;
            _expectedSampleOffset = frame.SampleOffset + PcmCodec.SampleCount(frame.Data);
            return true;
        }
    }

    public bool TryAdmitBoundary(Guid utteranceId, SpeechBoundary boundary, double? activityScore)
    {
        lock (_audioGate)
        {
            if (_snapshot.Mode != SessionMode.Voice || _recognitionSession is null || _muted)
            {
                return false;
            }

            return _ingress.TryWrite(new IngressBoundary(utteranceId, boundary, activityScore));
        }
    }

    private void EnqueueFault(string code, string message)
    {
        BeginWork();
        Enqueue(new AudioIngressFaultReceived(NewContext(), code, message), urgent: true);
    }

    private async Task StartRecognitionAsync(CancellationToken cancellationToken)
    {
        await StopRecognitionAsync().ConfigureAwait(false);
        if (_recognizer is null)
        {
            return;
        }

        _expectedFrameSequence = 1;
        _expectedSampleOffset = 0;
        _ingress = new AudioIngress();
        _sttCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _recognitionSession = await _recognizer
            .OpenAsync(new RecognitionOptions(CanonicalAudio.Format, _snapshot.Definition.ConversationPolicy.Language), cancellationToken)
            .ConfigureAwait(false);
        var session = _recognitionSession;
        var token = _sttCts.Token;
        _ = PumpIngressAsync(session, token);
        _ = PumpRecognitionAsync(session, token);
    }

    private async Task StopRecognitionAsync()
    {
        var session = _recognitionSession;
        var cts = _sttCts;
        _recognitionSession = null;
        _sttCts = null;
        lock (_audioGate)
        {
            _ingress.Complete();
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (session is not null)
        {
            try
            {
                await session.CompleteInputAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    private async Task PumpIngressAsync(ISpeechRecognitionSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _ingress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message)
                {
                    case IngressAudio audio:
                        await session.PushAudioAsync(audio.Frame, cancellationToken).ConfigureAwait(false);
                        break;
                    case IngressBoundary boundary:
                        EnqueueSpeech(
                            boundary.Boundary == SpeechBoundary.Started
                                ? new SpeechStarted(boundary.UtteranceId)
                                : new SpeechEnded(boundary.UtteranceId),
                            boundary.ActivityScore);
                        await session.ObserveBoundaryAsync(boundary.UtteranceId, boundary.Boundary, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpRecognitionAsync(ISpeechRecognitionSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evidence in session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                EnqueueSpeech(evidence, null);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnqueueSpeech(SpeechRecognitionEvent evidence, double? activityScore)
    {
        BeginWork();
        Enqueue(new SpeechEvidenceReceived(NewContext(), evidence, activityScore), urgent: false);
    }

    private async Task HandleAudioFaultAsync(AudioIngressFaultReceived input, CancellationToken cancellationToken)
    {
        if (_snapshot.Mode != SessionMode.Voice)
        {
            return;
        }

        await StopRecognitionAsync().ConfigureAwait(false);
        _streamId = _ids.NewId();
        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    null,
                    new ErrorOutput("Transport", input.Code, input.Message, false, null)),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailVoiceAsync(EventContext context, CancellationToken cancellationToken)
    {
        await StopRecognitionAsync().ConfigureAwait(false);
        _snapshot = _snapshot with
        {
            Mode = SessionMode.Text,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        _input = InputActivity.Idle;
        _streamId = null;
        _muted = false;
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await PublishAsync(
                new SessionOutput(
                    context,
                    null,
                    new ErrorOutput("Session", "VoiceUnavailable", "Voice capture could not start.", false, null)),
                cancellationToken)
            .ConfigureAwait(false);
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
