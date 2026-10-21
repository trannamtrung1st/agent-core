using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentCore.Application.Audio;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tests;

public sealed class SpeechRecognitionIngressTests
{
    private static AudioFrame Frame(long sequence, long offset, int samples = 480) => new(sequence,offset,new byte[samples*2]);
    [Fact]
    public void Mixed_frame_sizes_obey_500ms_sample_budget_and_dequeue_releases_only_its_audio()
    {
        var queue = new AudioIngress();
        for (var i=0;i<12;i++) Assert.True(queue.TryWrite(new IngressAudio(Frame(i+1,i*960,960))));
        Assert.True(queue.TryWrite(new IngressBoundary(Guid.NewGuid(),SpeechBoundary.Started,0.9)));
        Assert.True(queue.TryWrite(new IngressAudio(Frame(13,11520))));
        Assert.False(queue.TryWrite(new IngressAudio(Frame(14,12000,1))));
        Assert.True(queue.Reader.TryRead(out var first));Assert.IsType<IngressAudio>(first);
        Assert.True(queue.TryWrite(new IngressAudio(Frame(14,12000,960))));
        Assert.False(queue.TryWrite(new IngressAudio(Frame(15,12960,1))));
        for(var i=0;i<11;i++)Assert.True(queue.Reader.TryRead(out _));
        Assert.True(queue.Reader.TryRead(out var boundary));Assert.IsType<IngressBoundary>(boundary);
        Assert.True(queue.TryWrite(new IngressAudio(Frame(15,12960,960))));
    }
    [Fact]
    public async Task Admitted_PCM_is_owned_and_boundaries_at_equal_future_offsets_remain_ordered()
    {
        var session = new ControlledSession(); var stream=Guid.NewGuid(); var evidence=new ConcurrentQueue<SpeechRecognitionEvent>();
        await using var ingress=new SpeechRecognitionIngress(session,stream,(e,_)=>evidence.Enqueue(e),(_,_)=>Assert.Fail("Unexpected stream fault"));ingress.Start();
        var utterance=Guid.NewGuid(); Assert.True(ingress.TryAdmitBoundary(utterance,SpeechBoundary.Started,0.9,480,0,stream));Assert.True(ingress.TryAdmitBoundary(utterance,SpeechBoundary.Ended,0.2,480,20,stream));
        var bytes=new byte[960];bytes[0]=42;Assert.True(ingress.TryAdmitAudio(new(1,0,bytes),stream));await session.PushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));bytes[0]=99;session.Release.TrySetResult();await session.BothBoundaries.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42,session.Audio.Single().Data.Span[0]);Assert.Equal([SpeechBoundary.Started,SpeechBoundary.Ended],session.Boundaries.ToArray());Assert.Equal([typeof(SpeechStarted),typeof(SpeechEnded)],evidence.Select(e=>e.GetType()).ToArray());
    }
    [Fact]
    public async Task Deferred_boundary_flood_is_bounded_and_a_failed_stream_cannot_reenter()
    {
        var stream=Guid.NewGuid();var faults=new ConcurrentQueue<string>();await using var ingress=new SpeechRecognitionIngress(new ControlledSession(),stream,(_,_)=>{},(code,_)=>faults.Enqueue(code));ingress.Start();
        for(var i=0;i<SpeechRecognitionIngress.MaxPendingBoundaries;i++)Assert.True(ingress.TryAdmitBoundary(Guid.NewGuid(),SpeechBoundary.Started,0.9,480,0,stream));
        Assert.False(ingress.TryAdmitBoundary(Guid.NewGuid(),SpeechBoundary.Started,0.9,480,0,stream));Assert.False(ingress.TryAdmitAudio(Frame(1,0),stream));Assert.Equal("AudioDiscontinuity",Assert.Single(faults));
    }
    [Theory] [InlineData(12001)] [InlineData(long.MaxValue)]
    public async Task Far_future_boundary_cannot_retain_unbounded_pending_work(long offset)
    {
        var stream=Guid.NewGuid();var faults=new ConcurrentQueue<string>();await using var ingress=new SpeechRecognitionIngress(new ControlledSession(),stream,(_,_)=>{},(code,_)=>faults.Enqueue(code));ingress.Start();
        Assert.False(ingress.TryAdmitBoundary(Guid.NewGuid(),SpeechBoundary.Started,0.9,offset,0,stream));Assert.Equal("AudioDiscontinuity",Assert.Single(faults));
    }
    [Fact]
    public async Task Invalid_evidence_and_old_stream_are_rejected_without_poisoning_valid_input()
    {
        var stream=Guid.NewGuid();var faults=new ConcurrentQueue<string>();var session=new ControlledSession();await using var ingress=new SpeechRecognitionIngress(session,stream,(_,_)=>{},(code,_)=>faults.Enqueue(code));ingress.Start();var utterance=Guid.NewGuid();
        Assert.False(ingress.TryAdmitBoundary(utterance,SpeechBoundary.Started,double.NaN,0,0,stream));Assert.False(ingress.TryAdmitBoundary(utterance,SpeechBoundary.Started,0.5,0,double.PositiveInfinity,stream));Assert.False(ingress.TryAdmitBoundary(Guid.Empty,SpeechBoundary.Started,0.5,0,0,stream));
        Assert.False(ingress.TryAdmitAudio(Frame(1,0),Guid.NewGuid()));Assert.True(ingress.TryAdmitAudio(Frame(1,0),stream));Assert.Empty(faults);session.Release.TrySetResult();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Provider_worker_exceptions_are_observed_once_and_reveal_no_provider_body(bool reader)
    {
        var stream=Guid.NewGuid();var fault=new TaskCompletionSource<(string Code,string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);var session=new ControlledSession { ThrowPush=!reader,ThrowRead=reader };
        await using var ingress=new SpeechRecognitionIngress(session,stream,(_,_)=>{},(code,message)=>fault.TrySetResult((code,message)));ingress.Start();if(!reader){Assert.True(ingress.TryAdmitAudio(Frame(1,0),stream));session.Release.TrySetResult();}
        var error=await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("SpeechRecognitionFailed",error.Code);Assert.DoesNotContain("provider-secret-body",error.Message);Assert.False(ingress.TryAdmitAudio(Frame(2,480),stream));
    }
    [Fact]
    public async Task Disposal_retires_noncooperative_reader_and_completes_provider_cleanup_once()
    {
        var stream=Guid.NewGuid();var session=new ControlledSession { IgnoreReaderCancellation=true };var evidence=new ConcurrentQueue<SpeechRecognitionEvent>();var faults=new ConcurrentQueue<string>();var ingress=new SpeechRecognitionIngress(session,stream,(e,_)=>evidence.Enqueue(e),(code,_)=>faults.Enqueue(code));ingress.Start();
        await ingress.DisposeAsync();await ingress.DisposeAsync();Assert.Equal(1,session.Completed);Assert.Equal(1,session.Disposed);Assert.False(ingress.TryAdmitAudio(Frame(1,0),stream));
        session.Events.Writer.TryWrite(new SpeechStarted(Guid.NewGuid()));session.Events.Writer.TryComplete();await session.ReaderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Empty(evidence);Assert.Empty(faults);
    }
    private sealed class ControlledSession : ISpeechRecognitionSession
    {
        public bool ThrowPush,ThrowRead,IgnoreReaderCancellation;
        public int Completed,Disposed;
        public TaskCompletionSource PushEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BothBoundaries {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderFinished {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<AudioFrame> Audio {get;}=new();public ConcurrentQueue<SpeechBoundary> Boundaries {get;}=new();
        public Channel<SpeechRecognitionEvent> Events {get;}=Channel.CreateUnbounded<SpeechRecognitionEvent>();
        public async ValueTask PushAudioAsync(AudioFrame frame,CancellationToken ct=default){PushEntered.TrySetResult();await Release.Task.WaitAsync(ct);if(ThrowPush)throw new InvalidOperationException("provider-secret-body");Audio.Enqueue(frame);}
        public ValueTask ObserveBoundaryAsync(Guid id,SpeechBoundary boundary,CancellationToken ct=default){Boundaries.Enqueue(boundary);if(Boundaries.Count==2)BothBoundaries.TrySetResult();return ValueTask.CompletedTask;}
        public ValueTask CompleteInputAsync(CancellationToken ct=default){Interlocked.Increment(ref Completed);return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposed);return ValueTask.CompletedTask;}
        public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync([EnumeratorCancellation]CancellationToken ct=default)
        {
            try{if(ThrowRead)throw new InvalidOperationException("provider-secret-body");await foreach(var e in Events.Reader.ReadAllAsync(IgnoreReaderCancellation?CancellationToken.None:ct))yield return e;}
            finally{ReaderFinished.TrySetResult();}
        }
    }
}
