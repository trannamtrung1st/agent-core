using AgentCore.Application.Ports;
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

public sealed record UserTextReceived(EventContext Context, string Text) : SessionInput(Context);

public sealed record SpeechEvidenceReceived(
    EventContext Context,
    SpeechRecognitionEvent Evidence,
    double? ActivityScore) : SessionInput(Context);

public sealed record AudioIngressFaultReceived(EventContext Context, string Code, string Message) : SessionInput(Context);

public sealed record ClassifierReturned(
    EventContext Context,
    Guid CandidateId,
    Guid UtteranceId,
    Guid ResponseId,
    int Revision,
    InteractionDecision Decision) : SessionInput(Context);

public sealed record BrainReturned(
    EventContext Context,
    int TurnGeneration,
    Guid ResponseId,
    AgentTrigger Trigger,
    AgentDecision Decision,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record TimerElapsedReceived(
    EventContext Context,
    string Kind,
    int Generation,
    Guid? UtteranceId) : SessionInput(Context);

public sealed record AttachReceived(EventContext Context) : SessionInput(Context);

public sealed record DetachReceived(EventContext Context) : SessionInput(Context);

public sealed record SetModeReceived(EventContext Context, SessionMode Mode) : SessionInput(Context);

public sealed record ModelResultReceived(
    EventContext Context,
    Guid ResponseId,
    ModelGenerationEvent Event,
    TaskCompletionSource Processed) : SessionInput(Context);

public sealed record CancelResponseReceived(EventContext Context, Guid ResponseId) : SessionInput(Context);

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
    int TextEndExclusive) : SessionInput(Context);

public sealed record ResponseReceiptReceived(
    EventContext Context,
    Guid ResponseId,
    int TextEndExclusive) : SessionInput(Context);

public sealed record EndSessionReceived(EventContext Context) : SessionInput(Context);

public sealed record EnvironmentReceived(EventContext Context, EnvironmentEvent Event) : SessionInput(Context);

public sealed record SessionOutput(EventContext Context, Guid? ResponseId, OutputPayload Payload);

public abstract record OutputPayload;

public sealed record PublicAgentDescriptor(
    string Id,
    int Version,
    string Name,
    string Role,
    string Description,
    bool VoiceAvailable);

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
    DateTimeOffset CreatedAt);

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
    Guid? ActiveResponseId);

public sealed record ReadyOutput(SessionReadyProjection Ready) : OutputPayload;

public sealed record ResponseStartedOutput(Guid EntryId, long EntrySequence, string Trigger) : OutputPayload;

public sealed record TextDeltaOutput(int TextStart, string Text) : OutputPayload;

public sealed record TextCompletedOutput(int TextLength) : OutputPayload;

public sealed record AudioFrameOutput(
    long FrameSequence,
    long SampleOffset,
    bool IsFinal,
    byte[] Data) : OutputPayload;

public sealed record ResponseCompletedOutput(
    bool Failed,
    int HeardTextEndExclusive,
    string? InterruptReason = null) : OutputPayload;

public sealed record ResponseInterruptedOutput(string Reason, int HeardTextEndExclusive) : OutputPayload;

public sealed record StateChangedOutput(
    SessionStatus Status,
    SessionMode Mode,
    SessionMode? PendingMode,
    string InputState,
    string OutputState,
    bool Muted,
    Guid? StreamId) : OutputPayload;

public sealed record PlaybackStopOutput(string Reason) : OutputPayload;

public sealed record PlaybackGainOutput(double Gain, int RampMs, Guid? CandidateId) : OutputPayload;

public sealed record TranscriptPartialOutput(Guid UtteranceId, int Revision, string Text) : OutputPayload;

public sealed record TranscriptFinalOutput(Guid UtteranceId, string Text, Guid? EntryId, long? EntrySequence) : OutputPayload;

public sealed record ErrorOutput(
    string Category,
    string Code,
    string SafeMessage,
    bool Fatal,
    TimeSpan? RetryAfter) : OutputPayload;

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
        var heard = Math.Min(entry.HeardTextEndExclusive, text.Length);
        var received = Math.Min(entry.ReceivedTextEndExclusive, text.Length);
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
            entry.CreatedAt);
    }

    public static PublicAgentDescriptor FromDefinition(AgentDefinition definition, bool voiceAvailable) =>
        new(
            definition.Id,
            definition.Version,
            definition.Identity.Name,
            definition.Identity.Role,
            definition.Identity.Description,
            voiceAvailable);
}
