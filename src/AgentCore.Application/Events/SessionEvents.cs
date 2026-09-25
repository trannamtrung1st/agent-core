using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Events;

public sealed record EventContext(
    Guid EventId,
    Guid SessionId,
    Guid Epoch,
    DateTimeOffset Timestamp,
    Guid CorrelationId,
    Guid? CausationId);

public abstract record SessionInput(EventContext Context);

public sealed record PulseReceived(EventContext Context) : SessionInput(Context);

public sealed record MailboxSaturatedReceived(EventContext Context) : SessionInput(Context);

public sealed record UserTextReceived(
    EventContext Context,
    string Text,
    TaskCompletionSource<bool>? Persisted = null,
    IReadOnlyList<Guid>? AttachmentIds = null,
    UserTextBehavior Behavior = UserTextBehavior.Interrupt) : SessionInput(Context);

public sealed record AttachmentsStagedReceived(
    EventContext Context,
    IReadOnlyList<Guid> AttachmentIds) : SessionInput(Context);

public sealed record SpeechEvidenceReceived(
    EventContext Context,
    SpeechRecognitionEvent Evidence,
    double? ActivityScore,
    int Epoch) : SessionInput(Context);

public sealed record AudioIngressFaultReceived(EventContext Context, string Code, string Message) : SessionInput(Context);

public sealed record ClassifierReturned(
    EventContext Context,
    Guid CandidateId,
    Guid UtteranceId,
    Guid ResponseId,
    int Revision,
    InteractionDecision Decision) : SessionInput(Context);

public sealed record AttachmentsProcessedReceived(
    EventContext Context,
    int TurnGeneration,
    Guid ResponseId,
    AgentTrigger Trigger,
    IReadOnlyList<AttachmentProcessResult> Results,
    bool Failed = false) : SessionInput(Context);

public sealed record ToolActivityReceived(
    EventContext Context,
    OutputActivity Activity,
    bool Hold,
    Guid ResponseId,
    Guid Epoch,
    TaskCompletionSource<bool> Admitted) : SessionInput(Context);

