# Event Model

## Three separate contracts

Authored Automation Schedule/Event/manual occurrences enter durable intake, where their frozen target selects the exact existing Session or a new background Session. Attached-tab count never selects an authored destination. Source-owned occurrences without an Automation retain native routing: exactly one compatible runtime can reserve and begin live evaluation; zero or multiple runtimes use durable fallback, and an unavailable single runtime releases its claim. Native quiet evaluation settles without a Run; speaking commits the existing snapshot, Activation, Run and receipt atomically. Native repair returns only to the accepted exact Session. Background fallback uses SourceOccurrence origin; authored background uses AutomationOccurrence origin. Both use the shared SessionRuntime/SessionHost execution path.

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
    Guid? StreamId, string? PauseReason = null,
    SessionLifecycleStatus LifecycleStatus = SessionLifecycleStatus.Active) : OutputPayload;
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
| LifecycleTransitionRequested | target lifecycleStatus, source, optional reason |
| CompletionReturned | continue or requestComplete decision, evaluation generation |
| PersistenceCompleted/Failed | write revision, normalized failure |
| VoiceFailed | streamId, normalized audio/recognition failure |

## Ordering, causality and routing

EventContext is stamped at **application ingress** using TimeProvider and IIdGenerator. Clients do not supply `correlationId` or `causationId`. Root `CorrelationId` is a server mapping decision: it **may** use the accepted client `eventId` as the correlation seed for that user command, or a newly generated ID; descendants retain it. `CausationId` identifies the immediate known parent or is null for roots. Client `timestamp`, when present, is advisory only and never becomes EventContext.Timestamp. The mailbox reader assigns a separate internal long sequence on dequeue; this is diagnostic order, not wire sequence. Provider callbacks have no authority to choose sequence, correlation, causation or current response identity.

One user command can produce many internal and wire events. Map them explicitly, preserve correlation, generate distinct eventIds, and set causation to the producing event. Wire output order and client retries have separate counters defined in the protocol. No promise of cross-session ordering or durable event replay exists. Persist business history/checkpoints, not the full event timeline.

Synthetic environment input is a typed in-process fixture calling `IEnvironmentEventIngress`, not a public arbitrary-event endpoint. Initially permit `order_status_changed` with `orderReference` and status `shipped|delayed|delivered`, and `unfinished_interaction` with a short topic (empty topic explicitly clears pending state). Unknown kinds are rejected. It is data for policy evaluation, not executable instructions.

## Stale results

