using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentCore.Application.Agents;
using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Interaction;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime : IAsyncDisposable
{
    internal static Func<ILanguageModel, ILanguageModel>? TestDecorateLanguageModel;

    private readonly Channel<SessionInput> _mailbox = Channel.CreateBounded<SessionInput>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Channel<SessionInput> _urgent = Channel.CreateUnbounded<SessionInput>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _wake = new(0);

    private readonly ILanguageModelResolver _models;
    private readonly IModelCatalog? _catalog;
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
    private readonly IUserTurnCapabilityValidator? _turnCapabilities;
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
    private bool _usesResponseContract;
    private bool _structuredOutput;
    private bool _semanticReady;
    private int _publishedDisplayLength;
    private string? _publishedSpeechProjection;
    private bool _voiceSpeechResolved;
    private string _resolvedSpeakable = string.Empty;
    private readonly HashSet<string> _publishedBlockIds = new(StringComparer.Ordinal);
    private int _ttsFedLength;
    private string _ttsFedPrefix = string.Empty;
    private bool _responseTerminal;
    private string? _modelFinishReason;
    private CancellationTokenSource? _responseCts;
    private DateTimeOffset _lastCheckpoint = DateTimeOffset.MinValue;
    private int _inflight;
    private TaskCompletionSource _idle = CompletedIdle();
    private TaskCompletionSource _mailboxIdle = CompletedIdle();
    private InputActivity _input = InputActivity.Idle;
    private OutputActivity _outputActivity = OutputActivity.Idle;
    private Guid? _progressOwnerResponseId;
    private bool _progressLive;
    private ResponseProgressKind _progressKind;
    private Guid? _progressOperationId;
    private long _progressStartedTimestamp;
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
        VoiceAvailability? voice = null,
        ILanguageModelResolver? modelResolver = null,
        IModelCatalog? catalog = null,
        IUserTurnCapabilityValidator? turnCapabilities = null)
    {
        _snapshot = snapshot;
        _models = modelResolver ?? new StaticLanguageModelResolver(languageModel);
        _catalog = catalog;
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
        _turnCapabilities = turnCapabilities;
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

    public async Task ApplyProfileAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new ProfileUpdatedReceived(context, profile, applied), urgent: true))
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

    public async Task<bool> RequestModelSettingsAsync(
        string? modelKey,
        string? reasoningEffort,
        ModelSelectionSource source,
        CancellationToken cancellationToken = default)
    {
        var persisted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new ModelSelectionReceived(context, modelKey, reasoningEffort, source, persisted), urgent: true))
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
                        await RecoverProactiveHandleFailureAsync(brain, cancellationToken).ConfigureAwait(false);
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
                case ApprovalResponseReceived approval:
                    await HandleApprovalResponseAsync(approval, cancellationToken).ConfigureAwait(false);
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
                case ModelSelectionReceived modelSelection:
                    await HandleModelSelectionAsync(modelSelection, cancellationToken).ConfigureAwait(false);
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
                case ProfileUpdatedReceived profileUpdated:
                    HandleProfileUpdated(profileUpdated);
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
            if (input is AttachReceived or EndSessionReceived or LifecycleTransitionReceived or RenameReceived or SpeechLocaleReceived or ModelSelectionReceived or DetachReceived)
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
            case ModelSelectionReceived modelSelection:
                modelSelection.Persisted.TrySetResult(value);
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
            var interruptReason = "userSteer";
            await TerminalizeActiveResponseAsync(cause, liveResponse, cancellationToken, interruptReason, requestPersist: false)
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
        _snapshot = _snapshot with
        {
            Entries = entries,
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
            await FinishOwnedProgressAsync(input.Context, ResponseProgressState.Failed, cancellationToken)
                .ConfigureAwait(false);
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

        PinModelSelectionIfMissing();
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
            now,
            ModelProvenance: ToProvenance(_snapshot.ModelSelection));

        _activeResponseId = input.ResponseId;
        _progressOwnerResponseId = input.ResponseId;
        _activeEntryId = entryId;
        _accumulator.Reset();
        _envelope = null;
        _usesResponseContract = true;
        _semanticReady = false;
        _structuredOutput = false;
        _publishedDisplayLength = 0;
        _publishedSpeechProjection = null;
        _voiceSpeechResolved = false;
        _resolvedSpeakable = string.Empty;
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

        var request = speakable.Request with
        {
            ResponseId = input.ResponseId,
            ReasoningEffort = _snapshot.ModelSelection?.ReasoningEffort,
            ResponseContract = new ModelResponseContract(
                SpeechWillBeUsed: _snapshot.Mode == SessionMode.Voice)
        };
        var model = ResolveSessionModel(
            input.Trigger.Kind == TriggerKind.UserTurn ? ModelPurpose.Conversation : ModelPurpose.Initiative);
        _structuredOutput = model.Capabilities.StructuredOutput;
        BeginWork();
        var responseToken = _responseCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await PumpModelAsync(model, request, input.Context, responseToken).ConfigureAwait(false);
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
        PinModelSelectionIfMissing();
        var snapshot = _snapshot;
        var model = ResolveSessionModel(ModelPurpose.CompletionEvaluation);
        BeginWork();
        _ = Task.Run(async () =>
        {
            try
            {
                CompletionDecision decision;
                try
                {
                    decision = await CompletionEvaluator.EvaluateAsync(
                            model,
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
        await LaunchPreparedTurnAsync(batchCause, trigger, _ids.NewId(), turn, attachmentIds).ConfigureAwait(false);
        await PublishWaitingOutputAsync(batchCause).ConfigureAwait(false);
        return true;
    }

    private async Task LaunchPreparedTurnAsync(
        EventContext cause,
        AgentTrigger trigger,
        Guid responseId,
        int turn,
        IReadOnlyList<Guid> attachmentIds)
    {
        await FinishOwnedProgressAsync(cause, ResponseProgressState.Failed, CancellationToken.None)
            .ConfigureAwait(false);
        _progressOwnerResponseId = responseId;
        if (_processor is null || attachmentIds.Count == 0)
        {
            if (_outputActivity == OutputActivity.ProcessingAttachments)
            {
                _outputActivity = OutputActivity.WaitingForAgent;
            }

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
                await PublishProgressAsync(
                        inbound,
                        responseId,
                        ResponseProgressKind.ReadingAttachments,
                        ResponseProgressState.Started,
                        operationId: null,
                        ResponseProgressMessages.ReadingAttachments,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                IReadOnlyList<AttachmentProcessResult> results;
                var failed = false;
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
                    if (!TryMailbox(new AttachmentsProcessedReceived(inbound, turn, responseId, trigger, [], Failed: true)))
                    {
                        EndWork();
                    }

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Attachment processing failed for {SessionId}", SessionId);
                    results = [];
                    failed = true;
                }

                if (!TryMailbox(new AttachmentsProcessedReceived(inbound, turn, responseId, trigger, results, failed)))
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
        if (_deactivated
            || input.TurnGeneration != _turnGeneration
            || (_progressOwnerResponseId is { } owner && owner != input.ResponseId))
        {
            if (!_deactivated
                && _progressOwnerResponseId == input.ResponseId
                && _outputActivity == OutputActivity.ProcessingAttachments)
            {
                await PublishOutputIdleAsync(input.Context, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (TryRejectProcessedImageCapability(input))
        {
            await PublishProgressAsync(
                    input.Context,
                    input.ResponseId,
                    ResponseProgressKind.ReadingAttachments,
                    ResponseProgressState.Failed,
                    operationId: null,
                    ResponseProgressMessages.ReadingAttachments,
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishModelCapabilityFailureAsync(input.Context, input.ResponseId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PublishProgressAsync(
                input.Context,
                input.ResponseId,
                ResponseProgressKind.ReadingAttachments,
                input.Failed ? ResponseProgressState.Failed : ResponseProgressState.Completed,
                operationId: null,
                ResponseProgressMessages.ReadingAttachments,
                cancellationToken)
            .ConfigureAwait(false);
        _outputActivity = OutputActivity.WaitingForAgent;
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
        LaunchBrain(input.Context, input.Trigger, input.ResponseId, input.TurnGeneration, input.Results);
        if (_outputActivity == OutputActivity.Idle)
        {
            await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryRejectProcessedImageCapability(AttachmentsProcessedReceived input)
    {
        if (_turnCapabilities is null || input.Failed)
        {
            return false;
        }

        try
        {
            _turnCapabilities.ValidateProcessedImages(_snapshot.ModelSelection, input.Results);
            return false;
        }
        catch (AgentCoreException ex) when (ex.Code == "ModelCapabilityUnsupported")
        {
            return true;
        }
    }

    private async Task PublishModelCapabilityFailureAsync(
        EventContext cause,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        await FinishOwnedProgressAsync(cause, ResponseProgressState.Failed, cancellationToken).ConfigureAwait(false);
        await PublishOutputIdleAsync(cause, cancellationToken).ConfigureAwait(false);
        await PublishAsync(
                new SessionOutput(
                    cause,
                    responseId,
                    new ErrorOutput(
                        "Session",
                        "ModelCapabilityUnsupported",
                        AgentCoreErrors.ModelCapabilityUnsupported().Message,
                        false,
                        null)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void LaunchBrain(
        EventContext cause,
        AgentTrigger trigger,
        Guid responseId,
        int turn,
        IReadOnlyList<AttachmentProcessResult>? attachments = null)
    {
        var proactive = trigger.Kind != TriggerKind.UserTurn;
        if (_deactivated || (proactive && _activeResponseId is not null))
        {
            _outputActivity = OutputActivity.Idle;
            return;
        }

        BeginWork();

        CancelBrainEvaluation();
        if (proactive)
        {
            _proactiveBrainInFlight = true;
        }

        _brainEvaluationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var evaluationToken = _brainEvaluationCts.Token;
        PinModelSelectionIfMissing();
        var model = ResolveSessionModel(
            trigger.Kind == TriggerKind.UserTurn ? ModelPurpose.Conversation : ModelPurpose.Initiative);

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
                    ModelSupportsTools: model.Capabilities.Tools,
                    UtcNow: _time.GetUtcNow(),
                    LastUserActivityAt: _snapshot.LastUserActivityAt,
                    LanguageModel: model,
                    ReasoningEffort: _snapshot.ModelSelection?.ReasoningEffort,
                    SummarizedThroughEntrySequence: _snapshot.SummarizedThroughEntrySequence,
                    LastEntrySequence: _snapshot.DurableLastEntrySequence);
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

    private async Task PumpModelAsync(
        ILanguageModel model,
        ModelRequest request,
        EventContext cause,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = RuntimeTelemetry.Activity.StartActivity("model");
        var messages = request.Messages.ToList();
        var steps = 0;
        var outputBytes = 0;
        var toolDeadline = request.Tools is { Count: > 0 };
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ITimer? overallTimer = toolDeadline ? ScheduleCancel(_time, overallCts, ToolLimits.Overall) : null;
        var overallDeadline = toolDeadline ? _time.GetUtcNow() + ToolLimits.Overall : (DateTimeOffset?)null;
        var generateToken = toolDeadline ? overallCts.Token : cancellationToken;
        try
        {
            while (!generateToken.IsCancellationRequested)
            {
                var pending = new List<ModelToolCall>();
                var finished = false;
                var working = request with { Messages = messages };
                await foreach (var evt in model.GenerateAsync(working, generateToken).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case ModelToolCallEvent tool:
                            pending.Add(tool.Call);
                            continue;
                        case ModelCompleted completed when completed.Reason == ModelStopReason.ToolCalls:
                            continue;
                        default:
                            if (!_recordedLlm && evt is ModelTextDelta or ModelDisplayDelta or ModelSemanticResponseReady)
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

                    var operationId = _ids.NewId();
                    await PublishProgressAsync(
                            cause,
                            request.ResponseId,
                            ResponseProgressKind.RunningTool,
                            ResponseProgressState.Started,
                            operationId,
                            ResponseProgressMessages.RunningTools,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    var executionResult = ToolExecutionResult.FromText(
                        """{"error":"invalid","message":"Tool execution failed."}""");
                    var toolStarted = Stopwatch.GetTimestamp();
                    ToolApprovalGrant? approvalGrant = null;
                    try
                    {
                        JsonElement args;
                        try
                        {
                            args = JsonSerializer.Deserialize<JsonElement>(
                                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                            if (args.ValueKind != JsonValueKind.Object)
                            {
                                executionResult = ToolExecutionResult.FromText(
                                    """{"error":"invalid","message":"Tool arguments must be a JSON object."}""");
                                args = default;
                            }
                        }
                        catch (JsonException)
                        {
                            executionResult = ToolExecutionResult.FromText(
                                """{"error":"invalid","message":"Tool arguments were malformed."}""");
                            args = default;
                        }

                        if (args.ValueKind == JsonValueKind.Object)
                        {
                            var policy = _tools.EvaluateExecutionPolicy(_snapshot.Definition, call.Name);
                            if (policy == ToolPolicyDecision.Deny || string.IsNullOrWhiteSpace(call.Name))
                            {
                                executionResult = ToolExecutionResult.FromText(
                                    """{"error":"forbidden","message":"Tool is not permitted for this role."}""");
                            }
                            else
                            {
                                string actionHash;
                                string? approvalSummaryOverride = null;
                                IReadOnlyDictionary<string, string>? approvalDetailsOverride = null;
                                var preparedFailed = false;
                                if (policy == ToolPolicyDecision.RequireApproval
                                    && string.Equals(call.Name, ToolCatalog.EmailSend, StringComparison.Ordinal))
                                {
                                    var prepared = await _tools.PrepareEmailSendApprovalAsync(args, overallCts.Token)
                                        .ConfigureAwait(false);
                                    if (prepared.Preparation is null)
                                    {
                                        executionResult = ToolExecutionResult.FromText(
                                            prepared.ErrorJson ?? """{"error":"invalid","message":"Unable to prepare email send approval."}""");
                                        preparedFailed = true;
                                        actionHash = string.Empty;
                                    }
                                    else
                                    {
                                        actionHash = prepared.Preparation.ActionHash;
                                        approvalSummaryOverride = prepared.Preparation.Summary;
                                        approvalDetailsOverride = prepared.Preparation.Details;
                                    }
                                }
                                else if (policy == ToolPolicyDecision.RequireApproval
                                    && string.Equals(call.Name, ToolCatalog.HttpRequest, StringComparison.Ordinal))
                                {
                                    var prepared = _tools.PrepareHttpRequestApproval(args);
                                    if (prepared.Preparation is null)
                                    {
                                        executionResult = ToolExecutionResult.FromText(
                                            prepared.ErrorJson ?? """{"error":"invalid","message":"Unable to prepare HTTP request approval."}""");
                                        preparedFailed = true;
                                        actionHash = string.Empty;
                                    }
                                    else
                                    {
                                        actionHash = prepared.Preparation.ActionHash;
                                        approvalSummaryOverride = prepared.Preparation.Summary;
                                        approvalDetailsOverride = prepared.Preparation.Details;
                                    }
                                }
                                else
                                {
                                    actionHash = ToolActionHash.Compute(call.Name, args);
                                }

                                if (!preparedFailed
                                    && policy == ToolPolicyDecision.RequireApproval)
                                {
                                    var pausedOverallRemaining = PauseToolClock(overallTimer, overallDeadline);
                                    try
                                    {
                                        approvalGrant = await WaitForToolApprovalAsync(
                                                cause,
                                                request.ResponseId,
                                                operationId,
                                                call,
                                                args,
                                                actionHash,
                                                cancellationToken,
                                                approvalSummaryOverride,
                                                approvalDetailsOverride)
                                            .ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        overallDeadline = ResumeToolClock(
                                            overallTimer,
                                            overallCts,
                                            pausedOverallRemaining);
                                    }

                                    if (approvalGrant is null
                                        || approvalGrant.RuntimeEpoch != _epoch
                                        || approvalGrant.ResponseId != request.ResponseId
                                        || approvalGrant.OperationId != operationId)
                                    {
                                        executionResult = ToolExecutionResult.FromText(
                                            """{"error":"rejected","message":"Action was not approved."}""");
                                        preparedFailed = true;
                                    }
                                }

                                if (!preparedFailed)
                                {
                                    using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                                    using var toolTimer = ScheduleCancel(_time, toolCts, ToolLimits.PerTool);
                                    executionResult = await _tools.ExecuteAsync(
                                            _snapshot.Definition,
                                            SessionId,
                                            call,
                                            ToolLimits.MaxOutputBytes - outputBytes,
                                            toolCts.Token,
                                            approvalGrant)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        RuntimeTelemetry.Record("tools", RuntimeTelemetry.ElapsedMs(toolStarted), call.Name);
                        RuntimeTelemetry.RecordDropped("tools");
                        await PublishProgressAsync(
                                cause,
                                request.ResponseId,
                                ResponseProgressKind.RunningTool,
                                ResponseProgressState.Failed,
                                operationId,
                                ResponseProgressMessages.RunningTools,
                                CancellationToken.None)
                            .ConfigureAwait(false);
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

                    executionResult = ToolResultAdmission.AdmitForModel(model, executionResult);
                    outputBytes += ToolOutputBudget.TextByteCount(executionResult);
                    if (outputBytes > ToolLimits.MaxOutputBytes)
                    {
                        await PublishProgressAsync(
                                cause,
                                request.ResponseId,
                                ResponseProgressKind.RunningTool,
                                ResponseProgressState.Failed,
                                operationId,
                                ResponseProgressMessages.RunningTools,
                                CancellationToken.None)
                            .ConfigureAwait(false);
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

                    await PublishProgressAsync(
                            cause,
                            request.ResponseId,
                            ResponseProgressKind.RunningTool,
                            ResponseProgressState.Completed,
                            operationId,
                            ResponseProgressMessages.RunningTools,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    messages.Add(new ModelMessage(
                        ModelRole.Tool,
                        executionResult.Text,
                        Parts: executionResult.Parts,
                        ToolCallId: call.Id,
                        Name: call.Name));
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
            await FinishOwnedProgressAsync(cause, ResponseProgressState.Failed, CancellationToken.None, request.ResponseId)
                .ConfigureAwait(false);
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
            await FinishOwnedProgressAsync(cause, ResponseProgressState.Failed, CancellationToken.None, request.ResponseId)
                .ConfigureAwait(false);
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

    private TimeSpan? PauseToolClock(ITimer? timer, DateTimeOffset? deadline)
    {
        if (timer is null || deadline is null)
        {
            return null;
        }

        var remaining = deadline.Value - _time.GetUtcNow();
        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return remaining;
    }

    private DateTimeOffset? ResumeToolClock(ITimer? timer, CancellationTokenSource cts, TimeSpan? remaining)
    {
        if (timer is null || remaining is null)
        {
            return null;
        }

        if (remaining.Value <= TimeSpan.Zero)
        {
            cts.Cancel();
            return _time.GetUtcNow();
        }

        timer.Change(remaining.Value, Timeout.InfiniteTimeSpan);
        return _time.GetUtcNow() + remaining.Value;
    }

    private async Task HandleModelAsync(ModelResultReceived input, CancellationToken cancellationToken)
    {
        if (_activeResponseId != input.ResponseId
            || _responseTerminal
            || input.Context.Epoch != _epoch)
        {
            input.Processed.TrySetResult();
            return;
        }

        switch (input.Event)
        {
            case ModelReasoningDelta:
                RuntimeTelemetry.RecordDiagnostic("llm.reasoning.delta", 0, "present");
                break;
            case ModelDisplayDelta delta when _usesResponseContract:
                await HandleDisplayDeltaAsync(input.Context, input.ResponseId, delta.Text, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ModelSemanticResponseReady ready when _usesResponseContract:
                await HandleSemanticReadyAsync(input.Context, input.ResponseId, ready.Response, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ModelTextDelta when _usesResponseContract:
                break;
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
                if (_usesResponseContract && !_semanticReady)
                {
                    await CompleteAsync(input.Context, input.ResponseId, failed: true, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

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
            case ModelFailed failed:
                if (_usesResponseContract)
                {
                    _envelope = null;
                    _accumulator.Reset();
                }

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
        else if (reason is "newText" or "userSteer")
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
        _logger.LogInformation(
            "Active response terminalized {SessionId} {ResponseId} reason {Reason}",
            SessionId,
            responseId,
            reason);
        _responseCts?.Cancel();
        _ttsCts?.Cancel();
        ClearPendingApproval();

        _responseLifecycle = ResponseLifecycle.Superseded;
        _outputActivity = OutputActivity.Interrupted;
        _initiativeHeld = false;
        SpeechTelemetry.RecordCancel(reason);
        var heard = UsesClientSpeech ? _ackedPlaybackText : _spokenUntil.Credit(_ackedSamples);
        ApplyHeard(heard);
        InvalidateSpeechJobs();
        await FinishOwnedProgressAsync(context, ResponseProgressState.Failed, cancellationToken, responseId)
            .ConfigureAwait(false);
        await PublishStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!_responseTerminal)
        {
            _responseTerminal = true;
            UpdateAssistant(EntryStatus.Interrupted, reason);
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
                            InterruptReason: reason,
                            SpeechText: PublicSpeechText())),
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
        await FinishOwnedProgressAsync(
                context,
                failed ? ResponseProgressState.Failed : ResponseProgressState.Completed,
                cancellationToken,
                responseId)
            .ConfigureAwait(false);
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
                                FinishReason: failed ? null : _modelFinishReason,
                                SpeechText: PublicSpeechText())),
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

    private void UpdateAssistant(EntryStatus status, string? interruptReason = null)
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
                        Envelope = status == EntryStatus.Failed && (!_usesResponseContract || !_semanticReady)
                            ? null
                            : CurrentEnvelope(status != EntryStatus.Streaming),
                        FinishReason = status == EntryStatus.Completed ? _modelFinishReason : null,
                        InterruptReason = status switch
                        {
                            EntryStatus.Interrupted => interruptReason ?? entry.InterruptReason,
                            EntryStatus.Completed or EntryStatus.Failed => null,
                            _ => entry.InterruptReason
                        }
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

    private string? PublicSpeechText()
    {
        if (_activeEntryId is not { } entryId)
        {
            return null;
        }

        return _snapshot.Entries.FirstOrDefault(item => item.EntryId == entryId)?.Envelope?.PublicCustomSpeech();
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

    private async Task HandleDisplayDeltaAsync(
        EventContext context,
        Guid responseId,
        string text,
        CancellationToken cancellationToken)
    {
        _accumulator.Append(text);
        await PublishEnvelopeProgressAsync(context, responseId, finalize: false, cancellationToken)
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
            KickTts(context);
            await ReleaseClientSpeechAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleSemanticReadyAsync(
        EventContext context,
        Guid responseId,
        ModelSemanticResponse semantic,
        CancellationToken cancellationToken)
    {
        Guid? finalizingOperationId = null;
        if (_structuredOutput)
        {
            finalizingOperationId = _ids.NewId();
            await PublishProgressAsync(
                    context,
                    responseId,
                    ResponseProgressKind.Finalizing,
                    ResponseProgressState.Started,
                    finalizingOperationId.Value,
                    ResponseProgressMessages.Finalizing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ResponseEnvelope mapped;
        try
        {
            mapped = SemanticResponseMapper.ToEnvelope(
                semantic,
                _snapshot.SessionId,
                _artifacts,
                AttachmentAllowed);
        }
        catch (ArgumentException)
        {
            if (finalizingOperationId is { } failedOp)
            {
                await PublishProgressAsync(
                        context,
                        responseId,
                        ResponseProgressKind.Finalizing,
                        ResponseProgressState.Failed,
                        failedOp,
                        ResponseProgressMessages.Finalizing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _envelope = null;
            _accumulator.Reset();
            await CompleteAsync(context, responseId, failed: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        _semanticReady = true;
        _envelope = mapped;
        _accumulator.Replace(mapped.DisplayText);
        if (finalizingOperationId is { } completedOp)
        {
            await PublishProgressAsync(
                    context,
                    responseId,
                    ResponseProgressKind.Finalizing,
                    ResponseProgressState.Completed,
                    completedOp,
                    ResponseProgressMessages.Finalizing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PublishEnvelopeProgressAsync(context, responseId, finalize: false, cancellationToken)
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
            KickTts(context);
            await ReleaseClientSpeechAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool AttachmentAllowed(string attachmentId)
    {
        if (LooksLikeUnsafeAttachment(attachmentId))
        {
            return false;
        }

        if (FixtureAttachmentReferenceAuthorizer.IsAuthorized(attachmentId))
        {
            return true;
        }

        if (!Guid.TryParse(attachmentId, out var id))
        {
            return false;
        }

        foreach (var entry in _snapshot.Entries)
        {
            if (entry.Attachments is not { Count: > 0 })
            {
                continue;
            }

            foreach (var attachment in entry.Attachments)
            {
                if (attachment.AttachmentId == id)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeUnsafeAttachment(string attachmentId) =>
        attachmentId.Contains("..", StringComparison.Ordinal)
        || attachmentId.Contains('/', StringComparison.Ordinal)
        || attachmentId.Contains('\\', StringComparison.Ordinal);

    private async Task TryPublishContractSpeechAsync(
        EventContext context,
        Guid responseId,
        bool finalize,
        CancellationToken cancellationToken)
    {
        if (_snapshot.Mode != SessionMode.Voice || _envelope is null || _voiceSpeechResolved)
        {
            return;
        }

        if (_envelope.SpeechMode == ResponseSpeechMode.None)
        {
            if (!finalize)
            {
                return;
            }

            await ResolveVoiceSpeechAsync(
                    context,
                    responseId,
                    _envelope,
                    string.Empty,
                    fallbackReason: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var hadExplicitSpeech = _envelope.SpeechMode == ResponseSpeechMode.Custom
            && !string.IsNullOrEmpty(_envelope.SpeechText);
        if (hadExplicitSpeech)
        {
            var explicitPlayback = SpokenOutput.ForPlayback(_envelope.SpeechText, _envelope.DisplayText);
            if (explicitPlayback.Length > 0)
            {
                await ResolveVoiceSpeechAsync(
                        context,
                        responseId,
                        _envelope,
                        explicitPlayback,
                        fallbackReason: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!finalize)
            {
                return;
            }

            var rejectedFallback = SpokenOutput.ForPlayback(null, _envelope.DisplayText);
            var effectiveEnvelope = rejectedFallback.Length > 0
                ? _envelope with { SpeechMode = ResponseSpeechMode.Same, SpeechText = null }
                : _envelope with { SpeechMode = ResponseSpeechMode.None, SpeechText = null };
            await ResolveVoiceSpeechAsync(
                    context,
                    responseId,
                    effectiveEnvelope,
                    rejectedFallback,
                    rejectedFallback.Length > 0
                        ? SpeechTelemetry.VoiceSpeechFallbackReason.RejectedExplicit
                        : null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        else if (!finalize)
        {
            return;
        }

        if (string.Equals(_modelFinishReason, "lengthLimit", StringComparison.Ordinal) && !hadExplicitSpeech)
        {
            await ResolveVoiceSpeechAsync(
                    context,
                    responseId,
                    _envelope,
                    string.Empty,
                    fallbackReason: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var derivedPlayback = SpokenOutput.ForPlayback(null, _envelope.DisplayText);
        SpeechTelemetry.VoiceSpeechFallbackReason? fallbackReason = derivedPlayback.Length > 0
            ? hadExplicitSpeech
                ? SpeechTelemetry.VoiceSpeechFallbackReason.RejectedExplicit
                : SpeechTelemetry.VoiceSpeechFallbackReason.MissingExplicit
            : null;
        await ResolveVoiceSpeechAsync(
                context,
                responseId,
                _envelope,
                derivedPlayback,
                fallbackReason,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PublishEnvelopeProgressAsync(
        EventContext context,
        Guid responseId,
        bool finalize,
        CancellationToken cancellationToken)
    {
        if (_usesResponseContract)
        {
            await TryPublishContractSpeechAsync(context, responseId, finalize, cancellationToken)
                .ConfigureAwait(false);
            if (finalize
                && _voiceSpeechResolved
                && _resolvedSpeakable.Length > 0
                && SpokenOutput.ShouldPersistDerivedSpeechText(_resolvedSpeakable, _envelope?.DisplayText ?? _accumulator.Text))
            {
                _envelope = (_envelope ?? new ResponseEnvelope(_accumulator.Text, null, [], ResponseSpeechMode.Same))
                    with { SpeechText = _resolvedSpeakable };
            }

            if (!VoiceDisplayPublicationAllowed())
            {
                return;
            }

            var contractDisplay = _semanticReady && _envelope is { DisplayText.Length: > 0 }
                ? _envelope.DisplayText
                : _accumulator.Text;
            if (contractDisplay.Length > _publishedDisplayLength)
            {
                var chunk = contractDisplay[_publishedDisplayLength..];
                await PublishAsync(
                        new SessionOutput(context, responseId, new TextDeltaOutput(_publishedDisplayLength, chunk)),
                        cancellationToken)
                    .ConfigureAwait(false);
                _publishedDisplayLength = contractDisplay.Length;
            }

            foreach (var block in _envelope?.Blocks ?? [])
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

            return;
        }
    }

    private async Task ResolveVoiceSpeechAsync(
        EventContext context,
        Guid responseId,
        ResponseEnvelope parsed,
        string speakable,
        SpeechTelemetry.VoiceSpeechFallbackReason? fallbackReason,
        CancellationToken cancellationToken)
    {
        _voiceSpeechResolved = true;
        _resolvedSpeakable = speakable;
        _publishedSpeechProjection = speakable.Length > 0 ? speakable : null;

        if (speakable.Length > 0)
        {
            _envelope = parsed with { SpeechText = speakable };
            if (fallbackReason is { } reason)
            {
                SpeechTelemetry.RecordVoiceSpeechFallback(reason);
                _logger.LogInformation(
                    reason == SpeechTelemetry.VoiceSpeechFallbackReason.RejectedExplicit
                        ? "Voice explicit speech was rejected; applied display-derived speech fallback."
                        : "Voice response completed without an explicit spoken form; applied display-derived speech fallback.");
            }

            await PublishAsync(
                    new SessionOutput(context, responseId, new SpeechProjectionOutput(parsed.SpeechMode, speakable)),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _envelope = parsed with { SpeechText = null };
        await ArmNoSpeechVoicePlaybackAsync(context, responseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ArmNoSpeechVoicePlaybackAsync(
        EventContext context,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        _playbackDone = true;
        if (UsesClientSpeech && !_speechOutputCompleted)
        {
            _speechOutputCompleted = true;
            _speechTextEndExclusive = 0;
            await PublishAsync(
                    new SessionOutput(context, responseId, new SpeechOutputCompletedOutput(0)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (UsesVoicePlayback)
        {
            await FinishAudioIfReadyAsync(context, responseId, cancellationToken).ConfigureAwait(false);
        }
    }

    private string CurrentTtsSource()
    {
        if (_voiceSpeechResolved)
        {
            return _resolvedSpeakable;
        }

        return string.Empty;
    }

    private void FeedTtsFromLockedSource()
    {
        if (_segmenter is null)
        {
            return;
        }

        var source = CurrentTtsSource();
        if (source.Length == 0)
        {
            return;
        }

        if (TtsSourceIdentityChanged(source))
        {
            ResetTtsFeedPipeline();
        }

        if (source.Length > _ttsFedLength)
        {
            EnqueueSegments(_segmenter.Append(source[_ttsFedLength..], _time.GetUtcNow()));
            _ttsFedLength = source.Length;
            _ttsFedPrefix = source;
        }
    }

    private bool TtsSourceIdentityChanged(string source)
    {
        if (_ttsFedLength == 0)
        {
            return false;
        }

        if (source.Length < _ttsFedLength)
        {
            return true;
        }

        return !source.AsSpan(0, _ttsFedLength)
            .SequenceEqual(_ttsFedPrefix.AsSpan(0, Math.Min(_ttsFedLength, _ttsFedPrefix.Length)));
    }

    private bool VoiceDisplayPublicationAllowed() =>
        _snapshot.Mode != SessionMode.Voice || _voiceSpeechResolved;

    private string DisplayText()
    {
        if (!VoiceDisplayPublicationAllowed())
        {
            return string.Empty;
        }

        return _envelope?.DisplayText ?? _accumulator.Text;
    }

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
        _ = finalize;
        var contract = _envelope
            ?? new ResponseEnvelope(_accumulator.Text, null, [], ResponseSpeechMode.Same);
        var prior = _snapshot.Entries.FirstOrDefault(item => item.EntryId == _activeEntryId)?.Envelope;
        return ResponseEnvelopeParser.MergeDelivery(prior, contract);
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
        ClearPendingApproval();
        _activeResponseId = null;
        _activeEntryId = null;
        _modelFinishReason = null;
        _progressOwnerResponseId = null;
        _progressLive = false;
        _progressOperationId = null;
        _progressStartedTimestamp = 0;
        _usesResponseContract = false;
        _semanticReady = false;
        _structuredOutput = false;
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
        RequestPersist(
            applied,
            PersistKind.Resume,
            then: async ct =>
            {
                ApplyProposedResumeState(_durableSnapshot);
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

        if (!await TryStartPendingUserBatchAsync(context, cancellationToken).ConfigureAwait(false)
            && CanEvaluateIdle())
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
        else
        {
            await FinishOwnedProgressAsync(context, ResponseProgressState.Failed, cancellationToken)
                .ConfigureAwait(false);
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
        Resume,
        TerminalEnd,
        ModelSelection
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

    private ILanguageModel ResolveSessionModel(ModelPurpose purpose)
    {
        var selection = _snapshot.ModelSelection
            ?? new SessionModelSelection(
                "unspecified",
                "primary-llm",
                "unspecified",
                ModelSelectionSource.SystemDefault,
                null);
        var resolved = _models.Resolve(selection, purpose);
        return TestDecorateLanguageModel?.Invoke(resolved) ?? resolved;
    }

    private static ModelGenerationProvenance? ToProvenance(SessionModelSelection? selection) =>
        selection is null
            ? null
            : new ModelGenerationProvenance(
                selection.CatalogKey,
                selection.ProviderAlias,
                selection.ModelId,
                selection.ReasoningEffort);

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
                    && job.Kind is not PersistKind.Pause and not PersistKind.ModelSelection)
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
            PersistKind.Resume => _snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending,
            PersistKind.ModelSelection => _snapshot.Status is not SessionStatus.Ended and not SessionStatus.Ending,
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
            await FinishOwnedProgressAsync(context, ResponseProgressState.Failed, cancellationToken)
                .ConfigureAwait(false);
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

        if (kind is PersistKind.ModelSelection)
        {
            _snapshot = _snapshot with
            {
                ModelSelection = saved.ModelSelection,
                Revision = saved.Revision,
                UpdatedAt = saved.UpdatedAt
            };
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
                Envelope = current.Status == EntryStatus.Failed
                    ? current.Envelope
                    : ResponseEnvelopeParser.MergeDelivery(durable.Envelope, current.Envelope)
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

    private async Task PublishProgressAsync(
        EventContext context,
        Guid responseId,
        ResponseProgressKind kind,
        ResponseProgressState state,
        Guid? operationId,
        string? message,
        CancellationToken cancellationToken)
    {
        if (state is ResponseProgressState.Started or ResponseProgressState.Updated)
        {
            if (_deactivated
                || context.Epoch != _epoch
                || responseId != (_activeResponseId ?? _progressOwnerResponseId)
                || (_responseTerminal && _activeResponseId == responseId))
            {
                return;
            }

            _progressLive = true;
            _progressOwnerResponseId = responseId;
            _progressKind = kind;
            _progressOperationId = operationId;
            if (_progressStartedTimestamp == 0)
            {
                _progressStartedTimestamp = Stopwatch.GetTimestamp();
            }
        }
        else if (!_progressLive || _progressOwnerResponseId != responseId)
        {
            return;
        }
        else
        {
            _progressLive = false;
        }

        await PublishAsync(
                new SessionOutput(
                    context,
                    responseId,
                    new ResponseProgressOutput(
                        kind,
                        state,
                        operationId,
                        ResponseProgressMessages.Bound(message))),
                cancellationToken)
            .ConfigureAwait(false);
        RecordPublishedProgress(kind, state);
    }

    private async Task FinishOwnedProgressAsync(
        EventContext context,
        ResponseProgressState state,
        CancellationToken cancellationToken,
        Guid? responseId = null)
    {
        if (!_progressLive)
        {
            return;
        }

        var owned = _progressOwnerResponseId;
        if (owned is null || (responseId is { } expected && expected != owned))
        {
            return;
        }

        _progressLive = false;
        await PublishAsync(
                new SessionOutput(
                    context,
                    owned.Value,
                    new ResponseProgressOutput(
                        _progressKind,
                        state,
                        _progressOperationId,
                        null)),
                cancellationToken)
            .ConfigureAwait(false);
        RecordPublishedProgress(_progressKind, state);
    }

    private void RecordPublishedProgress(ResponseProgressKind kind, ResponseProgressState state)
    {
        double? activeMs = null;
        if (state is ResponseProgressState.Completed or ResponseProgressState.Failed)
        {
            if (_progressStartedTimestamp != 0)
            {
                activeMs = RuntimeTelemetry.ElapsedMs(_progressStartedTimestamp);
            }

            _progressStartedTimestamp = 0;
        }

        ProgressTelemetry.Record(kind, state, activeMs);
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
