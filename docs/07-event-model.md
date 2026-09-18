# Event Model

## Three separate contracts

| Family | Owner | Purpose |
| --- | --- | --- |
| Internal application | Application | Mailbox commands/results with runtime ownership and causal identity |
| Wire/realtime | Contracts, mapped in Api | Stable browser protocol v1; exact fields in [Protocol](14-api-and-realtime-protocol.md) |
| Normalized provider | Application ports, produced by Infrastructure | Request-scoped model/speech events in [Interfaces](04-backend-interfaces.md) |

Vendor SSE objects are a fourth, private adapter detail. Do not serialize domain records directly or create one universal DTO hierarchy. MVP provider events come from the independently composed STT, text Language Model and TTS ports. Audio input and TTS output use separate binary data paths, not one domain event per buffer. Native realtime events are reserved future contracts and are not routed in MVP.

## Internal taxonomy

```csharp
public sealed record EventContext(Guid EventId, Guid SessionId, Guid Epoch,
    DateTimeOffset Timestamp, Guid CorrelationId, Guid? CausationId);
public abstract record SessionInput(EventContext Context);
public sealed record UserTextReceived(EventContext Context, string Text,
    UserTextBehavior Behavior = UserTextBehavior.Interrupt)
    : SessionInput(Context);
public sealed record RecognitionReceived(EventContext Context,
    SpeechRecognitionEvent Event) : SessionInput(Context);
public sealed record ModelResultReceived(EventContext Context, Guid ResponseId,
    ModelGenerationEvent Event) : SessionInput(Context);
public sealed record InterruptionConfirmed(EventContext Context,
    Guid ResponseId, Guid UtteranceId) : SessionInput(Context);
public sealed record InitiativeTriggered(EventContext Context, AgentTrigger Trigger,
    long TimerGeneration) : SessionInput(Context);
public sealed record SessionOutput(EventContext Context, Guid? ResponseId,
    OutputPayload Payload);
public abstract record OutputPayload;
public sealed record PublicAgentDescriptor(string Id, int Version, string Name,
    string Role, string Description, bool VoiceAvailable, string Language = "en");
public sealed record PublicHistoryEntry(Guid EntryId, long Sequence,
    Guid? SourceEventId, ConversationRole Role, string Text, Guid? ResponseId,
    EntryStatus Status, int HeardTextEndExclusive, int ReceivedTextEndExclusive,
    SessionMode DeliveryMode, DateTimeOffset CreatedAt,
    IReadOnlyList<PublicResponseBlock> Blocks);
public sealed record SessionReadyProjection(
    SessionMode Mode, SessionMode? PendingMode, SessionStatus Status,
    PublicAgentDescriptor Agent, Guid? StreamId, AudioFormat? AudioFormat,
    RecognitionCapabilities Recognition, SynthesisCapabilities Synthesis,
    string BargeInPolicy, long LastEntrySequence,
    IReadOnlyList<PublicHistoryEntry> History, Guid? ActiveResponseId);
public sealed record ReadyOutput(SessionReadyProjection Ready) : OutputPayload;
public sealed record TranscriptOutput(Guid UtteranceId, bool IsFinal,
    int? Revision, string Text, Guid? EntryId, long? EntrySequence) : OutputPayload;
public sealed record ResponseStartedOutput(Guid EntryId, long EntrySequence,
    TriggerKind Trigger) : OutputPayload;
public sealed record TextDeltaOutput(int TextStart, string Text) : OutputPayload;
public sealed record TextCompletedOutput(int TextLength) : OutputPayload;
public sealed record SpeechOutputSegmentOutput(int SegmentIndex, int TextStart,
    string Text, string VoiceHint, string Language, double SpeakingRate)
    : OutputPayload;
public sealed record SpeechOutputCompletedOutput(int TextEndExclusive)
    : OutputPayload;
public sealed record PlaybackStopOutput(string Reason) : OutputPayload;
public sealed record PlaybackGainOutput(Guid CandidateId, double Gain,
    int RampMs) : OutputPayload;
public sealed record ResponseInterruptedOutput(string Reason,
    int HeardTextEndExclusive) : OutputPayload;
public sealed record ResponseCompletedOutput(bool Failed,
    int HeardTextEndExclusive) : OutputPayload;
public sealed record StateChangedOutput(SessionStatus Status, SessionMode Mode,
    SessionMode? PendingMode, string InputState, string OutputState, bool Muted,
    Guid? StreamId) : OutputPayload;
public sealed record ErrorOutput(string Category, string Code, string SafeMessage,
    bool Fatal, TimeSpan? RetryAfter) : OutputPayload;
```

