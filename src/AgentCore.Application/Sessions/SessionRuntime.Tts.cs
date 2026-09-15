using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private const long MaxUnackedSamples = CanonicalAudio.SampleRateHz * 2;

    private SpeechSegmenter? _segmenter;
    private readonly Queue<SpeechSegment> _pendingSegments = new();
    private SpeechSegment? _currentSegment;
    private AudioFrameOutput? _openAudio;
    private SynthesisResultReceived? _heldSynthesis;
    private TaskCompletionSource? _modelBackpressure;
    private bool _ttsBusy;
    private bool _modelDone;
    private bool _audioFinalSent;
    private bool _playbackDone;
    private bool _segmentTimerArmed;
    private long _outputFrameSequence = 1;
    private long _outputSampleOffset;
    private long _sentSamples;
    private long _ackedSamples;
    private long _segmentSampleOrigin;
    private int _segmentTimerGeneration;
    private int _ttsJobsStarted;
    private CancellationTokenSource? _ttsCts;
    private readonly SpokenUntilAccumulator _spokenUntil = new();

    public int TtsJobsStarted => _ttsJobsStarted;

    public long OutputSampleOffset => _outputSampleOffset;

    public long SentSamples => _sentSamples;

    private bool UsesVoicePlayback =>
        _snapshot.Mode == SessionMode.Voice && _synthesizer is not null && _activeResponseId is not null;

    private void ResetSpeechOutput()
    {
        InvalidateSpeechJobs();
        _pendingSegments.Clear();
        _ttsBusy = false;
        _modelDone = false;
        _audioFinalSent = false;
        _playbackDone = false;
        _openAudio = null;
        _heldSynthesis = null;
        _outputFrameSequence = 1;
        _outputSampleOffset = 0;
        _sentSamples = 0;
        _ackedSamples = 0;
        _ttsJobsStarted = 0;
        _currentSegment = null;
        _spokenUntil.Reset();
        _segmenter = _activeResponseId is { } id
            && _snapshot.Mode == SessionMode.Voice
            && _synthesizer is not null
                ? new SpeechSegmenter(id)
                : null;
    }

    private void InvalidateSpeechJobs()
    {
        _segmentTimerGeneration++;
        _segmentTimerArmed = false;
        _segmenter?.Invalidate();
        _pendingSegments.Clear();
        _currentSegment = null;
        _openAudio = null;
        _modelBackpressure?.TrySetResult();
        _modelBackpressure = null;
        _heldSynthesis?.Processed.TrySetResult();
        _heldSynthesis = null;
    }

    private void EnqueueSegments(IReadOnlyList<SpeechSegment> segments)
    {
        if (_responseLifecycle != ResponseLifecycle.Live)
        {
            return;
        }

        foreach (var segment in segments)
        {
            _pendingSegments.Enqueue(segment);
        }
    }

    private void ScheduleSegmentTimer()
    {
        if (_segmentTimerArmed || _segmenter is not { HasBuffered: true })
        {
            return;
        }

        _segmentTimerArmed = true;
        var generation = _segmentTimerGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SpeechSegmenter.Latency, _time, _lifetime.Token).ConfigureAwait(false);
                BeginWork();
                if (!_mailbox.Writer.TryWrite(new TimerElapsedReceived(NewContext(), "segment", generation, null)))
                {
                    EndWork();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void KickTts(EventContext cause)
    {
        if (_ttsBusy
            || _pendingSegments.Count == 0
            || _synthesizer is null
            || _responseLifecycle != ResponseLifecycle.Live
            || _activeResponseId is not { } responseId)
        {
            return;
        }

        var segment = _pendingSegments.Dequeue();
        if (_pendingSegments.Count < 4)
        {
            _modelBackpressure?.TrySetResult();
            _modelBackpressure = null;
        }

        _ttsBusy = true;
        _ttsJobsStarted++;
        _currentSegment = segment;
        _segmentSampleOrigin = _outputSampleOffset;
        _spokenUntil.TrackSegment(segment, _segmentSampleOrigin);
        _ttsCts?.Cancel();
        _ttsCts?.Dispose();
        _ttsCts = CancellationTokenSource.CreateLinkedTokenSource(_responseCts?.Token ?? _lifetime.Token, _lifetime.Token);
        var token = _ttsCts.Token;
        var synthesizer = _synthesizer;
        var request = new SpeechRequest(
            responseId,
            segment.SegmentIndex,
            segment.TextStart,
            segment.Text,
            _snapshot.Definition.Voice.VoiceId,
            _snapshot.Definition.Voice.SpeakingRate,
            CanonicalAudio.Format);
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in synthesizer.SynthesizeAsync(request, token).ConfigureAwait(false))
                {
                    var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    BeginWork();
                    if (!_mailbox.Writer.TryWrite(
                            new SynthesisResultReceived(NewContext(cause.EventId), responseId, segment.SegmentIndex, evt, processed)))
                    {
                        EndWork();
                        processed.TrySetResult();
                        break;
                    }

                    await processed.Task.WaitAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TTS pump failed for {ResponseId} segment {Segment}", responseId, segment.SegmentIndex);
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginWork();
                if (!_mailbox.Writer.TryWrite(
                        new SynthesisResultReceived(
                            NewContext(cause.EventId),
                            responseId,
                            segment.SegmentIndex,
                            new SpeechSynthesisFailed(new ProviderFailure(ProviderErrorCode.Unknown, "Synthesis failed.")),
                            processed)))
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

    private async Task HandleSynthesisAsync(SynthesisResultReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId || _responseLifecycle != ResponseLifecycle.Live)
        {
            input.Processed.TrySetResult();
            return;
        }

        switch (input.Event)
        {
            case SpeechAudio audio:
                if (_sentSamples - _ackedSamples + PcmCodec.SampleCount(audio.Frame.Data) > MaxUnackedSamples)
                {
                    _heldSynthesis = input;
                    return;
                }

                await FlushOpenAudioAsync(input.Context, input.ResponseId, isFinal: false, cancellationToken)
                    .ConfigureAwait(false);
                _openAudio = Rebase(audio.Frame, isFinal: false);
                break;
            case SpeechTimingMark mark:
                if (_currentSegment is { } timed)
                {
                    _spokenUntil.AddTimingMark(
                        timed.TextStart + mark.TextEndExclusive,
                        _segmentSampleOrigin + mark.SampleOffset);
                }

                break;
            case SpeechSynthesisCompleted completed:
                _ttsBusy = false;
                if (_currentSegment is { } done)
                {
                    _spokenUntil.CompleteSegment(done.SegmentIndex, completed.TotalSamples);
                }

                _currentSegment = null;
                KickTts(input.Context);
                await FinishAudioIfReadyAsync(input.Context, input.ResponseId, cancellationToken).ConfigureAwait(false);
                await TryCompleteVoiceAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
                break;
            case SpeechSynthesisFailed failed:
                _ttsBusy = false;
                await CompleteAsync(input.Context, input.ResponseId, failed: true, cancellationToken).ConfigureAwait(false);
                _ = failed;
                break;
        }

        input.Processed.TrySetResult();
    }

    private async Task HandlePlaybackAsync(PlaybackReportReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId || _responseLifecycle != ResponseLifecycle.Live)
        {
            return;
        }

        if (input.ConsumedSamples < _ackedSamples || input.ConsumedSamples > _sentSamples)
        {
            return;
        }

        _ackedSamples = input.ConsumedSamples;
        ApplyHeard(_spokenUntil.Credit(_ackedSamples));
        if (string.Equals(input.Kind, "started", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input.Kind, "progress", StringComparison.OrdinalIgnoreCase))
        {
            if (_outputActivity == OutputActivity.AgentGenerating)
            {
                _outputActivity = OutputActivity.AgentSpeaking;
                await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_heldSynthesis is { } held)
        {
            _heldSynthesis = null;
            await HandleSynthesisAsync(held, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(input.Kind, "completed", StringComparison.OrdinalIgnoreCase))
        {
            _playbackDone = true;
            await TryCompleteVoiceAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryCompleteVoiceAsync(EventContext context, bool failed, CancellationToken cancellationToken)
    {
        if (!UsesVoicePlayback
            || _activeResponseId is not { } responseId
            || _responseTerminal
            || !_modelDone
            || _ttsBusy
            || _pendingSegments.Count > 0)
        {
            return;
        }

        await FinishAudioIfReadyAsync(context, responseId, cancellationToken).ConfigureAwait(false);
        if (!_audioFinalSent || !_playbackDone)
        {
            return;
        }

        _responseTerminal = true;
        _responseLifecycle = failed ? ResponseLifecycle.Failed : ResponseLifecycle.Completed;
        _outputActivity = OutputActivity.Idle;
        UpdateAssistant(failed ? EntryStatus.Failed : EntryStatus.Completed, _accumulator.Length);
        var heard = failed ? 0 : _accumulator.Length;
        ApplyHeard(heard);
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        await PublishAsync(
                new SessionOutput(
                    context,
                    responseId,
                    new ResponseCompletedOutput(failed, HeardTextEndExclusive: heard)),
                cancellationToken)
            .ConfigureAwait(false);
        ClearActive();
        if (_snapshot.PendingMode == SessionMode.Voice)
        {
            await ApplyModeAsync(SessionMode.Voice, cancellationToken).ConfigureAwait(false);
            await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAudioIfReadyAsync(EventContext context, Guid responseId, CancellationToken cancellationToken)
    {
        if (_audioFinalSent || _ttsBusy || _pendingSegments.Count > 0 || !_modelDone)
        {
            return;
        }

        if (_openAudio is { } open)
        {
            await PublishAsync(
                    new SessionOutput(
                        context,
                        responseId,
                        open with { IsFinal = true }),
                    cancellationToken)
                .ConfigureAwait(false);
            _sentSamples = Math.Max(_sentSamples, open.SampleOffset + (open.Data.Length / 2));
            _openAudio = null;
        }
        else
        {
            await PublishAsync(
                    new SessionOutput(
                        context,
                        responseId,
                        new AudioFrameOutput(_outputFrameSequence, _outputSampleOffset, true, [])),
                    cancellationToken)
                .ConfigureAwait(false);
            _outputFrameSequence++;
        }

        _audioFinalSent = true;
    }

    private async Task FlushOpenAudioAsync(
        EventContext context,
        Guid responseId,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        if (_openAudio is not { } open)
        {
            return;
        }

        await PublishAsync(
                new SessionOutput(context, responseId, isFinal ? open with { IsFinal = true } : open),
                cancellationToken)
            .ConfigureAwait(false);
        _openAudio = null;
    }

    private AudioFrameOutput Rebase(AudioFrame frame, bool isFinal)
    {
        var data = frame.Data.ToArray();
        var output = new AudioFrameOutput(_outputFrameSequence, _outputSampleOffset, isFinal, data);
        _outputFrameSequence++;
        _outputSampleOffset += PcmCodec.SampleCount(data);
        _sentSamples = _outputSampleOffset;
        return output;
    }

    private void ApplyHeard(int heard)
    {
        if (_activeEntryId is not { } entryId)
        {
            return;
        }

        heard = Math.Clamp(heard, 0, _accumulator.Length);
        var entries = _snapshot.Entries.Select(entry =>
                entry.EntryId == entryId && heard >= entry.HeardTextEndExclusive
                    ? entry with { HeardTextEndExclusive = heard }
                    : entry)
            .ToArray();
        _snapshot = _snapshot with { Entries = entries };
    }
}