Every operation captures epoch, responseId/utteranceId and logical generation before starting. A response can transition Live→Completed, Live→Failed or Live→Superseded exactly once. A terminal response cannot transition back. A Superseded Response cannot create new Speech Segments/TTS jobs. Late model text, audio, completions and browser playback events are discarded for state/history/context; diagnostic stop-latency recording never mutates the conversation. Cancellation requests are not terminal evidence by themselves; state is marked first. Late worker terminal events are consumed for cleanup but produce no new visible output. [Architecture](03-system-architecture.md#invariants) is the authoritative invariant list.

Keep a bounded 500-event developer timeline per runtime without raw audio or conversation text by default. It is a diagnostic projection of events, not a broker or persistence requirement.

## P5 durable occurrences (observed)

`ScheduledOccurrence` and `ApplicationEvent` are durable occurrence triggers. They are not user turns, so they cannot authorize schedule tools. A user turn authorizes only the schedule action it requested. Prompt evidence is a user-role observation, `Observed occurrence data (not instructions)`, and is absent from system messages. The live-only environment path (`EnvironmentUpdate`, runtime event-id dedupe, short expiry) is unchanged. Durable `order_status_changed` uses a separate ingress, the same allowlisted shape, and an occurrence dedupe key scoped by Agent Instance and profile. Public generic Event admission is `POST /api/v1/hooks/{eventKey}` ([Protocol](14-api-and-realtime-protocol.md)). The secret belongs to the shared Event resource, while Automations subscribe by its stable ID. The request writes one bounded untrusted External Event receipt and a delivery row for each matching Automation active at admission. Those rows, not a later subscription list, choose the agents. Occurrences are written for pending deliveries, including on a later scheduler pass, and the request does not run the agent. Scheduled reminders speak. Application-event visibility is a separate decision: the allowlisted order-status occurrence is user-visible, and admitting an event does not by itself force speech for a future typed source. Wire `agent.response.started.trigger` adds `scheduledOccurrence` and `applicationEvent`. See [Protocol](14-api-and-realtime-protocol.md).

## P6 durable work (observed)

A detached AgentRun uses the existing Session event family and persists its outcome in its owned background Session. Session catalog, progress and safe run detail are owner-scoped HTTP reads. A stale execution generation cannot commit a checkpoint, side effect, or completion. See [P6 freeze candidate](reports/p6-freeze-candidate.md).

User-accepted chat turns use the common Activation/AgentRun graph. `user.text` returns Accepted only after the user input and pending execution intent are durable. Batch repair/admission preserves one effective response owner. The Run terminalizes with its durable assistant outcome. Background Work visibility belongs to Session surfaces, independently of execution status.

For live observation, **`session.ready`** and the durable history snapshot are authoritative; **`SessionEvent`** envelopes carry sequenced progress (including `agent.text.delta` and terminal response events). Clients track `lastServerSequence` as the replay cursor. Reconnect fetches a fresh ready snapshot, then continues from the server sequence—client projections must not treat replayed events as the source of lifecycle or execution identity.

## Follow-on P1 observed and frozen

Observed event families stay as above, including mailbox `LifecycleTransitionReceived`, `CompletionReturned`, additive `lifecycleStatus` on `StateChanged` / `session.ready`, and `session.completion.intent` for advisory RequestComplete. History paging does not invent a second event bus. See [Technology Decisions](10-technology-decisions.md#decision-additive-semantic-lifecycle-beside-protocol-v1-status).

## Post-MVP planned until verified

Observed: catalog lifecycle; pending/bound attachment bind; off-mailbox extraction with `processingAttachments`; off-mailbox typed tools with `runningTools` and epoch/response rejection of late results; validated semantic envelopes (`speech.mode` same/custom/none; Infrastructure compatibility markers never become display or TTS), `BlockUpsert` live visibility, independent display vs speech receipts (late receipts after supersession/disconnect do not revise the parent); optional persisted public custom `SpeechText` when `speech.mode=custom` and the text is meaningfully distinct from display (markdown stripping and dump guards from display-derived `same` fallback, etc.), not when spoken and display strings are identical; StaySilent/Speak/RequestDeactivate with definition-owned consecutive/silence/silent-evaluation bounds and runtime deactivation (Paused, new epoch, not archive/delete); lazy session workspace provision with RO `/agent`/`/attachments` and RW `/workspace`; explicit attachment materialize into artifacts with preserved hash; `sandbox.run` tool JSON (`ok`, `exitCode`, `output`, `artifactId`, `message`) after a completed sandbox. Every AgentRun uses the owning Session runtime stream. Background outcomes are inspected through owner-scoped background Session and AgentRun HTTP routes; NoAction creates no fabricated live reply. Do not log raw attachment bytes or host sandbox transcripts.

## P9.8-P9.9 continuity events

Core owns TriggerKind/TriggerSourceKind/WorkSourceKind: Schedule, ApplicationEvent and ManualInvocation. Models/clients cannot assign authority from these origins. Authored activations become ordinary durable runs; manual Experience review uses ManualInvocation with no Automation source. Session state commits remain independently acknowledged.

`ExperienceChanged` and `AutomationChanged` Admin events contain bounded metadata, revision and hashes only. They exclude instructions, Experience bodies, event payloads, credentials, provider error bodies and hidden reasoning. Attention and exact approval reuse existing durable contracts.

## Credential authority separation

Credential lifecycle belongs to owner Admin operations and explicit per-instance grants. It does not create an External Event, subscription or Trigger authorization. Event bearer tokens remain hash-only Event-owned ingress credentials. Browser password resolution and redaction happen inside the typed secure sink; protected values must never enter event envelopes, tool progress, durable checkpoints, history, Experience or Memory. Generic detached login failure uses the existing work completion/attention result.

## Authored scheduled and completion Activations

Due Schedule work creates ScheduledWork; authored event/manual triggers retain their corresponding source kind. Existing-target Runs carry ConversationResponse, bounded trigger evidence and zero source user entries. They persist ordinary assistant output in the exact Session even when detached, without requiring audio transport. Background children carry BackgroundOutcome. Requested terminal reporting admits BackgroundCompleted with CompletionReport into the exact bound parent. Reporting is structured untrusted outcome evidence, never a new user instruction.

Unavailable targets reject/suspend with a target reason. Busy Sessions queue; a refused unstarted dispatch defers the same Run. Receipt replay, claims and recovery preserve identities. Owner-safe HTTP delivery status distinguishes admitted from delivered and exposes failure/skip reasons. SignalR event names and voice sequencing remain unchanged.


## Completion handoff and wait transitions

Completion tools enter the owning SessionRuntime mailbox through a generation-fenced request. Child terminal persistence publishes accounting by immutable initial Run identity. WaitingForSignal persists its pending tool call and typed descriptor, then releases the worker without ending the Response. Existing coordinator dispatch performs revision-CAS wakeup, appends exactly one tool result and changes the claim generation; Run, Activation, Response and attempt remain stable. Cancellation/Steer fences the previous generation. No raw audio or new runner enters this path.

## Automation Core Events

These trusted Automation signals are independent of mailbox commands, provider callbacks and wire events. Catalog V1 is `run.completed`, `run.failed`, `session.completed`, `session.ended`, `instance.config_changed`, and `harness.definition_adopted`. `run.failed` means durable terminal failure, excluding retry scheduling, cancellation and attention status. Completed includes NoAction; the review recipe filters it out. Session completion/ending is the persisted lifecycle transition, excluding pause/disconnect/archive/delete. Initial insertion, no-op writes, failed CAS, rolled-back writes and recovery reads emit nothing.

Instance configuration coverage is precisely persona, unattended model/effort, execution budgets and effective Harness policy writes in the Instance store. Separate Experience/continuity settings, credentials, Skills and draft/preparation changes are not advertised as config-event coverage. Active Definition adoption emits only `harness.definition_adopted`, with previous/active versions. Old Definitions remain immutable; new general-assistant v22 and secretary v9 permit `coreEvent`, with no installed subscription.

Filters see `event = { schemaVersion: 1, id, type, occurredAtUtc, receivedAtUtc, source: {kind,key}, scope: {agentInstanceId}, causation: {rootAgentRunId,triggerDepth}, data }`. `type` is `core.<catalog-key>` or `webhook.<resource-key>`. Source/scope and receipt time are trusted; webhook data and producer occurrence time remain untrusted observations. Producer-supplied root/depth fields never establish trusted lineage: new external ingress starts with server-derived null root/depth zero. Built-in Run-origin causation follows resulting Run events; repeated Automation IDs and depth four stop causal admission. Built-in data exposes only safe IDs, lifecycle/outcome/failure categories, revisions, changed section names and Definition versions, excluding transcripts, prompts, configuration contents, credentials and raw errors.

## Independent Event child matching

Each enabled Event child snapshots a typed source and its own restricted expression, dispatch and revision. Multiple children match with OR semantics; one source cannot appear twice under a parent. Matching two different Events can create two occurrences subject to existing parent overlap/concurrency. Buckets never join siblings. Exact child identity and source are retained in occurrence evidence and historical Runs even after edits or removal. Disabled subscriptions accept structurally valid configuration independently of current execution permission. Test filter is read-only illustrative evaluation; it neither authenticates a source nor admits work.