This is the closed application output family. History entries in the projection already apply public received-prefix rules and include deliveryMode; they must not contain Agent system instructions, provider configuration, credentials, session summary, unreceived generated assistant tails or internal runtime fields. Persistence `SessionSnapshot` remains a separate store contract. The string reason/state/code vocabularies are restricted to the [controller states](05-interaction-controller.md#state-representation) and [protocol inventory](14-api-and-realtime-protocol.md#server-events); they are not extensible arbitrary metadata. Api maps these records to distinct Contracts DTOs. `ReadyOutput` is already a safe Application projection. The API must not receive a full `SessionSnapshot` and remember to strip fields. It does not serialize SessionSnapshot, Agent Definition internals or ProviderFailure directly. ResponseId is required for response/text/playback payloads and null for unscoped payloads.

Audio uses a separate response-tagged queue exposed through this additional application port, implemented by Api alongside ISessionOutput:

```csharp
public sealed record ResponseAudio(Guid SessionId, Guid ResponseId,
    long FrameSequence, long SampleOffset, bool IsFinal, ReadOnlyMemory<byte> Data);
public interface ISessionAudioOutput
{
    ValueTask PublishAsync(ResponseAudio audio, CancellationToken cancellationToken = default);
}
```

The response audio coordinator is a supervised worker: it owns only media sequence/sample counters and immutable captured response identity. It publishes audio through ISessionAudioOutput and feeds segment/timing/progress metadata back into the mailbox. It never changes conversational state. It reads an immutable output-authorization snapshot atomically published by the mailbox owner; Api's sender applies the same identity check before sending. Queued buffers are owned copies. Admission into bounded output queues waits in workers, never inside the mailbox loop. Model pump advances only after its previous delta has been processed and downstream admission has credit; this supplies bounded backpressure without blocking interruption processing. Cancellation releases pending credit waiters.

Required additional mailbox cases and their owned data:

| Input | Required data beyond EventContext |
| --- | --- |
| Attach/Detach/EndRequested | connection lease ID; lastServerSequence cursor; end reason |
| ModeSetRequested | requested SessionMode |
| UserSpeechStarted/UserSpeechEnded | utteranceId, activityScore/duration observation |
| PartialTranscriptReceived/FinalTranscriptReceived | utteranceId, revision for partial, text/confidence (mapped RecognitionReceived) |
| InterruptionCandidate/ClassifierReturned | candidateId, utteranceId, captured responseId, evidence revision, decision |
| BrainReturned | turnGeneration, candidate responseId, AgentDecision |
| TtsTimingReceived/TtsSegmentCompleted/Failed | responseId, segmentIndex, text/sample timing marks, sample duration or normalized failure |
| PlaybackObserved | responseId, consumedSamples, phase, acknowledged text offset |
| EnvironmentReceived | eventId, allowlisted kind and validated string data (from IEnvironmentEventIngress) |
| TimerElapsed | timer kind, timer generation |
| PersistenceCompleted/Failed | write revision, normalized failure |
| VoiceFailed | streamId, normalized audio/recognition failure |

## Ordering, causality and routing

EventContext is stamped at **application ingress** using TimeProvider and IIdGenerator. Clients do not supply `correlationId` or `causationId`. Root `CorrelationId` is a server mapping decision: it **may** use the accepted client `eventId` as the correlation seed for that user command, or a newly generated ID; descendants retain it. `CausationId` identifies the immediate known parent or is null for roots. Client `timestamp`, when present, is advisory only and never becomes EventContext.Timestamp. The mailbox reader assigns a separate internal long sequence on dequeue; this is diagnostic order, not wire sequence. Provider callbacks have no authority to choose sequence, correlation, causation or current response identity.

One user command can produce many internal and wire events. Map them explicitly, preserve correlation, generate distinct eventIds, and set causation to the producing event. Wire output order and client retries have separate counters defined in the protocol. No promise of cross-session ordering or durable event replay exists. Persist business history/checkpoints, not the full event timeline.

Synthetic environment input is a typed in-process fixture calling `IEnvironmentEventIngress`, not a public arbitrary-event endpoint. Initially permit `order_status_changed` with `orderReference` and status `shipped|delayed|delivered`, and `unfinished_interaction` with a short topic (empty topic explicitly clears pending state). Unknown kinds are rejected. It is data for policy evaluation, not executable instructions.

## Stale results

Every operation captures epoch, responseId/utteranceId and logical generation before starting. A response can transition Live→Completed, Live→Failed or Live→Superseded exactly once. A terminal response cannot transition back. A Superseded Response cannot create new Speech Segments/TTS jobs. Late model text, audio, completions and browser playback events are discarded for state/history/context; diagnostic stop-latency recording never mutates the conversation. Cancellation requests are not terminal evidence by themselves; state is marked first. Late worker terminal events are consumed for cleanup but produce no new visible output. [Architecture](03-system-architecture.md#invariants) is the authoritative invariant list.

Keep a bounded 500-event developer timeline per runtime without raw audio or conversation text by default. It is a diagnostic projection of events, not a broker or persistence requirement.

## Post-MVP planned until verified

Observed: catalog lifecycle; pending/bound attachment bind; off-mailbox extraction with `processingAttachments`; off-mailbox typed tools with `runningTools` and epoch/response rejection of late results; rich envelope parse (`[[speech:]]`, `[[md:]]`, `[[artifact:]]`, unknown fallback), `BlockUpsert` live visibility, independent display vs speech receipts (late receipts after supersession/disconnect do not revise the parent); optional persisted `SpeechText` only for model `[[speech:]]` or runtime deterministic lead-in, not markdown-only display stripping; StaySilent/Speak/RequestDeactivate with definition-owned consecutive/silence/silent-evaluation bounds and runtime deactivation (Paused, new epoch, not archive/delete); lazy session workspace provision with RO `/agent`/`/attachments` and RW `/workspace`; explicit attachment materialize into artifacts with preserved hash; `sandbox.run` tool JSON (`ok`, `exitCode`, `output`, `artifactId`, `message`) after a completed sandbox. There is no WorkItem event stream. Do not log raw attachment bytes or host sandbox transcripts.
