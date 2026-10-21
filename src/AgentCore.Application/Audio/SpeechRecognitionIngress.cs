using AgentCore.Application.Ports;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Audio;

// A provider-stream resource, not a conversation controller or execution authority.
// Only normalized evidence/faults leave it. The owner still authorizes each ingress call.
public sealed class SpeechRecognitionIngress : IAsyncDisposable
{
    public const int MaxPendingBoundaries = 32;
    private readonly object _gate = new();
    private readonly ISpeechRecognitionSession _session;
    private readonly Guid _stream;
    private readonly Action<SpeechRecognitionEvent, double?> _evidence;
    private readonly Action<string, string> _fault;
    private readonly CancellationTokenSource _lifetime;
    private readonly AudioIngress _queue = new();
    private readonly List<PendingBoundary> _pending = [];
    private long _nextSequence = 1, _nextOffset;
    private bool _started, _failed, _stopped;
    private TaskCompletionSource? _stop;
    private sealed record PendingBoundary(Guid Utterance, SpeechBoundary Boundary, double? Score, long Offset);

    public SpeechRecognitionIngress(ISpeechRecognitionSession session, Guid stream,
        Action<SpeechRecognitionEvent, double?> evidence, Action<string, string> fault, CancellationToken lifetime = default)
    {
        if (stream == Guid.Empty) throw new ArgumentException("Use a fresh input stream identity.", nameof(stream));
        _session = session; _stream = stream; _evidence = evidence; _fault = fault;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    public void Start()
    {
        lock (_gate) { if (_started || _stopped) return; _started = true; }
        // Both supervised workers are observed here; exceptions never escape as abandoned tasks.
        _ = PumpInputAsync(); _ = PumpEvidenceAsync();
    }
    public bool TryAdmitAudio(AudioFrame frame, Guid stream)
    {
        lock (_gate)
        {
            if (_stopped || _failed || !_started || _lifetime.IsCancellationRequested || stream != _stream) return false;
            try { PcmCodec.ValidateFrame(frame); }
            catch (ArgumentException) { return Fail("AudioDiscontinuity", "PCM frame is invalid."); }
            var samples = PcmCodec.SampleCount(frame.Data);
            if (frame.FrameSequence != _nextSequence || frame.SampleOffset != _nextOffset
                || frame.FrameSequence == long.MaxValue || frame.SampleOffset > long.MaxValue - samples)
                return Fail("AudioDiscontinuity", "Input audio sequence or sample offset gap.");
            // The transport owns these bytes after admission, even if the caller reuses its buffer.
            if (!_queue.TryWrite(new IngressAudio(frame with { Data = frame.Data.ToArray() })))
                return Saturated("Input audio queue exceeded 500 ms.");
            _nextSequence++; _nextOffset += samples;
            while (_pending.Count > 0 && _pending[0].Offset <= _nextOffset)
            {
                var pending = _pending[0];
                if (!WriteBoundary(pending.Utterance, pending.Boundary, pending.Score)) return false;
                _pending.RemoveAt(0);
            }
            return true;
        }
    }
    public bool TryAdmitBoundary(Guid utterance, SpeechBoundary boundary, double? score, long offset, double durationMs, Guid stream)
    {
        lock (_gate)
        {
            if (_stopped || _failed || !_started || _lifetime.IsCancellationRequested || stream != _stream) return false;
            if (utterance == Guid.Empty || !Enum.IsDefined(boundary) || offset < 0 || !double.IsFinite(durationMs) || durationMs < 0
                || score is { } activity && (!double.IsFinite(activity) || activity is < 0 or > 1)) return false;
            if (offset > _nextOffset)
            {
                if (offset - _nextOffset > AudioIngress.MaxQueuedSamples || _pending.Count >= MaxPendingBoundaries)
                    return Fail("AudioDiscontinuity", "Input boundary exceeded the bounded audio window.");
                // Equal-offset Started/Ended markers retain arrival order.
                var index = _pending.FindIndex(p => p.Offset > offset);
                _pending.Insert(index < 0 ? _pending.Count : index, new(utterance, boundary, score, offset)); return true;
            }
            return WriteBoundary(utterance, boundary, score);
        }
    }
    private bool WriteBoundary(Guid utterance, SpeechBoundary boundary, double? score) =>
        _queue.TryWrite(new IngressBoundary(utterance, boundary, score)) || Saturated("Input audio queue exceeded its control capacity.");
    private bool Saturated(string message)
    {
        RuntimeTelemetry.RecordDropped("audio"); return Fail("AudioDiscontinuity", message);
    }
    private bool Fail(string code, string message)
    {
        if (!_failed && !_stopped)
        {
            _failed = true; _queue.Complete(); _pending.Clear();
            try { _fault(code, message); } catch (Exception) { }
        }
        return false;
    }
    private void Emit(SpeechRecognitionEvent evidence, double? score)
    {
        lock (_gate) { if (!_stopped && !_failed && !_lifetime.IsCancellationRequested) _evidence(evidence, score); }
    }
    private async Task PumpInputAsync()
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                lock (_gate) { if (_stopped || _failed) return; }
                if (message is IngressAudio audio) await _session.PushAudioAsync(audio.Frame, _lifetime.Token).ConfigureAwait(false);
                else if (message is IngressBoundary boundary)
                {
                    Emit(boundary.Boundary == SpeechBoundary.Started ? new SpeechStarted(boundary.UtteranceId) : new SpeechEnded(boundary.UtteranceId), boundary.ActivityScore);
                    await _session.ObserveBoundaryAsync(boundary.UtteranceId, boundary.Boundary, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { lock (_gate) Fail("SpeechRecognitionFailed", "Speech input became unavailable. Start a fresh stream."); }
    }
    private async Task PumpEvidenceAsync()
    {
        try { await foreach (var evidence in _session.ReadEventsAsync(_lifetime.Token).ConfigureAwait(false)) Emit(evidence, null); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { lock (_gate) Fail("SpeechRecognitionFailed", "Speech input became unavailable. Start a fresh stream."); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource stop;
        lock (_gate)
        {
            if (_stop is not null) return new(_stop.Task);
            _stopped = true; _pending.Clear(); _queue.Complete();
            _stop = stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = StopAsync(stop); return new(stop.Task);
    }
    private async Task StopAsync(TaskCompletionSource stopped)
    {
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); } catch (Exception) { }
            try { await _session.CompleteInputAsync().ConfigureAwait(false); } catch (Exception) { }
            try { await _session.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
        finally { _lifetime.Dispose(); stopped.TrySetResult(); }
    }
}