public sealed record BrainReturned(
    EventContext Context,
    int TurnGeneration,
    Guid ResponseId,
    AgentTrigger Trigger,
    AgentDecision Decision,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record BrainFailed(
    EventContext Context,
    int TurnGeneration,
    Guid ResponseId,
    AgentTrigger Trigger,
    bool Recoverable,
    string? Message,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record TimerElapsedReceived(
    EventContext Context,
    string Kind,
    int Generation,
    Guid? UtteranceId) : SessionInput(Context);

public sealed record AttachReceived(EventContext Context, TaskCompletionSource<bool> Attached) : SessionInput(Context);

public sealed record DetachReceived(EventContext Context, TaskCompletionSource Detached) : SessionInput(Context);

public sealed record SetModeReceived(EventContext Context, SessionMode Mode) : SessionInput(Context);

public sealed record ModelResultReceived(
    EventContext Context,
    Guid ResponseId,
    ModelGenerationEvent Event,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record CancelResponseReceived(
    EventContext Context,
    Guid ResponseId,
    TaskCompletionSource<ResponseCancelResult>? Completed = null) : SessionInput(Context);

public sealed record ApprovalResponseReceived(
    EventContext Context,
    Guid ResponseId,
    Guid ApprovalId,
    ToolApprovalDecision Decision,
    TaskCompletionSource<ResponseApprovalResult>? Completed = null) : SessionInput(Context);

public sealed record MuteReceived(EventContext Context, bool Muted) : SessionInput(Context);

public sealed record SynthesisResultReceived(
    EventContext Context,
    Guid ResponseId,
    int SegmentIndex,
    SpeechSynthesisEvent Event,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record PlaybackReportReceived(
    EventContext Context,
    Guid ResponseId,
    string Kind,
    long ConsumedSamples,
    int TextEndExclusive,
    TaskCompletionSource<bool> Admitted) : SessionInput(Context);

public sealed record ResponseReceiptReceived(
    EventContext Context,
    Guid ResponseId,
    int TextEndExclusive,
    TaskCompletionSource<bool> Admitted,
    IReadOnlyList<string>? BlockIds = null) : SessionInput(Context);

public sealed record EndSessionReceived(EventContext Context, TaskCompletionSource<bool> Persisted) : SessionInput(Context);

public sealed record LifecycleTransitionReceived(
    EventContext Context,
    SessionLifecycleStatus Target,
    LifecycleTransitionSource Source,
    string? Reason,
    TaskCompletionSource<bool> Persisted) : SessionInput(Context);

public sealed record RenameReceived(
    EventContext Context,
    string Title,
    TaskCompletionSource<bool> Persisted) : SessionInput(Context);

public sealed record SpeechLocaleReceived(
    EventContext Context,
    string? Locale,
    TaskCompletionSource<bool> Persisted) : SessionInput(Context);

public sealed record ModelSelectionReceived(
    EventContext Context,
    string? ModelKey,
    string? ReasoningEffort,
    ModelSelectionSource Source,
    TaskCompletionSource<bool> Persisted) : SessionInput(Context);

public sealed record InitiativeHoldReceived(EventContext Context, bool Held) : SessionInput(Context);

public sealed record ReopenedSnapshotReceived(
    EventContext Context,
    SessionSnapshot Snapshot,
    TaskCompletionSource Applied) : SessionInput(Context);

public sealed record TransportResumedSnapshotReceived(
    EventContext Context,
    SessionSnapshot Snapshot,
    TaskCompletionSource Applied) : SessionInput(Context);

public sealed record ProfileUpdatedReceived(
    EventContext Context,
    UserProfile Profile,
    TaskCompletionSource Applied) : SessionInput(Context);

public sealed record EnvironmentReceived(EventContext Context, EnvironmentEvent Event) : SessionInput(Context);

public sealed record DurableOccurrenceReceived(
    EventContext Context,
    OccurrenceDelivery Delivery,
    TaskCompletionSource<OccurrenceAccept> Accepted) : SessionInput(Context);

public sealed record DurableOccurrenceBeginReceived(
    EventContext Context,
    OccurrenceDelivery Delivery,
    TaskCompletionSource<bool> Started) : SessionInput(Context);

public sealed record DurableOccurrenceAbandonReceived(
    EventContext Context,
    Guid OccurrenceId,
    TaskCompletionSource<bool> Abandoned) : SessionInput(Context);

public sealed record CompletionReturned(
    EventContext Context,
    int Generation,
    CompletionDecision Decision,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record CompactionReturned(
    EventContext Context,
    int Generation,
    long RuntimeEpoch,
    CompactionOutcome Outcome,
    TaskCompletionSource<bool> Processed) : SessionInput(Context);

public sealed record SessionOutput(EventContext Context, Guid? ResponseId, OutputPayload Payload);

public abstract record OutputPayload;

public sealed record PublicAgentDescriptor(
    string Id,
    int Version,
    string Name,
    string Role,
    string Description,
    bool VoiceAvailable,
    string Language = "en");

public sealed record PublicResponseBlock(
    string BlockId,
    string Kind,
    string Text,
    string FallbackText,
    string? AttachmentId,
    string? ArtifactId);

public sealed record PublicHistoryAttachment(
    Guid AttachmentId,
    string DisplayName,
    string ContentType);

public sealed record PublicHistoryEntry(
    Guid EntryId,
    long Sequence,
    Guid? SourceEventId,
    ConversationRole Role,
    string Text,
    Guid? ResponseId,
    EntryStatus Status,
    int HeardTextEndExclusive,
    int ReceivedTextEndExclusive,
    SessionMode DeliveryMode,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PublicResponseBlock> Blocks,
    IReadOnlyList<PublicHistoryAttachment>? Attachments = null,
    string? FinishReason = null,
    string? InterruptReason = null,
    string? SpeechText = null);

public sealed record PublicPendingApproval(
    Guid ApprovalId,
    Guid ResponseId,
    Guid OperationId,
    string ToolName,
    string Effect,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset ExpiresAt);

public sealed record SessionReadyProjection(
    SessionMode Mode,
    SessionMode? PendingMode,
    SessionStatus Status,
    PublicAgentDescriptor Agent,
    Guid? StreamId,
    AudioFormat? AudioFormat,
    RecognitionCapabilities Recognition,
    SynthesisCapabilities Synthesis,
    string BargeInPolicy,
    long LastEntrySequence,
    IReadOnlyList<PublicHistoryEntry> History,
    Guid? ActiveResponseId,
    string InputTransport,
    string OutputTransport,
    SessionLifecycleStatus LifecycleStatus = SessionLifecycleStatus.Active,
    SpeechLocaleResolution? SpeechLocale = null,
    SessionModelSelection? ModelSelection = null,
    PublicPendingApproval? PendingApproval = null);

public sealed record ReadyOutput(SessionReadyProjection Ready) : OutputPayload;

public sealed record ResponseStartedOutput(Guid EntryId, long EntrySequence, string Trigger) : OutputPayload;

public sealed record TextDeltaOutput(int TextStart, string Text) : OutputPayload;

public sealed record SpeechProjectionOutput(ResponseSpeechMode Mode, string Text) : OutputPayload;

public sealed record TextCompletedOutput(int TextLength) : OutputPayload;

public sealed record BlockUpsertOutput(
    string BlockId,
    string Kind,
    string Text,
    string FallbackText,
    string? AttachmentId,
    string? ArtifactId) : OutputPayload;

public sealed record AudioFrameOutput(
    long FrameSequence,
    long SampleOffset,
    bool IsFinal,
    byte[] Data) : OutputPayload;

public sealed record SpeechOutputSegmentOutput(
    int SegmentIndex,
    int TextStart,
    string Text,
    string VoiceHint,
    string Language,
    double SpeakingRate) : OutputPayload;

public sealed record SpeechOutputCompletedOutput(int TextEndExclusive) : OutputPayload;

public sealed record ResponseCompletedOutput(
    bool Failed,
    int HeardTextEndExclusive,
    string? InterruptReason = null,
    string? FinishReason = null,
    string? SpeechText = null) : OutputPayload;

public sealed record ResponseInterruptedOutput(string Reason, int HeardTextEndExclusive) : OutputPayload;

public sealed record StateChangedOutput(
    SessionStatus Status,
    SessionMode Mode,
    SessionMode? PendingMode,
    string InputState,
    string OutputState,
    bool Muted,
    Guid? StreamId,
    string? PauseReason = null,
    SessionLifecycleStatus LifecycleStatus = SessionLifecycleStatus.Active) : OutputPayload;

public sealed record PlaybackStopOutput(string Reason) : OutputPayload;

public sealed record PlaybackGainOutput(double Gain, int RampMs, Guid? CandidateId) : OutputPayload;

public sealed record TranscriptPartialOutput(Guid UtteranceId, int Revision, string Text) : OutputPayload;

public sealed record TranscriptFinalOutput(Guid UtteranceId, string Text, Guid EntryId, long EntrySequence) : OutputPayload;

public sealed record TranscriptDiscardedOutput(Guid UtteranceId) : OutputPayload;

public sealed record ErrorOutput(
    string Category,
    string Code,
    string SafeMessage,
    bool Fatal,
    TimeSpan? RetryAfter) : OutputPayload;

public enum ResponseProgressKind
{
    Preparing,
    ReadingAttachments,
    RunningTool,
    WaitingExternal,
    Finalizing
}

public enum ResponseProgressState
{
    Started,
    Updated,
    Completed,
    Failed
}

public sealed record ResponseProgressOutput(
    ResponseProgressKind Kind,
    ResponseProgressState State,
    Guid? OperationId = null,
    string? Message = null) : OutputPayload;

public sealed record ApprovalRequestedOutput(
    Guid ApprovalId,
    Guid OperationId,
    string ToolName,
    string Effect,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset ExpiresAt) : OutputPayload;

public static class ResponseProgressMessages
{
    public const string ReadingAttachments = "Reading attachments…";
    public const string RunningTools = "Running tools…";
    public const string WaitingForApproval = "Waiting for your approval…";
    public const string Finalizing = "Finalizing response…";
    public const int MaxLength = 80;

    public static string? Bound(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        return message.Length <= MaxLength ? message : message[..MaxLength];
    }
}

public sealed record CompletionIntentOutput(string Reason, bool Advisory) : OutputPayload;

public interface ISessionOutput
{
    ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default);
}

public sealed record ResponseAudio(
    Guid SessionId,
    Guid ResponseId,
    long FrameSequence,
    long SampleOffset,
    bool IsFinal,
    ReadOnlyMemory<byte> Data);

public interface ISessionAudioOutput
{
    ValueTask PublishAsync(ResponseAudio audio, CancellationToken cancellationToken = default);
}

public static class PublicHistory
{
    public static PublicHistoryEntry FromEntry(ConversationEntry entry)
    {
        var text = entry.Role == ConversationRole.Assistant
            ? entry.Text[..Math.Min(entry.ReceivedTextEndExclusive, entry.Text.Length)]
            : entry.Text;
        var heardLimit = entry.Envelope?.SpeechText?.Length ?? text.Length;
        var heard = Math.Min(entry.HeardTextEndExclusive, heardLimit);
        var received = Math.Min(entry.ReceivedTextEndExclusive, text.Length);
        var blocks = entry.Role == ConversationRole.Assistant && entry.Envelope is { } envelope
            ? envelope.Blocks
                .Where(block => block.DisplayDelivered)
                .Select(ToPublicBlock)
                .ToArray()
            : [];
        var attachments = entry.Attachments is { Count: > 0 }
            ? entry.Attachments
                .Select(item => new PublicHistoryAttachment(item.AttachmentId, item.DisplayName, item.ContentType))
                .ToArray()
            : null;
        var speechText = entry.Role == ConversationRole.Assistant
            ? entry.Envelope?.PublicCustomSpeech()
            : null;
        return new PublicHistoryEntry(
            entry.EntryId,
            entry.Sequence,
            entry.SourceEventId,
            entry.Role,
            text,
            entry.ResponseId,
            entry.Status,
            heard,
            received,
            entry.DeliveryMode,
            entry.CreatedAt,
            blocks,
            attachments,
            entry.FinishReason,
            entry.InterruptReason,
            speechText);
    }

    private static PublicResponseBlock ToPublicBlock(ResponseBlock block) =>
        new(
            block.BlockId,
            block.Kind switch
            {
                ResponseBlockKind.Markdown => "markdown",
                ResponseBlockKind.AttachmentReference => "attachment",
                ResponseBlockKind.ArtifactReference => "artifact",
                _ => "unknown"
            },
            string.IsNullOrEmpty(block.DisplayText) ? block.FallbackText : block.DisplayText,
            block.FallbackText,
            block.AttachmentId,
            block.ArtifactId);

    public static PublicAgentDescriptor FromDefinition(AgentDefinition definition, bool voiceAvailable) =>
        FromIdentity(definition, definition.Identity, voiceAvailable);

    public static PublicAgentDescriptor FromSnapshot(SessionSnapshot snapshot, bool voiceAvailable)
    {
        var identity = snapshot.PinnedPersona ?? snapshot.Definition.Identity;
        return FromIdentity(snapshot.Definition, identity, voiceAvailable);
    }

    private static PublicAgentDescriptor FromIdentity(
        AgentDefinition definition,
        AgentIdentity identity,
        bool voiceAvailable) =>
        new(
            definition.Id,
            definition.Version,
            identity.Name,
            identity.Role,
            identity.Description,
            voiceAvailable,
            definition.ConversationPolicy.Language);
}
