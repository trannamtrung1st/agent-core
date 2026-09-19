using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using AgentCore.Application.Agents;
using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime : IAsyncDisposable
{
    private readonly Channel<SessionInput> _mailbox = Channel.CreateBounded<SessionInput>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Channel<SessionInput> _urgent = Channel.CreateUnbounded<SessionInput>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _wake = new(0);

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
    private readonly IAttachmentStore? _attachments;
    private readonly IAttachmentProcessor? _processor;
    private readonly IArtifactReferenceAuthorizer _artifacts;
    private readonly SessionToolExecutor _tools;
    private readonly InteractionPolicy _policy;
    private readonly VoiceAvailability _voice;
    private readonly object _audioGate = new();
    private AudioIngress _ingress = new();
    private ISpeechRecognitionSession? _recognitionSession;
    private CancellationTokenSource? _sttCts;
    private long _expectedFrameSequence = 1;
    private long _expectedSampleOffset;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _brainEvaluationCts;
    private readonly Task _loop;
    private readonly Task _persistLoop;
    private readonly Channel<PersistJob> _persistJobs = Channel.CreateUnbounded<PersistJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<long, PersistJob> _pendingPersist = new();
    private readonly object _idleGate = new();
    private TaskCompletionSource _abandonPersist = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _persistToken;
    private long _terminalFence;
    private long _durableRevision;
    private SessionSnapshot _durableSnapshot;

    private UserProfile? _profile;
    private SessionSnapshot _snapshot;
    private Guid _epoch;
    private Guid? _activeResponseId;
    private Guid? _activeEntryId;
    private readonly ResponseTextAccumulator _accumulator = new();
    private ResponseEnvelope? _envelope;
    private int _publishedDisplayLength;
    private readonly HashSet<string> _publishedBlockIds = new(StringComparer.Ordinal);
    private bool _ttsSourceLocked;
    private bool _ttsUsesSpeech;
    private int _ttsFedLength;
    private bool _responseTerminal;
    private string? _modelFinishReason;
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
    private int _deadlineTimerGeneration;
    private int _speechEpoch;
    private int _maxUtteranceGeneration;
    private int _turnGeneration;
    private Guid? _committedUtteranceId;
    private Guid? _activeUtteranceId;
    private int _speechPartialRevision = -1;
    private DateTimeOffset? _utteranceStarted;
    private double? _activityScore;
    private Guid? _streamId;
    private int _pendingVoiceGeneration;
    private bool _muted;
    private bool _helpOfferedDuringSilence;
    private int _consecutiveProactiveSpeaks;
    private int _proactiveSpeaksThisSilence;
    private int _silentEvaluations;
    private DateTimeOffset _lastMeaningfulActivityAt;
    private bool _initiativeHeld;
    private bool _pendingUploadHold;
    private bool _deactivated;
    private bool _proactiveBrainInFlight;
    private int _completionGeneration;
    private CancellationTokenSource? _completionCts;
    private DateTimeOffset? _lastInitiativeAt;
    private DateTimeOffset? _pendingInitiativeExpiresAt;
    private TimeSpan? _pendingPostResponseIdleDelay;
    private string _pendingPostResponseIdleClamp = "n/a";
    private TimeSpan? _lastArmedIdleDelay;
    private readonly HashSet<Guid> _environmentIds = [];
    private readonly HashSet<Guid> _undurableUserEntryIds = [];
    private bool _allowQueuedSuffixAutoDispatch;
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
        ISessionAudioOutput? audioOutput = null,
        IAttachmentStore? attachments = null,
        IAttachmentProcessor? processor = null,
        IArtifactReferenceAuthorizer? artifacts = null,
        SessionToolExecutor? tools = null,
        VoiceAvailability? voice = null)
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
        _attachments = attachments;
        _processor = processor;
        _artifacts = artifacts ?? new FixtureArtifactReferenceAuthorizer();
        _tools = tools ?? new SessionToolExecutor();
        _voice = voice ?? new VoiceAvailability { SpeechAdaptersResolved = true };
        _recognition = recognition
            ?? _voice.EffectivePlan.RecognitionCapabilities
            ?? recognizer?.Capabilities
            ?? new RecognitionCapabilities(true, true, true, true);
        _policy = policy ?? new InteractionPolicy();
        _input = snapshot.Mode == SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle;
        _lastMeaningfulActivityAt = snapshot.LastUserActivityAt ?? snapshot.CreatedAt;
        _epoch = _ids.NewId();
        _durableRevision = snapshot.Revision;
        _durableSnapshot = snapshot;
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
        _persistLoop = Task.Run(() => RunPersistAsync(_lifetime.Token));
    }

    public Guid SessionId => _snapshot.SessionId;

    public SessionSnapshot Snapshot => _snapshot;

    public async Task ApplyReopenedSnapshotAsync(SessionSnapshot reopened, CancellationToken cancellationToken = default)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new ReopenedSnapshotReceived(context, reopened, applied), urgent: true))
        {
            applied.TrySetResult();
            EndWork();
            return;
        }

        await applied.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyTransportResumedSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new TransportResumedSnapshotReceived(context, snapshot, applied), urgent: true))
        {
            applied.TrySetResult();
            EndWork();
            return;
        }

        await applied.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SubmitUserTextAsync(
        string text,
        Guid? sourceEventId = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Guid>? attachmentIds = null,
        UserTextBehavior behavior = UserTextBehavior.Interrupt)
    {
        ValidateUserTurn(text, attachmentIds);
        _ = cancellationToken;
        var eventId = sourceEventId ?? _ids.NewId();
        var context = new EventContext(eventId, SessionId, _epoch, _time.GetUtcNow(), eventId, null);
        BeginWork();
        return Task.FromResult(Enqueue(
            new UserTextReceived(context, text, AttachmentIds: attachmentIds, Behavior: behavior),
            urgent: false));
    }

    public async Task<bool?> SubmitPersistedUserTextAsync(
        string text,
        Guid sourceEventId,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Guid>? attachmentIds = null,
        UserTextBehavior behavior = UserTextBehavior.Interrupt)
    {
        ValidateUserTurn(text, attachmentIds);
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new EventContext(sourceEventId, SessionId, _epoch, _time.GetUtcNow(), sourceEventId, null);
        BeginWork();
        if (!Enqueue(new UserTextReceived(context, text, persisted, attachmentIds, behavior), urgent: false))
        {
            persisted.TrySetResult(false);
            return null;
        }

        return await WaitOrCancelAsync(persisted, false, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> StageAttachmentsAsync(IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        return Task.FromResult(Enqueue(new AttachmentsStagedReceived(context, attachmentIds), urgent: true));
    }

    private void ValidateUserTurn(string text, IReadOnlyList<Guid>? attachmentIds)
    {
        text ??= "";
        var hasAttachments = attachmentIds is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(text) && !hasAttachments)
        {
            throw AgentCoreErrors.Validation("text is required.");
        }

        if (text.Length > 8000)
        {
            throw AgentCoreErrors.Validation("Text exceeds 8000 UTF-16 code units.");
        }
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

    public async Task<ResponseCancelResult?> CancelResponseAsync(
        Guid expectedResponseId,
        CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource<ResponseCancelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new CancelResponseReceived(context, expectedResponseId, completed), urgent: true))
        {
            completed.TrySetResult(ResponseCancelResult.Unknown);
            return null;
        }

        return await WaitOrCancelAsync(completed, ResponseCancelResult.Unknown, cancellationToken).ConfigureAwait(false);
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

    public TimeSpan? LastArmedIdleDelay => _lastArmedIdleDelay;

    public int MaxUtteranceGeneration => _maxUtteranceGeneration;

    private void InvalidateMaxUtteranceTimer() => _maxUtteranceGeneration++;

    public int TurnGeneration => _turnGeneration;

    public Guid? ActiveResponseId => _activeResponseId;

    public Guid? StreamId => _streamId;

    public bool Muted => _muted;

    public bool RecognitionActive => _recognitionSession is not null;

    public OutputActivity Output => _outputActivity;

    public InteractionDecision? LastControllerDecision { get; private set; }

    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new DetachReceived(context, detached), urgent: true))
        {
            return;
        }

        await detached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SetModeAsync(SessionMode mode, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new SetModeReceived(context, mode), urgent: false);
        return Task.CompletedTask;
    }

    public async Task<bool> AttachAsync(CancellationToken cancellationToken = default)
    {
        var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new AttachReceived(context, attached), urgent: false))
        {
            attached.TrySetResult(false);
            return false;
        }

        return await WaitOrCancelAsync(attached, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestEndAsync(CancellationToken cancellationToken = default)
    {
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new EndSessionReceived(context, persisted), urgent: true))
        {
            persisted.TrySetResult(false);
            return false;
        }

        return await WaitOrCancelAsync(persisted, false, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RequestDeactivateAsync(CancellationToken cancellationToken = default) =>
        RequestLifecycleTransitionAsync(
            SessionLifecycleStatus.Paused,
            LifecycleTransitionSource.User,
            "manual",
            cancellationToken);

    public async Task<bool> RequestLifecycleTransitionAsync(
        SessionLifecycleStatus target,
        LifecycleTransitionSource source,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new LifecycleTransitionReceived(context, target, source, reason, persisted), urgent: true))
        {
            persisted.TrySetResult(false);
            return false;
        }

        return await WaitOrCancelAsync(persisted, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestRenameAsync(string title, CancellationToken cancellationToken = default)
    {
        var trimmed = title.Trim();
        if (trimmed.Length is 0 or > SessionTitles.MaxLength)
        {
            throw AgentCoreErrors.Validation("Title must be between 1 and 200 characters.");
        }

        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new RenameReceived(context, trimmed, persisted), urgent: true))
        {
            persisted.TrySetResult(false);
            return false;
        }

        return await WaitOrCancelAsync(persisted, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestSpeechLocaleAsync(string? locale, CancellationToken cancellationToken = default)
    {
        var normalized = SpeechLocale.NormalizeOverride(locale);
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new SpeechLocaleReceived(context, normalized, persisted), urgent: true))
        {
            persisted.TrySetResult(false);
            return false;
        }

        return await WaitOrCancelAsync(persisted, false, cancellationToken).ConfigureAwait(false);
    }

    public Task SubmitInitiativeHoldAsync(bool held, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var context = NewContext();
        BeginWork();
        Enqueue(new InitiativeHoldReceived(context, held), urgent: true);
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
        Enqueue(new SpeechEvidenceReceived(context, evidence, activityScore, _speechEpoch), urgent: false);
        return Task.CompletedTask;
    }

    public bool CanAdmitClientTranscriptEvidence() =>
        _snapshot.Status == SessionStatus.Attached
        && _snapshot.Mode == SessionMode.Voice
        && !_muted
        && _voice.EffectivePlan.InputTransport == SpeechTransport.ClientTranscript;

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

    public async Task<bool?> SubmitPlaybackAsync(
        Guid responseId,
        string kind,
        long consumedSamples,
        int textEndExclusive,
        CancellationToken cancellationToken = default)
    {
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new PlaybackReportReceived(context, responseId, kind, consumedSamples, textEndExclusive, admitted), urgent: false))
        {
            return null;
        }

        return await WaitOrCancelAsync(admitted, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool?> SubmitReceiptAsync(
        Guid responseId,
        int textEndExclusive,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? blockIds = null)
    {
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new ResponseReceiptReceived(context, responseId, textEndExclusive, admitted, blockIds), urgent: false))
        {
            return null;
        }

        return await WaitOrCancelAsync(admitted, false, cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> WaitOrCancelAsync<T>(
        TaskCompletionSource<T> waiter,
        T cancelled,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return cancelled;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _persistJobs.Writer.TryComplete();
        foreach (var pair in _pendingPersist)
        {
            if (!_pendingPersist.TryRemove(pair.Key, out var job))
            {
                continue;
            }

            job.Then = null;
            job.Ended?.TrySetResult(false);
            job.Applied.TrySetResult();
        }

        _mailbox.Writer.TryComplete();
        _urgent.Writer.TryComplete();
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _persistLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _responseCts?.Cancel();
        _ttsCts?.Cancel();
        await StopRecognitionAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _responseCts?.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (_urgent.Reader.TryRead(out var urgent))
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
                    while (_urgent.Reader.TryRead(out var nested))
                    {
                        await DispatchAsync(nested, cancellationToken).ConfigureAwait(false);
                    }
                }

                TrySignalIdle();
                try
                {
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }

            while (_urgent.Reader.TryRead(out var rest))
            {
                await DispatchAsync(rest, cancellationToken).ConfigureAwait(false);
            }

            while (_mailbox.Reader.TryRead(out var leftover))
            {
                if (leftover is PulseReceived)
                {
                    continue;
                }

                await DispatchAsync(leftover, cancellationToken).ConfigureAwait(false);
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
                case AttachmentsStagedReceived staged:
                    await HandleAttachmentsStagedAsync(staged, cancellationToken).ConfigureAwait(false);
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
                case AttachmentsProcessedReceived processed:
                    await HandleAttachmentsProcessedAsync(processed, cancellationToken).ConfigureAwait(false);
                    break;
                case ToolActivityReceived tools:
                    await HandleToolActivityAsync(tools, cancellationToken).ConfigureAwait(false);
                    break;
                case BrainReturned brain:
                    try
                    {
                        await HandleBrainAsync(brain, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (brain.Trigger.Kind != TriggerKind.UserTurn && ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Proactive brain handling failed for session {SessionId}", SessionId);
                        RecoverProactiveHandleFailure(brain);
                    }
                    finally
                    {
                        if (brain.Trigger.Kind != TriggerKind.UserTurn)
                        {
                            _proactiveBrainInFlight = false;
                        }

                        brain.Processed.TrySetResult();
                    }

                    break;
                case BrainFailed brainFailed:
                    try
                    {
                        await HandleBrainFailedAsync(brainFailed, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (brainFailed.Trigger.Kind != TriggerKind.UserTurn)
                        {
                            _proactiveBrainInFlight = false;
                        }

                        brainFailed.Processed.TrySetResult();
                    }

                    break;
                case TimerElapsedReceived timer:
                    await HandleTimerAsync(timer, cancellationToken).ConfigureAwait(false);
                    break;
                case CompletionReturned completion:
                    try
                    {
                        await HandleCompletionReturnedAsync(completion, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        completion.Processed.TrySetResult();
                    }

                    break;
                case AttachReceived attach:
                    await HandleAttachAsync(attach, cancellationToken).ConfigureAwait(false);
                    break;
                case DetachReceived detach:
                    await HandleDetachAsync(detach, CancellationToken.None).ConfigureAwait(false);
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
                case LifecycleTransitionReceived lifecycle:
                    await HandleLifecycleTransitionAsync(lifecycle, cancellationToken).ConfigureAwait(false);
                    break;
                case RenameReceived rename:
                    await HandleRenameAsync(rename, cancellationToken).ConfigureAwait(false);
                    break;
                case SpeechLocaleReceived speechLocale:
                    await HandleSpeechLocaleAsync(speechLocale, cancellationToken).ConfigureAwait(false);
                    break;
                case InitiativeHoldReceived hold:
                    HandleInitiativeHold(hold);
                    break;
                case ReopenedSnapshotReceived reopened:
                    HandleReopenedSnapshot(reopened);
                    break;
                case TransportResumedSnapshotReceived transportResumed:
                    HandleTransportResumedSnapshot(transportResumed);
                    break;
                case EnvironmentReceived environment:
                    await HandleEnvironmentAsync(environment, cancellationToken).ConfigureAwait(false);
                    break;
                case MailboxSaturatedReceived saturated:
                    await HandleMailboxSaturatedAsync(saturated, cancellationToken).ConfigureAwait(false);
                    break;
                case PersistCompletedReceived persist:
                    await HandlePersistCompletedAsync(persist, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Mailbox processing failed for session {SessionId}", SessionId);
            if (input is AttachReceived or EndSessionReceived or LifecycleTransitionReceived or RenameReceived or SpeechLocaleReceived or DetachReceived)
            {
                CompleteInputWaiters(input, false);
            }
        }
        catch (OperationCanceledException)
        {
            CompleteInputWaiters(input, false);
        }
        finally
        {
            EndWork();
            if (input is DetachReceived detach)
            {
                detach.Detached.TrySetResult();
            }
            if (input is PlaybackReportReceived playback)
            {
                playback.Admitted.TrySetResult(false);
            }

            if (input is ResponseReceiptReceived receipt)
            {
                receipt.Admitted.TrySetResult(false);
            }
        }
    }

    private bool Enqueue(SessionInput input, bool urgent)
    {
        if (urgent)
        {
            if (!_urgent.Writer.TryWrite(input))
            {
                EndWork();
                CompleteInputWaiters(input, false);
                return false;
            }

            Pulse();
            return true;
        }

        if (TryMailbox(input))
        {
            return true;
        }

        EndWork();
        CompleteInputWaiters(input, false);
        RuntimeTelemetry.RecordDropped("mailbox");
        if (Interlocked.Exchange(ref _mailboxPressureSignaled, 1) == 0)
        {
            BeginWork();
            _ = Enqueue(new MailboxSaturatedReceived(input.Context), urgent: true);
        }

        return false;
    }

    private bool TryMailbox(SessionInput input)
    {
        if (!_mailbox.Writer.TryWrite(input))
        {
            return false;
        }

        Pulse();
        return true;
    }

    private void Pulse()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static void CompleteInputWaiters(SessionInput input, bool value)
    {
        switch (input)
        {
            case EndSessionReceived ended:
                ended.Persisted.TrySetResult(value);
                break;
            case LifecycleTransitionReceived lifecycle:
                lifecycle.Persisted.TrySetResult(value);
                break;
            case RenameReceived rename:
                rename.Persisted.TrySetResult(value);
                break;
            case SpeechLocaleReceived speechLocale:
                speechLocale.Persisted.TrySetResult(value);
                break;
            case AttachReceived attach:
                attach.Attached.TrySetResult(value);
                break;
            case DetachReceived detach:
                detach.Detached.TrySetResult();
                break;
            case PlaybackReportReceived playback:
                playback.Admitted.TrySetResult(value);
                break;
            case ResponseReceiptReceived receipt:
                receipt.Admitted.TrySetResult(value);
                break;
            case BrainReturned brain:
                brain.Processed.TrySetResult();
                break;
            case ModelResultReceived model:
                model.Processed.TrySetResult();
                break;
            case ToolActivityReceived tools:
                tools.Admitted.TrySetResult(false);
                break;
            case SynthesisResultReceived synthesis:
                synthesis.Processed.TrySetResult();
                break;
        }
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

    private async Task HandleAttachmentsStagedAsync(AttachmentsStagedReceived input, CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            return;
        }

        await _attachments.StageForNextTurnAsync(SessionId, input.AttachmentIds, cancellationToken).ConfigureAwait(false);
        _pendingUploadHold = true;
        NoteUserActivity();
    }

    private async Task HandleUserTextAsync(UserTextReceived input, CancellationToken cancellationToken)
    {
        using var activity = RuntimeTelemetry.Activity.StartActivity("user_turn");
        var started = Stopwatch.GetTimestamp();
        _recordedLlm = false;
        var attachmentIds = NormalizeAttachmentIds(input.AttachmentIds);
        var fingerprint = UserTextAdmission.Fingerprint(null, input.Text ?? "", attachmentIds, input.Behavior);
        if (_snapshot.Entries.FirstOrDefault(entry =>
                entry.SourceEventId == input.Context.EventId && entry.Role == ConversationRole.User)
            is { } existingByEvent)
        {
            var storedFingerprint = existingByEvent.SourceAdmissionFingerprint
                ?? UserTextAdmission.FingerprintFromStoredEntry(existingByEvent);
            if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                input.Persisted?.TrySetException(new AgentCoreException(
                    "ProtocolError",
                    "Repeated eventId with a different payload.",
                    400,
                    fatal: true));
                return;
            }

            if (_undurableUserEntryIds.Contains(existingByEvent.EntryId))
            {
                RollbackAdmittedUserEntry(existingByEvent.EntryId);
            }
            else
            {
                if (attachmentIds.Count > 0 && _attachments is not null)
                {
                    await _attachments.BindToEntryAsync(SessionId, existingByEvent.EntryId, attachmentIds, cancellationToken)
                        .ConfigureAwait(false);
                }

                input.Persisted?.TrySetResult(true);
                return;
            }
        }

        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending or SessionStatus.Paused
            || SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus))
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        if (_deactivated)
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(input.Text) && attachmentIds.Count == 0)
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        try
        {
            if (attachmentIds.Count > 0)
            {
                if (_attachments is null)
                {
                    throw AgentCoreErrors.Validation("Attachments are not available.");
                }

                await _attachments.ValidateBindableAsync(SessionId, attachmentIds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (AgentCoreException)
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        if (_snapshot.Entries.Count >= 1000)
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        var now = _time.GetUtcNow();
        var text = input.Text ?? "";
        try
        {
            UserTextAdmission.ValidatePendingBatch(_snapshot.Entries, text);
        }
        catch (AgentCoreException)
        {
            input.Persisted?.TrySetResult(false);
            return;
        }

        var attachmentRefs = await BuildAttachmentRefsAsync(attachmentIds, cancellationToken).ConfigureAwait(false);
        var userEntry = new ConversationEntry(
            input.Context.EventId,
            NextSequence(),
            input.Context.EventId,
            ConversationRole.User,
            text,
            ResponseId: null,
            EntryStatus.Completed,
            _snapshot.Mode,
            text.Length,
            text.Length,
            now,
            Attachments: attachmentRefs.Count == 0 ? null : attachmentRefs,
            SourceAdmissionFingerprint: fingerprint);

        _undurableUserEntryIds.Add(userEntry.EntryId);

        var titleHints = await AttachmentTitleHintsAsync(attachmentIds, cancellationToken).ConfigureAwait(false);
        _snapshot = Append(userEntry, titleHints) with { Status = _snapshot.Status };
        var (summary, through) = ConversationSummary.Refresh(
            _snapshot.Entries,
            _snapshot.Summary,
            _snapshot.SummarizedThroughEntrySequence);
        if (summary != _snapshot.Summary || through != _snapshot.SummarizedThroughEntrySequence)
        {
            _snapshot = _snapshot with { Summary = summary, SummarizedThroughEntrySequence = through };
        }

        var queued = input.Behavior == UserTextBehavior.Queue && _activeResponseId is not null;
        var cause = input.Context;
        if (_snapshot.Mode == SessionMode.Voice)
        {
            AbandonLiveSpeech(rotateEpoch: false);
            await PublishStateAsync(cause, cancellationToken).ConfigureAwait(false);
        }

        if (!queued)
        {
            _timerGeneration++;
            _environmentQueue.Clear();
        }

        if (!queued && _activeResponseId is { } liveResponse)
        {
            await TerminalizeActiveResponseAsync(cause, liveResponse, cancellationToken, "newText", requestPersist: false)
                .ConfigureAwait(false);
            await ApplyPendingVoiceIfIdleAsync(cause, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            RequestPersist(
                _snapshot,
                PersistKind.Normal,
                then: async ct =>
                {
                    if (attachmentIds.Count > 0 && _attachments is not null)
                    {
                        try
                        {
                            await _attachments.BindToEntryAsync(SessionId, userEntry.EntryId, attachmentIds, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (AgentCoreException)
                        {
                            return;
                        }
                    }

                    var wire = UserTextBehaviors.WireName(input.Behavior);
                    if (queued)
                    {
                        UserTextQueueTelemetry.Record(wire, queued: true);
                        if (_allowQueuedSuffixAutoDispatch && _activeResponseId is null)
                        {
                            if (await TryStartPendingUserBatchAsync(cause, ct).ConfigureAwait(false))
                            {
                                _allowQueuedSuffixAutoDispatch = false;
                            }
                        }

                        return;
                    }

                    UserTextQueueTelemetry.Record(wire, queued: false);
                    RuntimeTelemetry.Record("controller", RuntimeTelemetry.ElapsedMs(started));
                    _logger.LogInformation(
                        "User turn accepted {SessionId} {EventId} chars {CharCount}",
                        SessionId,
                        cause.EventId,
                        text.Length);
                    await TryStartPendingUserBatchAsync(cause, ct).ConfigureAwait(false);
                },
                ended: input.Persisted);
        }
        catch (AgentCoreException)
        {
            RollbackAdmittedUserEntry(userEntry.EntryId);
            input.Persisted?.TrySetResult(false);
            return;
        }
    }

    private void RollbackAdmittedUserEntry(Guid entryId)
    {
        if (!_undurableUserEntryIds.Remove(entryId))
        {
            return;
        }

        if (_snapshot.Entries.All(entry => entry.EntryId != entryId))
        {
            return;
        }

        var entries = _snapshot.Entries.Where(entry => entry.EntryId != entryId).ToArray();
        var (summary, through) = ConversationSummary.Refresh(
            entries,
            _snapshot.Summary,
            _snapshot.SummarizedThroughEntrySequence);
        _snapshot = _snapshot with
        {
            Entries = entries,
            Summary = summary,
            SummarizedThroughEntrySequence = through,
            UpdatedAt = _time.GetUtcNow()
        };
    }

    private async Task HandleBrainAsync(BrainReturned input, CancellationToken cancellationToken)
    {
        var proactive = input.Trigger.Kind != TriggerKind.UserTurn;
        if (_deactivated || input.TurnGeneration != _turnGeneration)
        {
            if (proactive)
            {
                InitiativeEvaluationTelemetry.RecordDisposition(
                    input.Trigger,
                    input.Decision,
                    admitted: false,
                    blockReason: "superseded");
            }

            return;
        }

        if (proactive && IsStaleProactiveDecision(input.Trigger))
        {
            InitiativeEvaluationTelemetry.RecordDisposition(
                input.Trigger,
                input.Decision,
                admitted: false,
                blockReason: "stale");
            await DeclineInitiativeAsync(
                input.Context,
                new StaySilent("Initiative declined.", CountsTowardSilentCap: false),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (input.Decision is RequestDeactivate)
        {
            if (proactive)
            {
                InitiativeEvaluationTelemetry.RecordDisposition(input.Trigger, input.Decision, admitted: true, blockReason: "deactivate");
            }

            InitiativeWaitTelemetry.RecordIgnoredDeactivate(_snapshot.Mode);
            await ApplyDeactivateAsync(input.Context, cancellationToken, pauseReason: "initiative")
                .ConfigureAwait(false);
            return;
        }

        if (input.Decision is not Speak speakable)
        {
            if (input.Decision is StaySilent silent
                && silent.CountsTowardSilentCap
                && proactive
                && !HasPendingUserBatch())
            {
                _silentEvaluations++;
            }

            if (proactive)
            {
                InitiativeEvaluationTelemetry.RecordDisposition(
                    input.Trigger,
                    input.Decision,
                    admitted: false,
                    blockReason: InitiativeEvaluationTelemetry.DispositionBlockReason(input.Decision));
            }

            await DeclineInitiativeAsync(input.Context, input.Decision, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (proactive)
        {
            InitiativeEvaluationTelemetry.RecordDisposition(
                input.Trigger,
                input.Decision,
                admitted: true,
                blockReason: "admitted");
        }

        await StartSpeakPathAsync(input, speakable, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleBrainFailedAsync(BrainFailed input, CancellationToken cancellationToken)
    {
        if (input.TurnGeneration != _turnGeneration)
        {
            return;
        }

        if (input.Trigger.Kind == TriggerKind.UserTurn)
        {
            await PublishOutputIdleAsync(input.Context, cancellationToken).ConfigureAwait(false);
            if (input.Recoverable)
            {
                await PublishAsync(
                        new SessionOutput(
                            input.Context,
                            null,
                            new ErrorOutput(
                                "Session",
                                "BrainFailed",
                                "The agent could not prepare a response.",
                                false,
                                null)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        InitiativeEvaluationTelemetry.RecordDisposition(
            input.Trigger,
            new StaySilent("Initiative provider failed.", CountsTowardSilentCap: false),
            admitted: false,
            blockReason: "provider_failed");
        await DeclineInitiativeAsync(
            input.Context,
            new StaySilent("Initiative evaluation failed.", CountsTowardSilentCap: false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StartSpeakPathAsync(BrainReturned input, Speak speakable, CancellationToken cancellationToken)
    {
        if (input.Trigger.Kind == TriggerKind.LongSilence)
        {
            _helpOfferedDuringSilence = true;
            _consecutiveProactiveSpeaks++;
            _proactiveSpeaksThisSilence++;
        }

        if (input.Trigger.Kind == TriggerKind.UnfinishedInteraction)
        {
            _snapshot = _snapshot with { PendingTopic = null, UpdatedAt = _time.GetUtcNow() };
        }

        if (input.Trigger.Kind != TriggerKind.UserTurn)
        {
            RecordInitiativeEvaluation();
            RememberPostResponseIdleDelay(speakable.NextWaitMs);
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
        _envelope = null;
        _publishedDisplayLength = 0;
        _publishedBlockIds.Clear();
        ResetSpeechOutput();
        _responseTerminal = false;
        _responseLifecycle = ResponseLifecycle.Live;
        _outputActivity = OutputActivity.AgentGenerating;
        _responseCts = new CancellationTokenSource();
        _lastCheckpoint = _time.GetUtcNow();
        _snapshot = Append(assistant);
        RequestPersist(_snapshot);
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

    private async Task AfterResponseTerminalizedAsync(EventContext context, CancellationToken cancellationToken)
    {
        if (HasPendingUserBatch())
        {
            _allowQueuedSuffixAutoDispatch = true;
        }

        if (await TryStartPendingUserBatchAsync(context, cancellationToken).ConfigureAwait(false))
        {
            _allowQueuedSuffixAutoDispatch = false;
            return;
        }

        if (!HasPendingUserBatch())
        {
            _allowQueuedSuffixAutoDispatch = false;
        }

        await DrainEnvironmentAsync(context, cancellationToken).ConfigureAwait(false);
        SchedulePostResponseIdleTimer();
        await ApplyPendingVoiceIfIdleAsync(context, cancellationToken).ConfigureAwait(false);
        LaunchCompletionEvaluation(context);
    }

    private void LaunchCompletionEvaluation(EventContext cause)
    {
        if (_deactivated
            || _activeResponseId is not null
            || HasPendingUserBatch()
            || !CompletionEvaluator.ShouldEvaluate(_snapshot))
        {
            return;
        }

        var lastAssistant = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
        if (lastAssistant is null || lastAssistant.Status != EntryStatus.Completed)
        {
            return;
        }

        CancelCompletionEvaluation();
        var generation = ++_completionGeneration;
        _completionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _completionCts.Token;
        var snapshot = _snapshot;
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                CompletionDecision decision;
                try
                {
                    decision = await CompletionEvaluator.EvaluateAsync(
                            _languageModel,
                            snapshot,
                            _time.GetUtcNow(),
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    decision = new ContinueSession("Completion evaluation failed.");
                }

                if (generation != _completionGeneration)
                {
                    return;
                }

                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginWork();
                if (!TryMailbox(new CompletionReturned(NewContext(cause.EventId), generation, decision, processed)))
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

    private async Task HandleCompletionReturnedAsync(CompletionReturned input, CancellationToken cancellationToken)
    {
        if (input.Generation != _completionGeneration
            || _deactivated
            || _activeResponseId is not null
            || HasPendingUserBatch()
            || LifecycleTransition.IsTerminal(_snapshot)
            || !CompletionEvaluator.ShouldEvaluate(_snapshot))
        {
            return;
        }

        if (input.Decision is not RequestComplete complete)
        {
            return;
        }

        var policy = _snapshot.CompletionPolicy ?? SessionCompletionPolicy.Default;
        if (policy.AgentCompletion == AgentCompletionAuthority.Advisory)
        {
            _snapshot = _snapshot with
            {
                LifecycleReason = complete.Reason,
                LifecycleSource = LifecycleTransitionSource.Agent,
                LifecycleChangedAt = _time.GetUtcNow(),
                UpdatedAt = _time.GetUtcNow()
            };
            RequestPersist(
                _snapshot,
                then: async ct =>
                {
                    await PublishAsync(
                            new SessionOutput(input.Context, null, new CompletionIntentOutput(complete.Reason, Advisory: true)),
                            ct)
                        .ConfigureAwait(false);
                    await PublishStateAsync(input.Context, ct).ConfigureAwait(false);
                });
            return;
        }

        if (policy.AgentCompletion != AgentCompletionAuthority.Allowed)
        {
            return;
        }

        await TerminalizeAsync(
                input.Context,
                SessionLifecycleStatus.Completed,
                LifecycleTransitionSource.Agent,
                complete.Reason,
                persisted: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void CancelCompletionEvaluation()
    {
        _completionGeneration++;
        var cts = _completionCts;
        if (cts is null)
        {
            return;
        }

        _completionCts = null;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private bool HasPendingUserBatch() => TrailingUserSuffix.HasPending(_snapshot.Entries);

    private async Task<bool> TryStartPendingUserBatchAsync(EventContext cause, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_deactivated
            || _activeResponseId is not null
            || _snapshot.Status is not (SessionStatus.Attached or SessionStatus.Created))
        {
            return false;
        }

        var suffix = TrailingUserSuffix.Of(_snapshot.Entries);
        if (suffix.Count == 0 || suffix.Any(entry => _undurableUserEntryIds.Contains(entry.EntryId)))
        {
            return false;
        }

        var last = suffix[^1];
        var eventId = last.SourceEventId ?? last.EntryId;
        var batchCause = new EventContext(
            eventId,
            SessionId,
            _epoch,
            _time.GetUtcNow(),
            cause.CorrelationId == Guid.Empty ? eventId : cause.CorrelationId,
            cause.EventId);
        var attachmentIds = suffix
            .SelectMany(entry => entry.Attachments ?? [])
            .Select(item => item.AttachmentId)
            .Distinct()
            .ToArray();
        var trigger = new AgentTrigger(eventId, TriggerKind.UserTurn, last.Text);
        var turn = ++_turnGeneration;
        _pendingUploadHold = false;
        NoteUserActivity();
        _environmentQueue.Clear();
        _outputActivity = OutputActivity.WaitingForAgent;
        UserTextQueueTelemetry.RecordPendingBatchStarted(suffix.Count);
        LaunchPreparedTurn(batchCause, trigger, _ids.NewId(), turn, attachmentIds);
        await PublishWaitingOutputAsync(batchCause).ConfigureAwait(false);
        return true;
    }

    private void LaunchPreparedTurn(
        EventContext cause,
        AgentTrigger trigger,
        Guid responseId,
        int turn,
        IReadOnlyList<Guid> attachmentIds)
    {
        if (_processor is null || attachmentIds.Count == 0)
        {
            LaunchBrain(cause, trigger, responseId, turn);
            return;
        }

        _outputActivity = OutputActivity.ProcessingAttachments;
        var inbound = NewContext(cause.EventId);
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishStateAsync(inbound, CancellationToken.None).ConfigureAwait(false);
                IReadOnlyList<AttachmentProcessResult> results;
                var extractionStarted = Stopwatch.GetTimestamp();
                try
                {
                    results = await _processor.ProcessTurnAsync(SessionId, attachmentIds, _lifetime.Token)
                        .ConfigureAwait(false);
                    RuntimeTelemetry.Record("extraction", RuntimeTelemetry.ElapsedMs(extractionStarted));
                }
                catch (OperationCanceledException)
                {
                    RuntimeTelemetry.Record("extraction", RuntimeTelemetry.ElapsedMs(extractionStarted));
                    if (!TryMailbox(new AttachmentsProcessedReceived(inbound, turn, responseId, trigger, [])))
                    {
                        EndWork();
                    }

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Attachment processing failed for {SessionId}", SessionId);
                    results = [];
                }

                if (!TryMailbox(new AttachmentsProcessedReceived(inbound, turn, responseId, trigger, results)))
                {
                    EndWork();
                }
            }
            catch
            {
                EndWork();
                throw;
            }
        }, CancellationToken.None);
    }

    private async Task HandleAttachmentsProcessedAsync(AttachmentsProcessedReceived input, CancellationToken cancellationToken)
    {
        if (_deactivated || input.TurnGeneration != _turnGeneration)
        {
            if (!_deactivated
                && _activeResponseId is null
                && _outputActivity == OutputActivity.ProcessingAttachments)
            {
                await PublishOutputIdleAsync(input.Context, cancellationToken).ConfigureAwait(false);
            }

            EndWork();
            return;
        }

        _outputActivity = OutputActivity.WaitingForAgent;
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
        LaunchBrain(input.Context, input.Trigger, input.ResponseId, input.TurnGeneration, input.Results, workHeld: true);
        if (_outputActivity == OutputActivity.Idle)
        {
            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LaunchBrain(
        EventContext cause,
        AgentTrigger trigger,
        Guid responseId,
        int turn,
        IReadOnlyList<AttachmentProcessResult>? attachments = null,
        bool workHeld = false)
    {
        var proactive = trigger.Kind != TriggerKind.UserTurn;
        if (_deactivated || (proactive && _activeResponseId is not null))
        {
            _outputActivity = OutputActivity.Idle;
            return;
        }

        if (!workHeld)
        {
            BeginWork();
        }

        CancelBrainEvaluation();
        if (proactive)
        {
            _proactiveBrainInFlight = true;
        }

        _brainEvaluationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var evaluationToken = _brainEvaluationCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var sessionAttachments = await BuildSessionAttachmentManifestAsync(evaluationToken).ConfigureAwait(false);
                var context = new AgentContext(
                    _snapshot.Definition,
                    _snapshot.Entries,
                    _snapshot.Summary,
                    Profile: _profile,
                    _snapshot.Mode,
                    _snapshot.PendingTopic,
                    HelpOfferedDuringSilence: _helpOfferedDuringSilence,
                    InterruptedHeardText: LastInterruptedHeardText(),
                    trigger,
                    attachments,
                    sessionAttachments,
                    ConsecutiveProactiveSpeaks: _consecutiveProactiveSpeaks,
                    SilentEvaluations: _silentEvaluations,
                    SpeaksThisSilencePeriod: _proactiveSpeaksThisSilence,
                    InitiativeHeld: _initiativeHeld || _pendingUploadHold,
                    InactivityExceeded: InactivityExceeded(),
                    ModelSupportsTools: _languageModel.Capabilities.Tools,
                    UtcNow: _time.GetUtcNow(),
                    LastUserActivityAt: _snapshot.LastUserActivityAt);
                var brainStarted = Stopwatch.GetTimestamp();
                using var activity = RuntimeTelemetry.Activity.StartActivity("brain");
                AgentDecision? decision = null;
                BrainFailed? failure = null;
                try
                {
                    decision = await _brain.DecideAsync(context, responseId, evaluationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (evaluationToken.IsCancellationRequested)
                {
                    if (trigger.Kind == TriggerKind.UserTurn)
                    {
                        failure = new BrainFailed(
                            NewContext(cause.EventId),
                            turn,
                            responseId,
                            trigger,
                            Recoverable: false,
                            Message: null,
                            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    }
                    else
                    {
                        decision = new StaySilent(
                            "Initiative evaluation superseded.",
                            CountsTowardSilentCap: false);
                    }
                }
                catch (Exception) when (trigger.Kind != TriggerKind.UserTurn)
                {
                    decision = new StaySilent(
                        "Initiative evaluation failed.",
                        CountsTowardSilentCap: false);
                }
                catch (Exception ex)
                {
                    failure = new BrainFailed(
                        NewContext(cause.EventId),
                        turn,
                        responseId,
                        trigger,
                        Recoverable: true,
                        Message: ex.Message,
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                RuntimeTelemetry.Record("brain", RuntimeTelemetry.ElapsedMs(brainStarted));
                var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var inbound = NewContext(cause.EventId);
                BeginWork();
                if (failure is not null)
                {
                    var failed = new BrainFailed(
                        failure.Context,
                        failure.TurnGeneration,
                        failure.ResponseId,
                        failure.Trigger,
                        failure.Recoverable,
                        failure.Message,
                        processed);
                    if (!TryMailbox(failed))
                    {
                        EndWork();
                        return;
                    }

                    await processed.Task.ConfigureAwait(false);
                    return;
                }

                if (decision is null)
                {
                    EndWork();
                    return;
                }

                if (!TryMailbox(new BrainReturned(inbound, turn, responseId, trigger, decision, processed)))
                {
                    EndWork();
                    return;
                }

                await processed.Task.ConfigureAwait(false);
            }
            finally
            {
                if (_brainEvaluationCts?.Token == evaluationToken)
                {
                    _brainEvaluationCts.Dispose();
                    _brainEvaluationCts = null;
                }

                EndWork();
            }
        }, CancellationToken.None);
    }

    private async Task PumpModelAsync(ModelRequest request, EventContext cause, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = RuntimeTelemetry.Activity.StartActivity("model");
        var messages = request.Messages.ToList();
        var steps = 0;
        var outputBytes = 0;
        var toolDeadline = request.Tools is { Count: > 0 };
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ITimer? overallTimer = toolDeadline ? ScheduleCancel(_time, overallCts, ToolLimits.Overall) : null;
        var generateToken = toolDeadline ? overallCts.Token : cancellationToken;
        try
        {
            while (!generateToken.IsCancellationRequested)
            {
                var pending = new List<ModelToolCall>();
                var finished = false;
                var working = request with { Messages = messages };
                await foreach (var evt in _languageModel.GenerateAsync(working, generateToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case ModelToolCallEvent tool:
                            pending.Add(tool.Call);
                            continue;
                        case ModelCompleted completed when completed.Reason == ModelStopReason.ToolCalls:
                            continue;
                        default:
                            if (!_recordedLlm && evt is ModelTextDelta)
                            {
                                _recordedLlm = true;
                                RuntimeTelemetry.Record("llm", RuntimeTelemetry.ElapsedMs(started));
                            }

                            if (!await MailboxModelAsync(cause, request.ResponseId, evt, generateToken)
                                    .ConfigureAwait(false))
                            {
                                return;
                            }

                            if (evt is ModelCompleted or ModelFailed)
                            {
                                finished = true;
                            }

                            break;
                    }
                }

                if (finished || pending.Count == 0)
                {
                    return;
                }

                if (steps + pending.Count > ToolLimits.MaxSteps)
                {
                    await MailboxModelAsync(
                            cause,
                            request.ResponseId,
                            new ModelFailed(new ProviderFailure(
                                ProviderErrorCode.InvalidRequest,
                                "Tool step limit reached.")),
                            overallCts.Token)
                        .ConfigureAwait(false);
                    return;
                }

                if (!await AdmitToolActivityAsync(
                            cause,
                            request.ResponseId,
                            OutputActivity.RunningTools,
                            hold: true,
                            overallCts.Token)
                        .ConfigureAwait(false))
                {
                    return;
                }

                messages.Add(new ModelMessage(ModelRole.Assistant, string.Empty, ToolCalls: pending));
                foreach (var call in pending)
                {
                    using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                    using var toolTimer = ScheduleCancel(_time, toolCts, ToolLimits.PerTool);
                    if (!await AdmitToolActivityAsync(
                                cause,
                                request.ResponseId,
                                OutputActivity.RunningTools,
                                hold: true,
                                toolCts.Token)
                            .ConfigureAwait(false))
                    {
                        return;
                    }

                    string result;
                    var toolStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        result = await _tools.ExecuteAsync(
                                _snapshot.Definition,
                                SessionId,
                                call,
                                ToolLimits.MaxOutputBytes - outputBytes,
                                toolCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        RuntimeTelemetry.Record("tools", RuntimeTelemetry.ElapsedMs(toolStarted), call.Name);
                        RuntimeTelemetry.RecordDropped("tools");
                        await MailboxModelAsync(
                                cause,
                                request.ResponseId,
                                new ModelFailed(new ProviderFailure(
                                    ProviderErrorCode.Cancelled,
                                    "Tool execution cancelled.")),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }

                    RuntimeTelemetry.Record("tools", RuntimeTelemetry.ElapsedMs(toolStarted), call.Name);

                    outputBytes += Encoding.UTF8.GetByteCount(result);
                    if (outputBytes > ToolLimits.MaxOutputBytes)
                    {
                        await MailboxModelAsync(
                                cause,
                                request.ResponseId,
                                new ModelFailed(new ProviderFailure(
                                    ProviderErrorCode.InvalidRequest,
                                    "Tool output limit reached.")),
                                overallCts.Token)
                            .ConfigureAwait(false);
                        return;
                    }

                    messages.Add(new ModelMessage(ModelRole.Tool, result, ToolCallId: call.Id, Name: call.Name));
                    steps++;
                }

                if (!await AdmitToolActivityAsync(
                            cause,
                            request.ResponseId,
                            OutputActivity.AgentGenerating,
                            hold: true,
                            overallCts.Token)
                        .ConfigureAwait(false))
                {
                    return;
                }
            }

            if (toolDeadline)
            {
                await MailboxModelAsync(
                        cause,
                        request.ResponseId,
                        new ModelFailed(new ProviderFailure(ProviderErrorCode.Timeout, "Tool deadline reached.")),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await MailboxModelAsync(
                    cause,
                    request.ResponseId,
                    new ModelFailed(new ProviderFailure(ProviderErrorCode.Cancelled, "Generation cancelled.")),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Language model pump failed for {ResponseId}", request.ResponseId);
            await MailboxModelAsync(
                    cause,
                    request.ResponseId,
                    new ModelFailed(new ProviderFailure(ProviderErrorCode.Unknown, "Generation failed.")),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> MailboxModelAsync(
        EventContext cause,
        Guid responseId,
        ModelGenerationEvent evt,
        CancellationToken cancellationToken)
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        var admitted = TryMailbox(new ModelResultReceived(NewContext(cause.EventId), responseId, evt, processed));
        if (!admitted)
        {
            EndWork();
            processed.TrySetResult();
            return false;
        }

        try
        {
            await processed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    private async Task<bool> AdmitToolActivityAsync(
        EventContext cause,
        Guid responseId,
        OutputActivity activity,
        bool hold,
        CancellationToken cancellationToken)
    {
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new ToolActivityReceived(NewContext(cause.EventId), activity, hold, responseId, _epoch, admitted)))
        {
            EndWork();
            return false;
        }

        try
        {
            return await admitted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task HandleToolActivityAsync(ToolActivityReceived input, CancellationToken cancellationToken)
    {
        if (_deactivated
            || _responseTerminal
            || _activeResponseId != input.ResponseId
            || _epoch != input.Epoch)
        {
            input.Admitted.TrySetResult(false);
            return;
        }

        _outputActivity = input.Activity;
        _initiativeHeld = input.Hold;
        _lastMeaningfulActivityAt = _time.GetUtcNow();
        input.Admitted.TrySetResult(true);
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
    }

    private static ITimer ScheduleCancel(TimeProvider time, CancellationTokenSource cts, TimeSpan delay) =>
        time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), cts, delay, Timeout.InfiniteTimeSpan);

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
                _accumulator.Append(delta.Text);
                await PublishEnvelopeProgressAsync(input.Context, input.ResponseId, finalize: false, cancellationToken)
                    .ConfigureAwait(false);
                UpdateStreamingAssistant();
                await CheckpointStreamingAsync(cancellationToken).ConfigureAwait(false);
                if (UsesSpeechSegmentation)
                {
                    if (_segmentPipelineStarted == 0)
                    {
                        _segmentPipelineStarted = Stopwatch.GetTimestamp();
                    }

                    FeedTtsFromLockedSource();
                    ScheduleSegmentTimer();
                    KickTts(input.Context);
                    await ReleaseClientSpeechAsync(input.Context, cancellationToken).ConfigureAwait(false);
                    if (_pendingSegments.Count >= 4)
                    {
                        _modelBackpressure = input.Processed;
                        return;
                    }
                }

                break;
            case ModelToolCallEvent:
                break;
            case ModelCompleted { Reason: ModelStopReason.ToolCalls }:
                break;
            case ModelCompleted completed:
                _modelFinishReason = completed.Reason switch
                {
                    ModelStopReason.LengthLimit => "lengthLimit",
                    ModelStopReason.ContentFiltered => "contentFiltered",
                    _ => null
                };
                await PublishEnvelopeProgressAsync(input.Context, input.ResponseId, finalize: true, cancellationToken)
                    .ConfigureAwait(false);
                UpdateStreamingAssistant();
                if (UsesSpeechSegmentation)
                {
                    _modelDone = true;
                    if (_segmentPipelineStarted == 0)
                    {
                        _segmentPipelineStarted = Stopwatch.GetTimestamp();
                    }

                    FeedTtsFromLockedSource();
                    EnqueueSegments(_segmenter!.Complete());
                    KickTts(input.Context);
                    await ReleaseClientSpeechAsync(input.Context, cancellationToken).ConfigureAwait(false);
                    await TryCompleteVoiceAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
                    await TryCompleteClientSpeechAsync(input.Context, failed: false, cancellationToken).ConfigureAwait(false);
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
        ResponseCancelTelemetry.RecordRequested();
        try
        {
            if (_activeResponseId == input.ResponseId)
            {
                await SupersedeAsync(input.Context, input.ResponseId, cancellationToken, "userStop").ConfigureAwait(false);
                input.Completed?.TrySetResult(ResponseCancelResult.Cancelled);
                return;
            }

            var known = _snapshot.Entries.Any(entry => entry.ResponseId == input.ResponseId);
            if (!known)
            {
                input.Completed?.TrySetResult(ResponseCancelResult.Unknown);
                return;
            }

            if (_activeResponseId is not null)
            {
                ResponseCancelTelemetry.RecordStale();
                input.Completed?.TrySetResult(ResponseCancelResult.Stale);
                return;
            }

            input.Completed?.TrySetResult(ResponseCancelResult.Idempotent);
        }
        catch
        {
            input.Completed?.TrySetResult(ResponseCancelResult.Unknown);
            throw;
        }
    }

    private async Task SupersedeAsync(
        EventContext context,
        Guid responseId,
        CancellationToken cancellationToken,
        string reason = "newText")
    {
        if (reason is "userStop")
        {
            _allowQueuedSuffixAutoDispatch = false;
        }

        await TerminalizeActiveResponseAsync(context, responseId, cancellationToken, reason, requestPersist: true)
            .ConfigureAwait(false);
        if (reason is "userBargeIn")
        {
            await AfterResponseTerminalizedAsync(context, cancellationToken).ConfigureAwait(false);
        }
        else if (reason is "newText")
        {
            await ApplyPendingVoiceIfIdleAsync(context, cancellationToken).ConfigureAwait(false);
        }
        else if (reason is "userStop")
        {
            await PublishOutputIdleAsync(context, cancellationToken).ConfigureAwait(false);
            SchedulePostResponseIdleTimer();
            await ApplyPendingVoiceIfIdleAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TerminalizeActiveResponseAsync(
        EventContext context,
        Guid responseId,
        CancellationToken cancellationToken,
        string reason,
        bool requestPersist)
    {
        _responseCts?.Cancel();
        _ttsCts?.Cancel();

        _responseLifecycle = ResponseLifecycle.Superseded;
        _outputActivity = OutputActivity.Interrupted;
        _initiativeHeld = false;
        SpeechTelemetry.RecordCancel(reason);
        var heard = UsesClientSpeech ? _ackedPlaybackText : _spokenUntil.Credit(_ackedSamples);
        ApplyHeard(heard);
        InvalidateSpeechJobs();
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!_responseTerminal)
        {
            _responseTerminal = true;
            UpdateAssistant(EntryStatus.Interrupted);
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
                        new ResponseCompletedOutput(
                            true,
                            HeardTextEndExclusive: heard,
                            InterruptReason: reason)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (requestPersist && _snapshot.Status is SessionStatus.Attached or SessionStatus.Created)
            {
                RequestPersist(_snapshot);
            }
        }

        ClearActive();
        _turnGeneration++;
        ClearPendingPostResponseIdleDelay();
    }

    private async Task CompleteAsync(EventContext context, Guid responseId, bool failed, CancellationToken cancellationToken)
    {
        _responseTerminal = true;
        _responseLifecycle = failed ? ResponseLifecycle.Failed : ResponseLifecycle.Completed;
        _initiativeHeld = false;
        var status = failed ? EntryStatus.Failed : EntryStatus.Completed;
        UpdateAssistant(status);
        await PublishOutputIdleAsync(context, cancellationToken).ConfigureAwait(false);
        var capturedResponseId = responseId;
        var capturedEntryId = _activeEntryId;
        var textLength = DisplayLength();
        var heard = CurrentHeard();
        RequestPersist(
            _snapshot,
            then: async ct =>
            {
                if (_activeResponseId != capturedResponseId || _activeEntryId != capturedEntryId)
                {
                    return;
                }

                if (!failed)
                {
                    await PublishAsync(
                            new SessionOutput(context, capturedResponseId, new TextCompletedOutput(textLength)),
                            ct)
                        .ConfigureAwait(false);
                }

                await PublishAsync(
                        new SessionOutput(
                            context,
                            capturedResponseId,
                            new ResponseCompletedOutput(
                                failed,
                                HeardTextEndExclusive: heard,
                                FinishReason: failed ? null : _modelFinishReason)),
                        ct)
                    .ConfigureAwait(false);
                ClearActive();
                await AfterResponseTerminalizedAsync(context, ct).ConfigureAwait(false);
            });
    }

    private async Task DeclineInitiativeAsync(
        EventContext context,
        AgentDecision decision,
        CancellationToken cancellationToken)
    {
        RecordInitiativeEvaluation();
        await PublishOutputIdleAsync(context, cancellationToken).ConfigureAwait(false);
        ScheduleIdleTimer(ResolveInitiativeDelay(decision));
    }

    private TimeSpan ResolveInitiativeDelay(AgentDecision decision)
    {
        if (decision is StaySilent silent && silent.NextWaitMs is int waitMs)
        {
            return RecordClampedWait(TimeSpan.FromMilliseconds(waitMs), "model");
        }

        if (decision is Speak speak && speak.NextWaitMs is int speakWaitMs)
        {
            return RecordClampedWait(TimeSpan.FromMilliseconds(speakWaitMs), "model");
        }

        var fallback = IdleBackoff();
        InitiativeWaitTelemetry.Record(fallback, "fallback", "n/a", _snapshot.Mode);
        return fallback;
    }

    private void RememberPostResponseIdleDelay(int? nextWaitMs)
    {
        if (nextWaitMs is not int waitMs)
        {
            ClearPendingPostResponseIdleDelay();
            return;
        }

        var (applied, clamp) = ClampInitiativeWaitWithReason(TimeSpan.FromMilliseconds(waitMs));
        _pendingPostResponseIdleDelay = applied;
        _pendingPostResponseIdleClamp = clamp;
    }

    private void SchedulePostResponseIdleTimer()
    {
        if (_pendingPostResponseIdleDelay is { } delay)
        {
            _pendingPostResponseIdleDelay = null;
            InitiativeWaitTelemetry.Record(delay, "model", _pendingPostResponseIdleClamp, _snapshot.Mode);
            _pendingPostResponseIdleClamp = "n/a";
            ScheduleIdleTimer(delay);
            return;
        }

        ScheduleIdleTimer(SilenceThreshold());
    }

    private void ClearPendingPostResponseIdleDelay()
    {
        _pendingPostResponseIdleDelay = null;
        _pendingPostResponseIdleClamp = "n/a";
    }

    private TimeSpan RecordClampedWait(TimeSpan requested, string source)
    {
        var (applied, clamp) = ClampInitiativeWaitWithReason(requested);
        InitiativeWaitTelemetry.Record(applied, source, clamp, _snapshot.Mode);
        return applied;
    }

    private TimeSpan ClampInitiativeWait(TimeSpan requested) =>
        ClampInitiativeWaitWithReason(requested).Applied;

    private (TimeSpan Applied, string Clamp) ClampInitiativeWaitWithReason(TimeSpan requested)
    {
        var policy = _snapshot.Definition.InitiativePolicy;
        var min = _snapshot.Mode == SessionMode.Text
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(5);
        var max = TimeSpan.FromMilliseconds(Math.Max(policy.InactivityLimitMs / 2, policy.CooldownMs * 4));
        if (requested < min)
        {
            return (min, "min");
        }

        if (requested > max)
        {
            return (max, "max");
        }

        return (requested, "none");
    }

    private static bool HasCompletedAssistantTurn(SessionSnapshot snapshot) =>
        snapshot.Entries.Any(entry =>
            entry.Role == ConversationRole.Assistant && entry.Status != EntryStatus.Streaming);

    private async Task PublishWaitingOutputAsync(EventContext context)
    {
        if (_outputActivity != OutputActivity.WaitingForAgent)
        {
            return;
        }

        await PublishStateAsync(context, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PublishOutputIdleAsync(EventContext context, CancellationToken cancellationToken)
    {
        _outputActivity = OutputActivity.Idle;
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
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
                        Text = DisplayText(),
                        Status = status,
                        Envelope = CurrentEnvelope(status != EntryStatus.Streaming),
                        FinishReason = status == EntryStatus.Completed ? _modelFinishReason : null
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

    private async Task HandleReceiptAsync(ResponseReceiptReceived input, CancellationToken cancellationToken)
    {
        var entry = _snapshot.Entries.FirstOrDefault(item => item.ResponseId == input.ResponseId);
        if (entry is null
            || _snapshot.Status is SessionStatus.Paused or SessionStatus.Ending or SessionStatus.Ended)
        {
            input.Admitted.TrySetResult(false);
            return;
        }

        if (entry.Status is EntryStatus.Interrupted or EntryStatus.Failed)
        {
            input.Admitted.TrySetResult(true);
            return;
        }

        var generated = DisplayLength(entry);
        if (input.TextEndExclusive > generated || input.TextEndExclusive < entry.ReceivedTextEndExclusive)
        {
            input.Admitted.TrySetResult(false);
            return;
        }

        var blockIds = input.BlockIds ?? [];
        if (blockIds.Count > 0)
        {
            var known = entry.Envelope?.Blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal)
                ?? [];
            if (blockIds.Any(id => !known.Contains(id)))
            {
                input.Admitted.TrySetResult(false);
                return;
            }
        }

        if (input.TextEndExclusive == entry.ReceivedTextEndExclusive && blockIds.Count == 0)
        {
            input.Admitted.TrySetResult(true);
            return;
        }

        var received = input.TextEndExclusive;
        var envelope = MarkBlocksDelivered(entry.Envelope, blockIds);
        var entries = _snapshot.Entries.Select(item =>
                item.ResponseId == input.ResponseId
                    ? item with { ReceivedTextEndExclusive = received, Envelope = envelope }
                    : item)
            .ToArray();
        _snapshot = _snapshot with { Entries = entries, UpdatedAt = _time.GetUtcNow() };
        RequestPersist(_snapshot);
        input.Admitted.TrySetResult(true);
        _ = cancellationToken;
    }

    private async Task PublishEnvelopeProgressAsync(
        EventContext context,
        Guid responseId,
        bool finalize,
        CancellationToken cancellationToken)
    {
        var parsed = ResponseEnvelopeParser.Parse(
            _accumulator.Text,
            id => _artifacts.IsAuthorized(_snapshot.SessionId, id),
            finalize);
        _envelope = _ttsSourceLocked && _ttsUsesSpeech && !string.IsNullOrEmpty(_envelope?.SpeechText)
            ? parsed with { SpeechText = _envelope.SpeechText }
            : parsed;
        if (finalize && string.IsNullOrEmpty(_envelope.SpeechText))
        {
            var spoken = SpokenOutput.ForPlayback(null, _envelope.DisplayText);
            if (SpokenOutput.ShouldPersistDerivedSpeechText(spoken, _envelope.DisplayText))
            {
                _envelope = _envelope with { SpeechText = spoken };
            }
        }
        var display = _envelope.DisplayText;
        if (display.Length > _publishedDisplayLength)
        {
            var chunk = display[_publishedDisplayLength..];
            await PublishAsync(
                    new SessionOutput(context, responseId, new TextDeltaOutput(_publishedDisplayLength, chunk)),
                    cancellationToken)
                .ConfigureAwait(false);
            _publishedDisplayLength = display.Length;
        }

        foreach (var block in _envelope.Blocks)
        {
            if (!_publishedBlockIds.Add(block.BlockId))
            {
                continue;
            }

            await PublishAsync(
                    new SessionOutput(
                        context,
                        responseId,
                        new BlockUpsertOutput(
                            block.BlockId,
                            BlockKindName(block.Kind),
                            string.IsNullOrEmpty(block.DisplayText) ? block.FallbackText : block.DisplayText,
                            block.FallbackText,
                            block.AttachmentId,
                            block.ArtifactId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string CurrentTtsSource()
    {
        var parsed = _envelope
            ?? ResponseEnvelopeParser.Parse(_accumulator.Text, id => _artifacts.IsAuthorized(_snapshot.SessionId, id), finalize: false);
        if (!_ttsSourceLocked)
        {
            if (!string.IsNullOrEmpty(parsed.SpeechText))
            {
                var explicitPlayback = SpokenOutput.ForPlayback(parsed.SpeechText, parsed.DisplayText);
                if (explicitPlayback.Length == 0)
                {
                    return string.Empty;
                }

                _ttsUsesSpeech = true;
                _ttsSourceLocked = true;
                _envelope = parsed with { SpeechText = explicitPlayback };
                return explicitPlayback;
            }

            if (ShouldWaitForExplicitSpeech(parsed))
            {
                return string.Empty;
            }

            var fallback = SpokenOutput.ForPlayback(null, parsed.DisplayText);
            if (fallback.Length == 0)
            {
                return string.Empty;
            }

            _ttsUsesSpeech = false;
            _ttsSourceLocked = true;
            return fallback;
        }

        if (_ttsUsesSpeech)
        {
            return _envelope?.SpeechText
                ?? SpokenOutput.ForPlayback(parsed.SpeechText, parsed.DisplayText);
        }

        return SpokenOutput.ForPlayback(null, parsed.DisplayText);
    }

    private bool ShouldWaitForExplicitSpeech(ResponseEnvelope parsed)
    {
        if (!string.IsNullOrEmpty(parsed.SpeechText))
        {
            return false;
        }

        if (!_modelDone)
        {
            if (_accumulator.Text.Contains("[[speech:", StringComparison.Ordinal)
                || _accumulator.Text.Contains("[[", StringComparison.Ordinal)
                || SpokenOutput.LooksLikeStructuredDisplay(parsed.DisplayText))
            {
                return true;
            }
        }

        return false;
    }

    private void FeedTtsFromLockedSource()
    {
        if (_segmenter is null)
        {
            return;
        }

        var source = CurrentTtsSource();
        if (source.Length > _ttsFedLength)
        {
            EnqueueSegments(_segmenter.Append(source[_ttsFedLength..], _time.GetUtcNow()));
            _ttsFedLength = source.Length;
        }
    }

    private string DisplayText() => _envelope?.DisplayText ?? _accumulator.Text;

    private int DisplayLength() => DisplayText().Length;

    private int DisplayLength(ConversationEntry entry) =>
        entry.EntryId == _activeEntryId ? DisplayLength() : entry.Text.Length;

    private int SpeechCoordinateLength()
    {
        var source = CurrentTtsSource();
        return source.Length == 0 ? DisplayLength() : source.Length;
    }

    private ResponseEnvelope CurrentEnvelope(bool finalize)
    {
        var parsed = _envelope
            ?? ResponseEnvelopeParser.Parse(_accumulator.Text, id => _artifacts.IsAuthorized(_snapshot.SessionId, id), finalize);
        var existing = _snapshot.Entries.FirstOrDefault(item => item.EntryId == _activeEntryId)?.Envelope;
        return ResponseEnvelopeParser.MergeDelivery(existing, parsed);
    }

    private static ResponseEnvelope? MarkBlocksDelivered(
        ResponseEnvelope? envelope,
        IReadOnlyList<string> blockIds)
    {
        if (envelope is null || blockIds.Count == 0)
        {
            return envelope;
        }

        var selected = blockIds.ToHashSet(StringComparer.Ordinal);
        return envelope with
        {
            Blocks = envelope.Blocks
                .Select(block => selected.Contains(block.BlockId) ? block with { DisplayDelivered = true } : block)
                .ToArray()
        };
    }

    private static string BlockKindName(ResponseBlockKind kind) => kind switch
    {
        ResponseBlockKind.Markdown => "markdown",
        ResponseBlockKind.AttachmentReference => "attachment",
        ResponseBlockKind.ArtifactReference => "artifact",
        _ => "unknown"
    };

    private void ClearActive()
    {
        _activeResponseId = null;
        _activeEntryId = null;
        _modelFinishReason = null;
        _responseCts?.Dispose();
        _responseCts = null;
        _ttsCts?.Dispose();
        _ttsCts = null;
    }

    private static string ToStopReason(string reason) => reason switch
    {
        "disconnected" => "disconnected",
        "ended" => "ended",
        "deactivated" => "interrupted",
        "modeChange" => "modeChange",
        _ => "interrupted"
    };

    private async Task HandleEndAsync(EndSessionReceived input, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _terminalFence);
        await TerminalizeAsync(
                input.Context,
                SessionLifecycleStatus.Ended,
                LifecycleTransitionSource.Legacy,
                "ended",
                input.Persisted,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleLifecycleTransitionAsync(
        LifecycleTransitionReceived input,
        CancellationToken cancellationToken)
    {
        switch (input.Target)
        {
            case SessionLifecycleStatus.Paused:
                await ApplyDeactivateAsync(
                        input.Context,
                        cancellationToken,
                        input.Persisted,
                        input.Reason ?? "manual",
                        input.Source)
                    .ConfigureAwait(false);
                return;
            case SessionLifecycleStatus.Active:
                await ApplyResumeAsync(
                        input.Context,
                        input.Source,
                        input.Reason,
                        input.Persisted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            case SessionLifecycleStatus.Completed:
            case SessionLifecycleStatus.Expired:
            case SessionLifecycleStatus.Cancelled:
            case SessionLifecycleStatus.Ended:
                await TerminalizeAsync(
                        input.Context,
                        input.Target,
                        input.Source,
                        input.Reason,
                        input.Persisted,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                input.Persisted?.TrySetException(
                    AgentCoreErrors.Validation("Lifecycle transition is not allowed."));
                return;
        }
    }

    private async Task ApplyResumeAsync(
        EventContext context,
        LifecycleTransitionSource source,
        string? reason,
        TaskCompletionSource<bool>? persisted,
        CancellationToken cancellationToken)
    {
        if (SessionLifecycle.IsTerminal(_snapshot.LifecycleStatus))
        {
            persisted?.TrySetException(
                AgentCoreErrors.Validation($"Session is already {LifecycleTransition.ToWire(_snapshot.LifecycleStatus)}."));
            return;
        }

        if (_snapshot.LifecycleStatus == SessionLifecycleStatus.Active
            && !_deactivated
            && _snapshot.Status is SessionStatus.Attached or SessionStatus.Created)
        {
            persisted?.TrySetResult(true);
            return;
        }

        SessionSnapshot applied;
        try
        {
            applied = LifecycleTransition.Apply(
                _snapshot,
                SessionLifecycleStatus.Active,
                source,
                _time.GetUtcNow(),
                reason);
        }
        catch (AgentCoreException ex)
        {
            persisted?.TrySetException(ex);
            return;
        }

        applied = applied with
        {
            Status = SessionStatus.Attached,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        ApplyProposedResumeState(applied);
        RequestPersist(
            _snapshot,
            then: async ct =>
            {
                await ReactivateLiveSessionAsync(context, ct).ConfigureAwait(false);
                await PublishStateAsync(context, ct).ConfigureAwait(false);
                persisted?.TrySetResult(true);
            },
            ended: persisted);
    }

    private async Task ReactivateLiveSessionAsync(EventContext context, CancellationToken cancellationToken)
    {
        if (_snapshot.Mode == SessionMode.Voice
            && _snapshot.Status is SessionStatus.Attached or SessionStatus.Created
            && !_deactivated)
        {
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
                await FailVoiceAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (CanEvaluateIdle())
        {
            ScheduleIdleTimer(SilenceThreshold());
        }
    }

    private async Task TerminalizeAsync(
        EventContext context,
        SessionLifecycleStatus target,
        LifecycleTransitionSource source,
        string? reason,
        TaskCompletionSource<bool>? persisted,
        CancellationToken cancellationToken)
    {
        if (LifecycleTransition.IsTerminal(_snapshot) && _snapshot.LifecycleStatus == target)
        {
            persisted?.TrySetResult(true);
            return;
        }

        Interlocked.Increment(ref _terminalFence);
        _deadlineTimerGeneration++;
        CancelCompletionEvaluation();
        _deactivated = true;
        SessionSnapshot applied;
        try
        {
            applied = LifecycleTransition.Apply(_snapshot, target, source, _time.GetUtcNow(), reason);
        }
        catch (AgentCoreException ex)
        {
            persisted?.TrySetException(ex);
            return;
        }

        _snapshot = applied with
        {
            Status = SessionStatus.Ending,
            PendingMode = null,
            UpdatedAt = _time.GetUtcNow()
        };
        AbandonLiveSpeech(rotateEpoch: true);
        _input = InputActivity.Idle;
        await StopRecognitionAsync(null, assignStreamId: true).ConfigureAwait(false);
        InvalidateSpeechJobs();
        _ttsCts?.Cancel();
        CancelBrainEvaluation();
        ClearPendingPostResponseIdleDelay();
        if (_activeResponseId is { } live)
        {
            await SupersedeAsync(context, live, cancellationToken, "ended").ConfigureAwait(false);
        }

        Signal(ref _abandonPersist);
        RequestPersist(
            _snapshot with
            {
                Status = SessionStatus.Ended,
                LifecycleStatus = applied.LifecycleStatus,
                PendingMode = null,
                UpdatedAt = _time.GetUtcNow()
            },
            PersistKind.TerminalEnd,
            then: async ct =>
            {
                await PublishStateAsync(context, ct).ConfigureAwait(false);
                persisted?.TrySetResult(true);
            },
            ended: persisted);
    }

    private SessionSnapshot Append(ConversationEntry entry, AttachmentTitleHints? attachments = null)
    {
        var entries = _snapshot.Entries.Concat([entry]).ToArray();
        var title = _snapshot.Title;
        if (entry.Role == ConversationRole.User
            && string.Equals(title, SessionTitles.Default, StringComparison.Ordinal)
            && !_snapshot.Entries.Any(item => item.Role == ConversationRole.User))
        {
            title = string.IsNullOrWhiteSpace(entry.Text) && attachments is { Names.Count: > 0 }
                ? SessionTitles.FromAttachments(attachments.Names, attachments.ImageOnly)
                : SessionTitles.FromUserText(entry.Text);
        }

        return _snapshot with
        {
            Entries = entries,
            Title = title,
            LastEntrySequence = Math.Max(_snapshot.DurableLastEntrySequence, entry.Sequence),
            UpdatedAt = _time.GetUtcNow()
        };
    }

    private static IReadOnlyList<Guid> NormalizeAttachmentIds(IReadOnlyList<Guid>? ids) =>
        ids is null || ids.Count == 0 ? [] : ids;

    private async Task<AttachmentTitleHints?> AttachmentTitleHintsAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0 || _attachments is null)
        {
            return null;
        }

        var names = new List<string>(attachmentIds.Count);
        var imageOnly = true;
        foreach (var id in attachmentIds)
        {
            var record = await _attachments.GetAsync(SessionId, id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            names.Add(record.DisplayName);
            if (!AttachmentMedia.IsImage(record.ContentType))
            {
                imageOnly = false;
            }
        }

        return names.Count == 0 ? null : new AttachmentTitleHints(names, imageOnly);
    }

    private async Task<IReadOnlyList<ConversationAttachmentRef>> BuildAttachmentRefsAsync(
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0 || _attachments is null)
        {
            return [];
        }

        var refs = new List<ConversationAttachmentRef>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            var record = await _attachments.GetAsync(SessionId, id, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                refs.Add(new ConversationAttachmentRef(record.AttachmentId, record.DisplayName, record.ContentType));
            }
        }

        return refs;
    }

    private async Task<IReadOnlyList<SessionAttachmentManifestItem>> BuildSessionAttachmentManifestAsync(
        CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            return [];
        }

        var records = await _attachments.ListForSessionAsync(SessionId, cancellationToken).ConfigureAwait(false);
        var sequenceByEntry = _snapshot.Entries.ToDictionary(entry => entry.EntryId, entry => entry.Sequence);
        var items = new List<SessionAttachmentManifestItem>();
        foreach (var record in records.Where(item => item.State == AttachmentState.Bound))
        {
            long turnSequence = 0;
            if (record.EntryId is { } entryId && sequenceByEntry.TryGetValue(entryId, out var sequence))
            {
                turnSequence = sequence;
            }
            else
            {
                var entry = _snapshot.Entries.FirstOrDefault(row =>
                    row.Attachments?.Any(attachment => attachment.AttachmentId == record.AttachmentId) == true);
                if (entry is not null)
                {
                    turnSequence = entry.Sequence;
                }
            }

            items.Add(new SessionAttachmentManifestItem(
                record.AttachmentId,
                record.DisplayName,
                record.ContentType,
                turnSequence));
        }

        return items
            .OrderBy(item => item.UploadedWithEntrySequence)
            .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record AttachmentTitleHints(IReadOnlyList<string> Names, bool ImageOnly);

    private long NextSequence() => _snapshot.DurableLastEntrySequence + 1;

    private static readonly TimeSpan[] PersistRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private enum PersistKind
    {
        Normal,
        Checkpoint,
        Pause,
        TerminalEnd
    }

    private sealed class PersistJob(
        SessionSnapshot proposed,
        PersistKind kind,
        long token,
        long terminalFence,
        Func<CancellationToken, Task>? then,
        TaskCompletionSource<bool>? ended,
        long started)
    {
        public SessionSnapshot Proposed { get; } = proposed;
        public PersistKind Kind { get; } = kind;
        public long Token { get; } = token;
        public long TerminalFence { get; } = terminalFence;
        public Func<CancellationToken, Task>? Then { get; set; } = then;
        public TaskCompletionSource<bool>? Ended { get; } = ended;
        public long Started { get; } = started;
        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PersistCompletedReceived(
        EventContext Context,
        PersistJob Job,
        SessionSnapshot? Saved,
        Exception? Error,
        bool Superseded) : SessionInput(Context);

    private static void Signal(ref TaskCompletionSource source)
    {
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prior = Interlocked.Exchange(ref source, next);
        prior.TrySetResult();
    }

    private void RequestPersist(
        SessionSnapshot snapshot,
        PersistKind kind = PersistKind.Normal,
        Func<CancellationToken, Task>? then = null,
        TaskCompletionSource<bool>? ended = null)
    {
        if (snapshot.Entries.Count > 1000)
        {
            ended?.TrySetResult(false);
            throw AgentCoreErrors.Validation("Session entry limit of 1000 was reached.");
        }

        var started = Stopwatch.GetTimestamp();
        var token = Interlocked.Increment(ref _persistToken);
        var fence = Volatile.Read(ref _terminalFence);
        var job = new PersistJob(snapshot, kind, token, fence, then, ended, started);
        BeginWork();
        _pendingPersist[token] = job;
        if (!_persistJobs.Writer.TryWrite(job))
        {
            _pendingPersist.TryRemove(token, out _);
            ended?.TrySetResult(false);
            job.Applied.TrySetResult();
            EndWork();
            throw AgentCoreErrors.Persistence("Persistent save failed.");
        }
    }

    private async Task HandlePersistCompletedAsync(PersistCompletedReceived input, CancellationToken cancellationToken)
    {
        var job = input.Job;
        try
        {
            if (!_pendingPersist.TryRemove(job.Token, out _))
            {
                return;
            }

            RuntimeTelemetry.Record("persist", RuntimeTelemetry.ElapsedMs(job.Started));
            if (input.Superseded)
            {
                job.Ended?.TrySetResult(false);
                return;
            }

            if (input.Error is not null)
            {
                if (job.Kind is PersistKind.TerminalEnd)
                {
                    _snapshot = _snapshot with
                    {
                        Status = SessionStatus.Ending,
                        PendingMode = null,
                        UpdatedAt = _time.GetUtcNow()
                    };
                    await PublishAsync(
                            new SessionOutput(
                                NewContext(),
                                null,
                                new ErrorOutput(
                                    "Session",
                                    "SessionPersistenceUnavailable",
                                    input.Error.Message,
                                    false,
                                    TimeSpan.FromSeconds(1))),
                                cancellationToken)
                        .ConfigureAwait(false);
                    job.Ended?.TrySetResult(false);
                    return;
                }

                if (_snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending
                    && job.Kind is not PersistKind.Pause)
                {
                    await FailPersistenceAsync(cancellationToken).ConfigureAwait(false);
                }

                job.Ended?.TrySetResult(false);
                return;
            }

            if (input.Saved is { } saved)
            {
                _durableRevision = saved.Revision;
                _durableSnapshot = saved;
                AdoptPersisted(job.Proposed, saved, job.Kind);
                foreach (var entry in saved.Entries)
                {
                    if (entry.Role == ConversationRole.User)
                    {
                        _undurableUserEntryIds.Remove(entry.EntryId);
                    }
                }
            }

            try
            {
                if (job.Then is { } then && ShouldRunPersistThen(job.Kind))
                {
                    await then(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                job.Ended?.TrySetResult(job.Kind is not PersistKind.TerminalEnd || _snapshot.Status is SessionStatus.Ended);
            }
        }
        finally
        {
            job.Applied.TrySetResult();
        }
    }

    private bool ShouldRunPersistThen(PersistKind kind) =>
        kind switch
        {
            PersistKind.TerminalEnd => true,
            PersistKind.Pause => _snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending,
            PersistKind.Checkpoint => _snapshot.Status is SessionStatus.Attached or SessionStatus.Created,
            _ => _snapshot.Status is SessionStatus.Attached or SessionStatus.Created
        };

    private async Task FailPersistenceAsync(CancellationToken cancellationToken)
    {
        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        var context = NewContext();
        if (!_deactivated)
        {
            CancelBrainEvaluation();
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
                await SupersedeAsync(context, live, cancellationToken, "disconnected").ConfigureAwait(false);
            }

            _outputActivity = OutputActivity.Idle;
            _initiativeHeld = false;
        }

        _snapshot = _durableSnapshot with
        {
            Status = SessionStatus.Paused,
            PendingMode = null,
            PauseReason = "persistence",
            RuntimeEpoch = Math.Max(_durableSnapshot.RuntimeEpoch, _snapshot.RuntimeEpoch) + 1,
            UpdatedAt = _time.GetUtcNow()
        };
        SessionPauseTelemetry.Record("persistence");
        await PublishAsync(
                new SessionOutput(
                    context,
                    null,
                    new ErrorOutput(
                        "Session",
                        "SessionPersistenceUnavailable",
                        "Persistent save failed.",
                        false,
                        TimeSpan.FromSeconds(1))),
                cancellationToken)
            .ConfigureAwait(false);
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (_durableSnapshot.Status is not SessionStatus.Paused)
        {
            RequestPersist(_snapshot, PersistKind.Pause);
        }
    }

    private async Task RunPersistAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var first in _persistJobs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var job = await CoalesceQueuedCheckpointsAsync(first, cancellationToken).ConfigureAwait(false);
                SessionSnapshot? saved = null;
                Exception? error = null;
                try
                {
                    saved = await SaveWithRetriesAsync(job, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                await CompletePersistJobAsync(job, saved, error, superseded: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsCoalescableCheckpoint(PersistJob job) =>
        job.Kind == PersistKind.Checkpoint && job.Then is null && job.Ended is null;

    private async Task<PersistJob> CoalesceQueuedCheckpointsAsync(PersistJob job, CancellationToken cancellationToken)
    {
        if (!IsCoalescableCheckpoint(job))
        {
            return job;
        }

        while (_persistJobs.Reader.TryPeek(out var next) && IsCoalescableCheckpoint(next))
        {
            if (!_persistJobs.Reader.TryRead(out next))
            {
                break;
            }

            await CompletePersistJobAsync(job, saved: null, error: null, superseded: true, cancellationToken)
                .ConfigureAwait(false);
            job = next;
        }

        return job;
    }

    private async Task CompletePersistJobAsync(
        PersistJob job,
        SessionSnapshot? saved,
        Exception? error,
        bool superseded,
        CancellationToken cancellationToken)
    {
        if (Enqueue(new PersistCompletedReceived(NewContext(), job, saved, error, superseded), urgent: true))
        {
            await job.Applied.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        job.Ended?.TrySetResult(false);
        job.Applied.TrySetResult();
    }

    private async Task<SessionSnapshot> SaveWithRetriesAsync(PersistJob job, CancellationToken cancellationToken)
    {
        var abandon = _abandonPersist.Task;
        AgentCoreException? last = null;
        for (var attempt = 0; attempt <= PersistRetryDelays.Length; attempt++)
        {
            if (job.Kind != PersistKind.TerminalEnd && abandon.IsCompleted)
            {
                throw last ?? AgentCoreErrors.Persistence("Persistent save superseded by a terminal command.");
            }

            try
            {
                return await WriteSnapshotAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentCoreException ex) when (ex.Code == "SessionPersistenceUnavailable"
                                               && ex.Message.Contains("superseded", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
            catch (AgentCoreException ex) when (ex.Code == "SessionPersistenceUnavailable" && attempt < PersistRetryDelays.Length)
            {
                last = ex;
                var waitAbandon = job.Kind == PersistKind.TerminalEnd
                    ? new TaskCompletionSource().Task
                    : abandon;
                await DelayPersistRetryAsync(PersistRetryDelays[attempt], waitAbandon, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? AgentCoreErrors.Persistence("Persistent save failed.");
    }

    private async Task<SessionSnapshot> WriteSnapshotAsync(PersistJob job, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (job.Kind != PersistKind.TerminalEnd && job.TerminalFence < Volatile.Read(ref _terminalFence))
        {
            throw AgentCoreErrors.Persistence("Persistent save superseded by a terminal command.");
        }

        if (job.Kind == PersistKind.Pause && _durableSnapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return _durableSnapshot;
        }

        var toSave = job.Kind switch
        {
            PersistKind.TerminalEnd => RebaseForPersist(job.Proposed, SessionStatus.Ended, now) with
            {
                LifecycleStatus = job.Proposed.LifecycleStatus,
                LifecycleReason = job.Proposed.LifecycleReason,
                LifecycleSource = job.Proposed.LifecycleSource,
                LifecycleChangedAt = job.Proposed.LifecycleChangedAt,
                RuntimeEpoch = job.Proposed.RuntimeEpoch
            },
            PersistKind.Pause => RebaseForPersist(job.Proposed, SessionStatus.Paused, _durableSnapshot.UpdatedAt),
            _ => job.Proposed with
            {
                Revision = _durableRevision + 1,
                UpdatedAt = CatalogUpdatedAt(job.Proposed, _durableSnapshot, now)
            }
        };

        await _store.SaveAsync(toSave, _durableRevision, cancellationToken).ConfigureAwait(false);
        return toSave;
    }

    private SessionSnapshot RebaseForPersist(SessionSnapshot proposed, SessionStatus status, DateTimeOffset updatedAt)
    {
        var entries = proposed.Entries.Count >= _durableSnapshot.Entries.Count
            ? proposed.Entries
            : _durableSnapshot.Entries;
        return proposed with
        {
            Entries = entries,
            Status = status,
            PendingMode = null,
            UpdatedAt = updatedAt,
            Revision = _durableRevision + 1
        };
    }

    private static DateTimeOffset CatalogUpdatedAt(
        SessionSnapshot proposed,
        SessionSnapshot durable,
        DateTimeOffset now) =>
        TouchesCatalogOrder(proposed, durable) ? now : durable.UpdatedAt;

    private static bool TouchesCatalogOrder(SessionSnapshot proposed, SessionSnapshot durable) =>
        proposed.Title != durable.Title
        || proposed.ArchivedAt != durable.ArchivedAt
        || !EntriesEqual(proposed.Entries, durable.Entries);

    private void AdoptPersisted(SessionSnapshot proposed, SessionSnapshot saved, PersistKind kind)
    {
        if (kind is PersistKind.TerminalEnd)
        {
            _snapshot = MergePersisted(saved, _snapshot) with
            {
                Status = SessionStatus.Ended,
                LifecycleStatus = saved.LifecycleStatus,
                PendingMode = null
            };
            return;
        }

        if (_snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
        {
            return;
        }

        if (kind is PersistKind.Pause)
        {
            _snapshot = MergePersisted(saved, _snapshot);
            return;
        }

        if (_snapshot.Status is SessionStatus.Paused)
        {
            return;
        }

        if (_snapshot.Entries.Count > saved.Entries.Count)
        {
            return;
        }

        if (_snapshot.Entries.Count < saved.Entries.Count
            || EntriesEqual(_snapshot.Entries, saved.Entries)
            || EntriesEqual(_snapshot.Entries, proposed.Entries))
        {
            _snapshot = MergePersisted(saved, _snapshot);
        }
    }

    private static SessionSnapshot MergePersisted(SessionSnapshot saved, SessionSnapshot live) =>
        saved with
        {
            Mode = live.Mode,
            PendingMode = live.PendingMode,
            PendingTopic = live.PendingTopic,
            Summary = live.SummarizedThroughEntrySequence >= saved.SummarizedThroughEntrySequence
                ? live.Summary
                : saved.Summary,
            SummarizedThroughEntrySequence = Math.Max(saved.SummarizedThroughEntrySequence, live.SummarizedThroughEntrySequence),
            Entries = MergeEntryOffsets(saved.Entries, live.Entries),
            LastEntrySequence = Math.Max(saved.DurableLastEntrySequence, live.DurableLastEntrySequence),
            Status = live.Status is SessionStatus.Ending or SessionStatus.Ended or SessionStatus.Attached
                ? live.Status
                : saved.Status
        };

    private static ConversationEntry[] MergeEntryOffsets(
        IReadOnlyList<ConversationEntry> saved,
        IReadOnlyList<ConversationEntry> live)
    {
        if (saved.Count != live.Count)
        {
            return saved.Count > live.Count ? saved.ToArray() : live.ToArray();
        }

        var merged = new ConversationEntry[saved.Count];
        for (var index = 0; index < saved.Count; index++)
        {
            var durable = saved[index];
            var current = live[index];
            if (durable.EntryId != current.EntryId)
            {
                merged[index] = durable;
                continue;
            }

            merged[index] = durable with
            {
                Text = current.Text.Length > durable.Text.Length ? current.Text : durable.Text,
                Status = current.Status == EntryStatus.Streaming ? current.Status : durable.Status,
                ReceivedTextEndExclusive = Math.Max(durable.ReceivedTextEndExclusive, current.ReceivedTextEndExclusive),
                HeardTextEndExclusive = Math.Max(durable.HeardTextEndExclusive, current.HeardTextEndExclusive),
                Envelope = ResponseEnvelopeParser.MergeDelivery(durable.Envelope, current.Envelope)
            };
        }

        return merged;
    }

    private static bool EntriesEqual(
        IReadOnlyList<ConversationEntry> left,
        IReadOnlyList<ConversationEntry> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private async Task DelayPersistRetryAsync(TimeSpan delay, Task abandon, CancellationToken cancellationToken)
    {
        var delayTask = Task.Delay(delay, _time, cancellationToken);
        var completed = await Task.WhenAny(delayTask, abandon).ConfigureAwait(false);
        if (completed == abandon)
        {
            throw AgentCoreErrors.Persistence("Persistent save superseded by a terminal command.");
        }

        await delayTask.ConfigureAwait(false);
    }

    private async Task CheckpointStreamingAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - _lastCheckpoint < TimeSpan.FromSeconds(1))
        {
            return;
        }

        RequestPersist(_snapshot, PersistKind.Checkpoint);
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
