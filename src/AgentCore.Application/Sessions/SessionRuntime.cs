using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentCore.Application.Agents;
using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime : IAsyncDisposable
{
    private readonly Channel<SessionInput> _mailbox = Channel.CreateBounded<SessionInput>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    private readonly ConcurrentQueue<SessionInput> _urgent = new();

    private readonly ILanguageModel _languageModel;
    private readonly IAgentBrain _brain;
    private readonly IInterruptionClassifier _classifier;
    private readonly IMemoryStore _store;
    private readonly ISessionOutput _output;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RecognitionCapabilities _recognition;
    private readonly ISpeechRecognizer? _recognizer;
    private readonly ISpeechSynthesizer? _synthesizer;
    private readonly ISessionAudioOutput? _audioOutput;
    private readonly InteractionPolicy _policy;
    private readonly object _audioGate = new();
    private AudioIngress _ingress = new();
    private ISpeechRecognitionSession? _recognitionSession;
    private CancellationTokenSource? _sttCts;
    private long _expectedFrameSequence = 1;
    private long _expectedSampleOffset;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private readonly object _idleGate = new();

    private UserProfile? _profile;
    private SessionSnapshot _snapshot;
    private Guid _epoch;
    private Guid? _activeResponseId;
    private Guid? _activeEntryId;
    private readonly ResponseTextAccumulator _accumulator = new();
    private bool _responseTerminal;
    private CancellationTokenSource? _responseCts;
    private DateTimeOffset _lastCheckpoint = DateTimeOffset.MinValue;
    private int _inflight;
    private TaskCompletionSource _idle = CompletedIdle();
    private TaskCompletionSource _mailboxIdle = CompletedIdle();
    private InputActivity _input = InputActivity.Idle;
    private OutputActivity _outputActivity = OutputActivity.Idle;
    private InterruptionCandidate? _candidate;
    private ResponseLifecycle? _responseLifecycle;
    private int _timerGeneration = 1;
    private int _turnGeneration;
    private Guid? _committedUtteranceId;
    private Guid? _activeUtteranceId;
    private DateTimeOffset? _utteranceStarted;
    private double? _activityScore;
    private Guid? _streamId;
    private int _pendingVoiceGeneration;
    private bool _muted;
    private bool _helpOfferedDuringSilence;
    private DateTimeOffset? _lastInitiativeAt;
    private DateTimeOffset? _pendingInitiativeExpiresAt;
    private readonly HashSet<Guid> _environmentIds = [];
    private readonly Queue<QueuedEnvironment> _environmentQueue = new();
    private int _mailboxPressureSignaled;
    private long _ttsStartedAt;
    private long _segmentPipelineStarted;
    private long _firstAudioReadyAt;
    private long _firstFrameSentAt;
    private bool _recordedLlm;
    private long _sttMark;
    private bool _recordedStt;
    private bool _recordedSegment;
    private bool _recordedTts;
    private bool _recordedTransport;
    private bool _recordedPlayback;

    public SessionRuntime(
        SessionSnapshot snapshot,
        ILanguageModel languageModel,
        IAgentBrain brain,
        IMemoryStore store,
        ISessionOutput output,
        IIdGenerator ids,
        TimeProvider time,
        ILogger logger,
        IInterruptionClassifier? classifier = null,
        RecognitionCapabilities? recognition = null,
        InteractionPolicy? policy = null,
        ISpeechRecognizer? recognizer = null,
        ISpeechSynthesizer? synthesizer = null,
        ISessionAudioOutput? audioOutput = null)
    {
        _snapshot = snapshot;
        _languageModel = languageModel;
        _brain = brain;
        _classifier = classifier ?? new HeuristicInterruptionClassifier();
        _store = store;
        _output = output;
        _ids = ids;
        _time = time;
        _logger = logger;
        _recognizer = recognizer;
        _synthesizer = synthesizer;
        _audioOutput = audioOutput ?? output as ISessionAudioOutput;
        _recognition = recognition ?? recognizer?.Capabilities ?? new RecognitionCapabilities(true, true, true, true);
        _policy = policy ?? new InteractionPolicy();
        _input = snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        _epoch = _ids.NewId();
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    public Guid SessionId => _snapshot.SessionId;

    public SessionSnapshot Snapshot => _snapshot;

    public Task<bool> SubmitUserTextAsync(string text, Guid? sourceEventId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 8000)
        {
            throw AgentCoreErrors.Validation("Text exceeds 8000 UTF-16 code units.");
        }

        _ = cancellationToken;
        var eventId = sourceEventId ?? _ids.NewId();
        var context = new EventContext(eventId, SessionId, _epoch, _time.GetUtcNow(), eventId, null);
        BeginWork();
        return Task.FromResult(Enqueue(new UserTextReceived(context, text), urgent: false));
    }

    public Task CancelActiveResponseAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (_activeResponseId is not { } responseId)
        {
            return Task.CompletedTask;
        }

        var context = NewContext();
        BeginWork();
        _ = Enqueue(new CancelResponseReceived(context, responseId), urgent: true);
        return Task.CompletedTask;
    }

    public Task WaitUntilMailboxDrainedAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource idle;
        lock (_idleGate)
        {
            idle = _mailboxIdle;
        }

        return idle.Task.WaitAsync(cancellationToken);
    }

    public InterruptionCandidate? Candidate => _candidate;

    public InputActivity Input => _input;

    public int TimerGeneration => _timerGeneration;

    public int TurnGeneration => _turnGeneration;

    public Guid? ActiveResponseId => _activeResponseId;

    public Guid? StreamId => _streamId;

    public bool Muted => _muted;

    public bool RecognitionActive => _recognitionSession is not null;

    public OutputActivity Output => _outputActivity;

    public InteractionDecision? LastControllerDecision { get; private set; }

    public Task DetachAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new DetachReceived(context), urgent: true);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(SessionMode mode, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new SetModeReceived(context, mode), urgent: false);
        return Task.CompletedTask;
    }

    public Task AttachAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new AttachReceived(context), urgent: false);
        return Task.CompletedTask;
    }

    public Task RequestEndAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new EndSessionReceived(context), urgent: true);
        return Task.CompletedTask;
    }

    public Task SubmitSpeechAsync(
        SpeechRecognitionEvent evidence,
        double? activityScore = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new SpeechEvidenceReceived(context, evidence, activityScore), urgent: false);
        return Task.CompletedTask;
    }

    public Task SubmitClassifierResultAsync(
        Guid candidateId,
        Guid utteranceId,
        Guid responseId,
        int revision,
        InteractionDecision decision,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(
            new ClassifierReturned(context, candidateId, utteranceId, responseId, revision, decision),
            urgent: false);
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new MuteReceived(context, muted), urgent: false);
        return Task.CompletedTask;
    }

    public Task<bool> SubmitPlaybackAsync(
        Guid responseId,
        string kind,
        long consumedSamples,
        int textEndExclusive,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        return Task.FromResult(Enqueue(
            new PlaybackReportReceived(context, responseId, kind, consumedSamples, textEndExclusive),
            urgent: false));
    }

    public Task<bool?> SubmitReceiptAsync(Guid responseId, int textEndExclusive, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (!TryValidateReceipt(responseId, textEndExclusive))
        {
            return Task.FromResult<bool?>(false);
        }

        var context = NewContext();
        BeginWork();
        if (!Enqueue(new ResponseReceiptReceived(context, responseId, textEndExclusive), urgent: false))
        {
            return Task.FromResult<bool?>(null);
        }

        return Task.FromResult<bool?>(true);
    }

    public Task SubmitTimerElapsedAsync(
        string kind,
        int generation,
        Guid? utteranceId = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new TimerElapsedReceived(context, kind, generation, utteranceId), urgent: false);
        return Task.CompletedTask;
    }

    public Task SubmitEnvironmentAsync(EnvironmentEvent input, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new EnvironmentReceived(context, input), urgent: false);
        return Task.CompletedTask;
    }

    public Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource idle;
        lock (_idleGate)
        {
            idle = _idle;
        }

        return idle.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _responseCts?.Cancel();
        _ttsCts?.Cancel();
        await StopRecognitionAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _responseCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _mailbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_urgent.TryDequeue(out var urgent))
                {
                    await DispatchAsync(urgent, cancellationToken).ConfigureAwait(false);
                }

                while (_mailbox.Reader.TryRead(out var input))
                {
                    if (input is PulseReceived)
                    {
                        continue;
                    }

                    await DispatchAsync(input, cancellationToken).ConfigureAwait(false);
                    while (_urgent.TryDequeue(out var nested))
                    {
                        await DispatchAsync(nested, cancellationToken).ConfigureAwait(false);
                    }
                }

                TrySignalIdle();
            }

            while (_urgent.TryDequeue(out var rest))
            {
                await DispatchAsync(rest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DispatchAsync(SessionInput input, CancellationToken cancellationToken)
    {
        try
        {
            switch (input)
            {
                case UserTextReceived user:
                    await HandleUserTextAsync(user, cancellationToken).ConfigureAwait(false);
                    break;
                case SpeechEvidenceReceived speech:
                    await HandleSpeechAsync(speech, cancellationToken).ConfigureAwait(false);
                    break;
                case AudioIngressFaultReceived fault:
                    await HandleAudioFaultAsync(fault, cancellationToken).ConfigureAwait(false);
                    break;
                case ClassifierReturned classified:
                    await HandleClassifierAsync(classified, cancellationToken).ConfigureAwait(false);
                    break;
                case BrainReturned brain:
                    await HandleBrainAsync(brain, cancellationToken).ConfigureAwait(false);
                    brain.Processed.TrySetResult();
                    break;
                case TimerElapsedReceived timer:
                    await HandleTimerAsync(timer, cancellationToken).ConfigureAwait(false);
                    break;
                case AttachReceived attach:
                    await HandleAttachAsync(attach, cancellationToken).ConfigureAwait(false);
                    break;
                case DetachReceived detach:
                    await HandleDetachAsync(detach, cancellationToken).ConfigureAwait(false);
                    break;
                case SetModeReceived mode:
                    await HandleSetModeAsync(mode, cancellationToken).ConfigureAwait(false);
                    break;
                case MuteReceived mute:
                    await HandleMuteAsync(mute, cancellationToken).ConfigureAwait(false);
                    break;
                case ModelResultReceived model:
                    await HandleModelAsync(model, cancellationToken).ConfigureAwait(false);
                    break;
                case SynthesisResultReceived synthesis:
                    await HandleSynthesisAsync(synthesis, cancellationToken).ConfigureAwait(false);
                    break;
                case PlaybackReportReceived playback:
                    await HandlePlaybackAsync(playback, cancellationToken).ConfigureAwait(false);
                    break;
                case ResponseReceiptReceived receipt:
                    await HandleReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
                    break;
                case CancelResponseReceived cancel:
                    await HandleCancelAsync(cancel, cancellationToken).ConfigureAwait(false);
                    break;
                case EndSessionReceived ended:
                    await HandleEndAsync(ended, cancellationToken).ConfigureAwait(false);
                    break;
                case EnvironmentReceived environment:
                    await HandleEnvironmentAsync(environment, cancellationToken).ConfigureAwait(false);
                    break;
                case MailboxSaturatedReceived saturated:
                    await HandleMailboxSaturatedAsync(saturated, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mailbox processing failed for session {SessionId}", SessionId);
        }
        finally
        {
            EndWork();
        }
    }

    private bool Enqueue(SessionInput input, bool urgent)
    {
        if (urgent)
        {
            _urgent.Enqueue(input);
            _mailbox.Writer.TryWrite(new PulseReceived(input.Context));
            return true;
        }

        if (_mailbox.Writer.TryWrite(input))
        {
            return true;
        }

        EndWork();
        RuntimeTelemetry.RecordDropped("mailbox");
        if (Interlocked.Exchange(ref _mailboxPressureSignaled, 1) == 0)
        {
            BeginWork();
            _urgent.Enqueue(new MailboxSaturatedReceived(input.Context));
            _mailbox.Writer.TryWrite(new PulseReceived(input.Context));
        }

        return false;
    }

    private async Task HandleMailboxSaturatedAsync(MailboxSaturatedReceived input, CancellationToken cancellationToken)
    {
        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    null,
                    new ErrorOutput(
                        "Transport",
                        "Backpressure",
                        "The session mailbox is full. Stop or retry after the current work drains.",
                        false,
                        TimeSpan.FromSeconds(1))),
                cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Exchange(ref _mailboxPressureSignaled, 0);
    }

    private async Task HandleUserTextAsync(UserTextReceived input, CancellationToken cancellationToken)
    {
        using var activity = RuntimeTelemetry.Activity.StartActivity("user_turn");
        var started = Stopwatch.GetTimestamp();
        _recordedLlm = false;
        if (_snapshot.Entries.Any(entry => entry.SourceEventId == input.Context.EventId && entry.Role == ConversationRole.User))
        {
            return;
        }

        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(input.Context, live, cancellationToken, "newText").ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var userEntry = new ConversationEntry(
            input.Context.EventId,
            NextSequence(),
            input.Context.EventId,
            ConversationRole.User,
            input.Text,
            ResponseId: null,
            EntryStatus.Completed,
            _snapshot.Mode,
            input.Text.Length,
            input.Text.Length,
            now);

        await PersistAsync(Append(userEntry) with { Status = _snapshot.Status }, cancellationToken)
            .ConfigureAwait(false);

        var (summary, through) = ConversationSummary.Refresh(
            _snapshot.Entries,
            _snapshot.Summary,
            _snapshot.SummarizedThroughEntrySequence);
        if (summary != _snapshot.Summary || through != _snapshot.SummarizedThroughEntrySequence)
        {
            await PersistAsync(
                    _snapshot with { Summary = summary, SummarizedThroughEntrySequence = through },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _timerGeneration++;
        _environmentQueue.Clear();
        var responseId = _ids.NewId();
        var trigger = new AgentTrigger(input.Context.EventId, TriggerKind.UserTurn, input.Text);
        var turn = ++_turnGeneration;
        _helpOfferedDuringSilence = false;
        _outputActivity = OutputActivity.WaitingForAgent;
        RuntimeTelemetry.Record("controller", RuntimeTelemetry.ElapsedMs(started));
        _logger.LogInformation(
            "User turn accepted {SessionId} {EventId} chars {CharCount}",
            SessionId,
            input.Context.EventId,
            input.Text.Length);
        LaunchBrain(input.Context, trigger, responseId, turn);
    }

    private async Task HandleBrainAsync(BrainReturned input, CancellationToken cancellationToken)
    {
        if (input.TurnGeneration != _turnGeneration)
        {
            return;
        }

        if (input.Decision is Speak
            && input.Trigger.Kind != TriggerKind.UserTurn
            && !InitiativeStillEligible(input.Trigger))
        {
            RecordInitiativeEvaluation();
            _outputActivity = OutputActivity.Idle;
            ScheduleIdleTimer(Cooldown());
            return;
        }

        if (input.Decision is not Speak speakable)
        {
            RecordInitiativeEvaluation();
            _outputActivity = OutputActivity.Idle;
            ScheduleIdleTimer(Cooldown());
            return;
        }

        if (input.Trigger.Kind == TriggerKind.LongSilence)
        {
            _helpOfferedDuringSilence = true;
        }

        if (input.Trigger.Kind == TriggerKind.UnfinishedInteraction)
        {
            _snapshot = _snapshot with { PendingTopic = null, UpdatedAt = _time.GetUtcNow() };
        }

        if (input.Trigger.Kind != TriggerKind.UserTurn)
        {
            RecordInitiativeEvaluation();
        }

        var now = _time.GetUtcNow();
        var entryId = _ids.NewId();
        var sequence = NextSequence();
        var assistant = new ConversationEntry(
            entryId,
            sequence,
            SourceEventId: null,
            ConversationRole.Assistant,
            string.Empty,
            input.ResponseId,
            EntryStatus.Streaming,
            _snapshot.Mode,
            0,
            0,
            now);

        _activeResponseId = input.ResponseId;
        _activeEntryId = entryId;
        _accumulator.Reset();
        ResetSpeechOutput();
        _responseTerminal = false;
        _responseLifecycle = ResponseLifecycle.Live;
        _outputActivity = OutputActivity.AgentGenerating;
        _responseCts = new CancellationTokenSource();
        _lastCheckpoint = _time.GetUtcNow();
        await PersistAsync(Append(assistant), cancellationToken).ConfigureAwait(false);

        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    input.ResponseId,
                    new ResponseStartedOutput(entryId, sequence, input.Trigger.Kind.ToString())),
                cancellationToken)
            .ConfigureAwait(false);

        var request = speakable.Request with { ResponseId = input.ResponseId };
        BeginWork();
        var responseToken = _responseCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await PumpModelAsync(request, input.Context, responseToken).ConfigureAwait(false);
            }
            finally
            {
                EndWork();
            }
        }, CancellationToken.None);
    }

    private void LaunchBrain(EventContext cause, AgentTrigger trigger, Guid responseId, int turn)
    {
        var context = new AgentContext(
            _snapshot.Definition,
            _snapshot.Entries,
            _snapshot.Summary,
            Profile: _profile,
            _snapshot.Mode,
            _snapshot.PendingTopic,
            HelpOfferedDuringSilence: _helpOfferedDuringSilence,
            InterruptedHeardText: LastInterruptedHeardText(),
            trigger);
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                var brainStarted = Stopwatch.GetTimestamp();
                using var activity = RuntimeTelemetry.Activity.StartActivity("brain");
                var decision = await _brain.DecideAsync(context, responseId, _lifetime.Token).ConfigureAwait(false);
                RuntimeTelemetry.Record("brain", RuntimeTelemetry.ElapsedMs(brainStarted));
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var inbound = NewContext(cause.EventId);
                BeginWork();
                if (!_mailbox.Writer.TryWrite(new BrainReturned(inbound, turn, responseId, trigger, decision, processed)))
                {
                    EndWork();
                    return;
                }

                await processed.Task.ConfigureAwait(false);
            }
            finally
            {
                EndWork();
            }
        }, CancellationToken.None);
    }

    private async Task PumpModelAsync(ModelRequest request, EventContext cause, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = RuntimeTelemetry.Activity.StartActivity("model");
        try
        {
            await foreach (var evt in _languageModel.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (!_recordedLlm && evt is ModelTextDelta)
                {
                    _recordedLlm = true;
                    RuntimeTelemetry.Record("llm", RuntimeTelemetry.ElapsedMs(started));
                }
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var context = NewContext(cause.EventId);
                BeginWork();
                var admitted = _mailbox.Writer.TryWrite(new ModelResultReceived(context, request.ResponseId, evt, processed));
                if (!admitted)
                {
                    EndWork();
                    processed.TrySetResult();
                    break;
                }

                await processed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new ModelFailed(new ProviderFailure(ProviderErrorCode.Cancelled, "Generation cancelled."));
            var context = NewContext(cause.EventId);
            BeginWork();
            if (!_mailbox.Writer.TryWrite(new ModelResultReceived(context, request.ResponseId, failed, processed)))
            {
                EndWork();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Language model pump failed for {ResponseId}", request.ResponseId);
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new ModelFailed(new ProviderFailure(ProviderErrorCode.Unknown, "Generation failed."));
            BeginWork();
            if (!_mailbox.Writer.TryWrite(
                    new ModelResultReceived(NewContext(cause.EventId), request.ResponseId, failed, processed)))
            {
                EndWork();
            }
        }
    }

    private async Task HandleModelAsync(ModelResultReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId || _responseTerminal)
        {
            input.Processed.TrySetResult();
            return;
        }

        switch (input.Event)
        {
            case ModelTextDelta delta:
                var (start, text) = _accumulator.Append(delta.Text);
                await PublishAsync(
                        new SessionOutput(input.Context, input.ResponseId, new TextDeltaOutput(start, text)),
                        cancellationToken)
                    .ConfigureAwait(false);
            UpdateStreamingAssistant();
                await CheckpointStreamingAsync(cancellationToken).ConfigureAwait(false);
                if (UsesVoicePlayback)
                {
                    if (_segmentPipelineStarted == 0)
                    {
                        _segmentPipelineStarted = Stopwatch.GetTimestamp();
                    }

                    EnqueueSegments(_segmenter!.Append(text, _time.GetUtcNow()));
                    ScheduleSegmentTimer();
                    KickTts(input.Context);
                    if (_pendingSegments.Count >= 4)
                    {
                        _modelBackpressure = input.Processed;
                        return;
                    }
                }

                break;
            case ModelCompleted:
                if (UsesVoicePlayback)
                {
                    _modelDone = true;
                    await PublishAsync(
                            new SessionOutput(input.Context, input.ResponseId, new TextCompletedOutput(_accumulator.Length)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (_segmentPipelineStarted == 0)
                    {
                        _segmentPipelineStarted = Stopwatch.GetTimestamp();
                    }

                    EnqueueSegments(_segmenter!.Complete());
                    KickTts(input.Context);
                    await TryCompleteVoiceAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await CompleteAsync(input.Context, input.ResponseId, failed: false, cancellationToken).ConfigureAwait(false);
                }

                break;
            case ModelFailed:
                await CompleteAsync(input.Context, input.ResponseId, failed: true, cancellationToken).ConfigureAwait(false);
                break;
        }

        input.Processed.TrySetResult();
    }

    private async Task HandleCancelAsync(CancelResponseReceived input, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (_activeResponseId != input.ResponseId)
        {
            return;
        }

        await SupersedeAsync(input.Context, input.ResponseId, cancellationToken, "userBargeIn").ConfigureAwait(false);
        RuntimeTelemetry.Record("bargein", RuntimeTelemetry.ElapsedMs(started));
    }

    private async Task SupersedeAsync(
        EventContext context,
        Guid responseId,
        CancellationToken cancellationToken,
        string reason = "newText")
    {
        _responseLifecycle = ResponseLifecycle.Superseded;
        _outputActivity = OutputActivity.Interrupted;
        var heard = _spokenUntil.Credit(_ackedSamples);
        ApplyHeard(heard);
        InvalidateSpeechJobs();
        if (!_responseTerminal)
        {
            _responseTerminal = true;
            UpdateAssistant(EntryStatus.Interrupted);
            await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
            await PublishAsync(
                    new SessionOutput(
                        context,
                        responseId,
                        new PlaybackStopOutput(ToStopReason(reason))),
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishAsync(
                    new SessionOutput(
                        context,
                        responseId,
                        new ResponseCompletedOutput(true, HeardTextEndExclusive: heard, InterruptReason: reason)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _responseCts?.Cancel();
        _ttsCts?.Cancel();
        ClearActive();
        _turnGeneration++;
    }

    private async Task CompleteAsync(EventContext context, Guid responseId, bool failed, CancellationToken cancellationToken)
    {
        _responseTerminal = true;
        _responseLifecycle = failed ? ResponseLifecycle.Failed : ResponseLifecycle.Completed;
        _outputActivity = OutputActivity.Idle;
        var status = failed ? EntryStatus.Failed : EntryStatus.Completed;
        UpdateAssistant(status);
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        if (!failed)
        {
            await PublishAsync(
                    new SessionOutput(context, responseId, new TextCompletedOutput(_accumulator.Length)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var heard = CurrentHeard();
        await PublishAsync(
                new SessionOutput(
                    context,
                    responseId,
                    new ResponseCompletedOutput(failed, HeardTextEndExclusive: heard)),
                cancellationToken)
            .ConfigureAwait(false);
        ClearActive();
        await DrainEnvironmentAsync(context, cancellationToken).ConfigureAwait(false);
        ScheduleIdleTimer(SilenceThreshold());
        if (_snapshot.PendingMode == SessionMode.Voice)
        {
            await ApplyModeAsync(SessionMode.Voice, cancellationToken).ConfigureAwait(false);
            await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private void UpdateStreamingAssistant() => UpdateAssistant(EntryStatus.Streaming);

    private void UpdateAssistant(EntryStatus status)
    {
        if (_activeEntryId is not { } entryId)
        {
            return;
        }

        var entries = _snapshot.Entries.Select(entry =>
                entry.EntryId == entryId
                    ? entry with
                    {
                        Text = _accumulator.Text,
                        Status = status
                    }
                    : entry)
            .ToArray();
        _snapshot = _snapshot with { Entries = entries, UpdatedAt = _time.GetUtcNow() };
    }

    private int CurrentHeard()
    {
        if (_activeEntryId is not { } entryId)
        {
            return 0;
        }

        var entry = _snapshot.Entries.FirstOrDefault(item => item.EntryId == entryId);
        return entry?.HeardTextEndExclusive ?? 0;
    }

    private bool TryValidateReceipt(Guid responseId, int textEndExclusive)
    {
        var generated = _activeResponseId == responseId
            ? _accumulator.Length
            : _snapshot.Entries.FirstOrDefault(entry => entry.ResponseId == responseId)?.Text.Length ?? -1;
        if (generated < 0 || textEndExclusive > generated)
        {
            return false;
        }

        var entry = _snapshot.Entries.FirstOrDefault(item => item.ResponseId == responseId);
        if (entry is not null && entry.Status == EntryStatus.Interrupted && textEndExclusive > entry.ReceivedTextEndExclusive)
        {
            return false;
        }

        return true;
    }

    private async Task HandleReceiptAsync(ResponseReceiptReceived input, CancellationToken cancellationToken)
    {
        var entry = _snapshot.Entries.FirstOrDefault(item => item.ResponseId == input.ResponseId);
        if (entry is null)
        {
            return;
        }

        if (entry.Status == EntryStatus.Interrupted)
        {
            return;
        }

        var generated = entry.EntryId == _activeEntryId ? _accumulator.Length : entry.Text.Length;
        var received = Math.Clamp(input.TextEndExclusive, entry.ReceivedTextEndExclusive, generated);
        if (received == entry.ReceivedTextEndExclusive)
        {
            return;
        }

        var heard = entry.DeliveryMode == SessionMode.Text ? received : entry.HeardTextEndExclusive;
        var entries = _snapshot.Entries.Select(item =>
                item.ResponseId == input.ResponseId
                    ? item with { ReceivedTextEndExclusive = received, HeardTextEndExclusive = heard }
                    : item)
            .ToArray();
        _snapshot = _snapshot with { Entries = entries, UpdatedAt = _time.GetUtcNow() };
        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
    }

    private void ClearActive()
    {
        _activeResponseId = null;
        _activeEntryId = null;
        _responseCts?.Dispose();
        _responseCts = null;
        _ttsCts?.Dispose();
        _ttsCts = null;
    }

    private static string ToStopReason(string reason) => reason switch
    {
        "disconnected" => "disconnected",
        "ended" => "ended",
        "modeChange" => "modeChange",
        _ => "interrupted"
    };

    private async Task HandleEndAsync(EndSessionReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(input.Context, live, cancellationToken, "ended").ConfigureAwait(false);
        }

        _input = InputActivity.Idle;
        await StopRecognitionAsync().ConfigureAwait(false);
        await PersistAsync(
                _snapshot with { Status = SessionStatus.Ended, PendingMode = null, UpdatedAt = _time.GetUtcNow() },
                cancellationToken)
            .ConfigureAwait(false);
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private SessionSnapshot Append(ConversationEntry entry)
    {
        var entries = _snapshot.Entries.Concat([entry]).ToArray();
        return _snapshot with
        {
            Entries = entries,
            UpdatedAt = _time.GetUtcNow()
        };
    }

    private long NextSequence() =>
        _snapshot.Entries.Count == 0 ? 1 : _snapshot.Entries[^1].Sequence + 1;

    private async Task PersistAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Entries.Count > 1000)
        {
            throw AgentCoreErrors.Validation("Session entry limit of 1000 was reached.");
        }

        using var activity = RuntimeTelemetry.Activity.StartActivity("persist");
        var started = Stopwatch.GetTimestamp();
        var next = snapshot with { Revision = _snapshot.Revision + 1, UpdatedAt = _time.GetUtcNow() };
        await _store.SaveAsync(next, _snapshot.Revision, cancellationToken).ConfigureAwait(false);
        _snapshot = next;
        RuntimeTelemetry.Record("persist", RuntimeTelemetry.ElapsedMs(started));
    }

    private async Task CheckpointStreamingAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - _lastCheckpoint < TimeSpan.FromSeconds(1))
        {
            return;
        }

        await PersistAsync(_snapshot, cancellationToken).ConfigureAwait(false);
        _lastCheckpoint = _time.GetUtcNow();
    }

    private async Task PublishAsync(SessionOutput output, CancellationToken cancellationToken)
    {
        if (output.Payload is AudioFrameOutput frame)
        {
            NoteAudioTransport();
            if (_audioOutput is not null)
            {
                await _audioOutput.PublishAsync(
                        new ResponseAudio(
                            output.Context.SessionId,
                            output.ResponseId ?? Guid.Empty,
                            frame.FrameSequence,
                            frame.SampleOffset,
                            frame.IsFinal,
                            frame.Data),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        await _output.PublishAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private void NoteAudioTransport()
    {
        if (_firstFrameSentAt == 0)
        {
            _firstFrameSentAt = Stopwatch.GetTimestamp();
        }

        if (_recordedTransport)
        {
            return;
        }

        _recordedTransport = true;
        RuntimeTelemetry.Record(
            "transport",
            RuntimeTelemetry.ElapsedMs(_firstAudioReadyAt != 0 ? _firstAudioReadyAt : _ttsStartedAt));
    }

    private EventContext NewContext(Guid? causationId = null)
    {
        var id = _ids.NewId();
        return new EventContext(id, SessionId, _epoch, _time.GetUtcNow(), id, causationId);
    }

    private void BeginWork()
    {
        lock (_idleGate)
        {
            _inflight++;
            if (_idle.Task.IsCompleted)
            {
                _idle = NewIdle();
            }

            if (_mailboxIdle.Task.IsCompleted)
            {
                _mailboxIdle = NewIdle();
            }
        }
    }

    private void EndWork()
    {
        lock (_idleGate)
        {
            _inflight--;
            if (_mailbox.Reader.Count == 0)
            {
                _mailboxIdle.TrySetResult();
            }

            if (_inflight == 0 && _mailbox.Reader.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private void TrySignalIdle()
    {
        lock (_idleGate)
        {
            if (_mailbox.Reader.Count == 0)
            {
                _mailboxIdle.TrySetResult();
            }

            if (_inflight == 0 && _mailbox.Reader.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource NewIdle() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = NewIdle();
        idle.TrySetResult();
        return idle;
    }
}
