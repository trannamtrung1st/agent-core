using System.Threading.Channels;
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
        {
            if (_inputStreamFailed)
            {
                return false;
            }

            if (_snapshot.Mode != SessionMode.Voice
                || _muted
                || _streamId is null
                || (streamId ?? _streamId) != _streamId)
            {
                return false;
            }

            if (_voice.EffectivePlan.InputTransport == SpeechTransport.ClientTranscript
                || _recognitionSession is null)
            {
                return false;
            }

            try
            {
                PcmCodec.ValidateFrame(frame);
            }
            catch (ArgumentException)
            {
                LatchInputStreamFailure("AudioDiscontinuity", "PCM frame is invalid.");
                return false;
            }

            if (frame.FrameSequence != _expectedFrameSequence
                || !PcmCodec.IsContiguous(_expectedSampleOffset, frame))
            {
                LatchInputStreamFailure("AudioDiscontinuity", "Input audio sequence or sample offset gap.");
                return false;
            }

            if (!_ingress.TryWrite(new IngressAudio(frame)))
            {
                RuntimeTelemetry.RecordDropped("audio");
                LatchInputStreamFailure("AudioDiscontinuity", "Input audio queue exceeded 500 ms.");
                return false;
            }

            _expectedFrameSequence++;
            _expectedSampleOffset = frame.SampleOffset + PcmCodec.SampleCount(frame.Data);
            FlushPendingBoundaries();
            return true;
        }
    }

    public bool TryAdmitBoundary(
        Guid utteranceId,
        SpeechBoundary boundary,
        double? activityScore,
        long sampleOffset = 0,
        double durationMs = 0,
        Guid? streamId = null)
    {
        lock (_audioGate)
        {
            if (_inputStreamFailed)
            {
                return false;
            }

            if (_snapshot.Mode != SessionMode.Voice
                || _recognitionSession is null
                || _muted
                || _streamId is null
                || (streamId ?? _streamId) != _streamId)
            {
                return false;
            }

            if (sampleOffset < 0 || durationMs < 0)
            {
                return false;
            }

            if (sampleOffset > _expectedSampleOffset)
            {
                _pendingBoundaries.Add(new PendingBoundary(utteranceId, boundary, activityScore, sampleOffset, durationMs));
                _pendingBoundaries.Sort((left, right) => left.SampleOffset.CompareTo(right.SampleOffset));
                return true;
            }

            return WriteBoundary(utteranceId, boundary, activityScore);
        }
    }

    private bool WriteBoundary(Guid utteranceId, SpeechBoundary boundary, double? activityScore)
    {
        if (_ingress.TryWrite(new IngressBoundary(utteranceId, boundary, activityScore)))
        {
            return true;
        }

        RuntimeTelemetry.RecordDropped("audio");
        LatchInputStreamFailure("AudioDiscontinuity", "Input audio queue exceeded 500 ms.");
        return false;
    }

    private void FlushPendingBoundaries()
    {
        while (_pendingBoundaries.Count > 0 && _pendingBoundaries[0].SampleOffset <= _expectedSampleOffset)
        {
            var pending = _pendingBoundaries[0];
            if (!WriteBoundary(pending.UtteranceId, pending.Boundary, pending.ActivityScore))
            {
                return;
            }

            _pendingBoundaries.RemoveAt(0);
        }
    }

    private readonly List<PendingBoundary> _pendingBoundaries = [];

    private readonly record struct PendingBoundary(
        Guid UtteranceId,
        SpeechBoundary Boundary,
        double? ActivityScore,
        long SampleOffset,
        double DurationMs);

    private void LatchInputStreamFailure(string code, string message)
    {
        if (_inputStreamFailed)
        {
            return;
        }

        _inputStreamFailed = true;
        EnqueueFault(code, message);
    }

    private void EnqueueFault(string code, string message)
    {
        BeginWork();
        Enqueue(new AudioIngressFaultReceived(NewContext(), code, message), urgent: true);
    }

    private async Task StartRecognitionAsync(CancellationToken cancellationToken, Guid? rotateStreamId = null)
    {
        await StopRecognitionAsync(rotateStreamId, assignStreamId: rotateStreamId.HasValue).ConfigureAwait(false);
        if (_voice.EffectivePlan.InputTransport == SpeechTransport.ClientTranscript)
        {
            lock (_audioGate)
            {
                _expectedFrameSequence = 1;
                _expectedSampleOffset = 0;
                _inputStreamFailed = false;
                _ingress = new AudioIngress();
                if (!rotateStreamId.HasValue)
                {
                    _streamId ??= _ids.NewId();
                }
            }

            _input = InputActivity.Listening;
            return;
        }

        if (_recognizer is null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var session = await _recognizer
            .OpenAsync(new RecognitionOptions(CanonicalAudio.Format, SpeechLocale.Resolve(_snapshot).Effective), cancellationToken)
            .ConfigureAwait(false);
        ChannelReader<IngressMessage> reader;
        int epoch;
        lock (_audioGate)
        {
            _expectedFrameSequence = 1;
            _expectedSampleOffset = 0;
            _inputStreamFailed = false;
            _ingress = new AudioIngress();
            _sttCts = cts;
            _recognitionSession = session;
            reader = _ingress.Reader;
            epoch = _speechEpoch;
        }

        _ = PumpIngressAsync(session, reader, cts.Token, epoch);
        _ = PumpRecognitionAsync(session, cts.Token, epoch);
    }

    private Task StopRecognitionAsync() => StopRecognitionAsync(null, assignStreamId: false);

    private async Task StopRecognitionAsync(Guid? streamId, bool assignStreamId)
    {
        ISpeechRecognitionSession? session;
        CancellationTokenSource? cts;
        lock (_audioGate)
        {
            session = _recognitionSession;
            cts = _sttCts;
            _recognitionSession = null;
            _sttCts = null;
            _pendingBoundaries.Clear();
            _ingress.Complete();
            if (assignStreamId)
            {
                _streamId = streamId;
            }
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

    private async Task PumpIngressAsync(
        ISpeechRecognitionSession session,
        ChannelReader<IngressMessage> reader,
        CancellationToken cancellationToken,
        int epoch)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
                            boundary.ActivityScore,
                            epoch);
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

    private async Task PumpRecognitionAsync(ISpeechRecognitionSession session, CancellationToken cancellationToken, int epoch)
    {
        try
        {
            await foreach (var evidence in session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                EnqueueSpeech(evidence, null, epoch);
            }
        }
        catch (OperationCanceledException)
        {
        }
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
        if (_snapshot.Mode != SessionMode.Voice)
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
        catch (Exception)
        {
            await FailVoiceAsync(input.Context, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailVoiceAsync(EventContext context, CancellationToken cancellationToken)
    {
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
