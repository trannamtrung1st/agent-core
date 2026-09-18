# Backend Interfaces

These C# 14 signatures are the implementation contract, not source files. Ports and normalized provider records live in Application; definition/conversation records live in Domain as specified in [Backend Implementation](12-backend-implementation-spec.md). Use BCL `System`, `System.Collections.Generic`, `System.Threading`, and `System.Threading.Tasks` namespaces. Collections passed to workers must be immutable snapshots, even where typed as `IReadOnlyList<T>`.

## Portable capabilities and concrete configurations

ILanguageModel, ISpeechRecognizer and ISpeechSynthesizer are independent portable capabilities. A conversational agent always requires a language-model alias. Speech recognizer and synthesizer aliases are required only when that Agent Definition has `Voice.Enabled`. MVP voice orchestration composes all three ports; text-only agents compose ILanguageModel alone. No IAIProvider, OpenRouter-specific port or native-audio reasoning dependency is introduced. Each agent selects logical provider aliases, allowing different text models by identity. Infrastructure resolves those aliases to hosted, hybrid or local configurations. The recommended hosted text alias resolves to OpenAICompatibleLanguageModel configured for OpenRouter; the same adapter can target direct OpenAI or a compatible local server. The recommended future hosted STT adapter is OpenAiSpeechRecognizer over an OpenAI **realtime transcription** session (speech-to-text only; not native speech-to-speech reasoning), with configurable model and default recommendation `gpt-live-transcribe`. Until that session is implemented, the selectable hosted STT adapter is `OpenAICompatibleBatch`. See [Configuration](15-persistence-and-configuration.md#hosted-and-on-prem-provider-configurations) and [OpenAI realtime STT](12-backend-implementation-spec.md#openai-realtime-transcription-adapter).

### Speech provider replacement rule

Changing the STT, LLM or TTS provider must not require changes to Agent Runtime or Interaction Controller. Provider selection uses application configuration and dependency injection; concrete adapters belong in Infrastructure. Observed selectable hosted speech is OpenAI TTS plus `OpenAICompatibleBatch` STT. OpenAI realtime transcription remains a planned adapter, not a selectable runtime path, while its live session is a no-op.

```text
ISpeechRecognizer                         ISpeechSynthesizer
├── OpenAICompatibleBatchSpeechRecognizer ├── OpenAiSpeechSynthesizer
├── SyntheticSpeechRecognizer             ├── SyntheticSpeechSynthesizer
├── OpenAiSpeechRecognizer (unselectable) ├── LocalSpeechSynthesizer (future)
├── LocalSpeechRecognizer (future)        └── FutureHostedSpeechSynthesizer
└── FutureHostedSpeechRecognizer
```

Browser is not a leaf of those ports. Selecting Adapter=`Browser` maps to client transports (`clientTranscript` / `clientSpeech`) with no backend recognizer or synthesizer. Do not register a stand-in `ISpeechRecognizer` that pretends to be the Web Speech API. An Infrastructure `SpeechFactory` (same responsibility as `LanguageModelFactory`) resolves an `EffectiveSpeechPlan` plus optional ports. Browser transports still carry explicit **effective client capabilities** on that plan (`PartialTranscripts=true` and `SpeechBoundaryEvents=true` for `clientTranscript`; `Cancellation`, `VoiceSelection` and `SpeakingRate` for `clientSpeech`; `StreamingAudio=false` because no backend PCM port exists). Session Runtime uses those plan capabilities for interruption timing and `session.ready`; it must not treat a missing backend adapter as all-false capabilities. Selected non-Synthetic adapters must not silently become Synthetic. `OpenAiSpeechSynthesizer` is selectable when Synthesis Adapter=`OpenAI` and a backend API key is structurally present. `OpenAICompatibleBatch` is the supported hosted STT adapter when Recognition Adapter=`OpenAICompatibleBatch` and a backend API key is present; it reports no streaming input and no interim partials. `OpenAiSpeechRecognizer` remains unimplemented as a live session and is not a selectable runtime adapter.

Future names illustrate replaceable implementations, not mandatory projects/providers. Adapters normalize vendor behavior and payloads; no OpenAI request/response type crosses into Domain, Application, Agent Runtime, Interaction Controller or wire contracts. Capability differences select the existing [fallback policies](05-interaction-controller.md#stt-capability-fallback-and-local-ducking), not a runtime rewrite. STT `SpeechPartial`/`SpeechFinal` Confidence remains optional (null when unavailable); streaming input, partials, boundaries and cancellation must be reported honestly as **effective adapter capabilities**, never as operator-invented flags.

SyntheticSpeechRecognizer, SyntheticSpeechSynthesizer and ScriptedLanguageModel remain mandatory for offline unit/conversation tests, frontend development, CI, latency simulation, cancellation and interruption tests. They require no API keys. [Configuration](15-persistence-and-configuration.md#provider-selection-and-di) owns adapter selection examples.

## Language model and failures

```csharp
public enum ProviderErrorCode
{
    Authentication, RateLimited, Timeout, Cancelled, InvalidRequest,
    Unavailable, UnsupportedCapability, Unknown
}
public sealed record ProviderFailure(
    ProviderErrorCode Code, string SafeMessage, TimeSpan? RetryAfter = null);
public enum ModelRole { System, User, Assistant, Tool }
public enum ModelStopReason { Completed, LengthLimit, ContentFiltered, ToolCalls }
public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ModelToolDefinition(string Name, string Description, string ParametersJson);
public sealed record ModelMessage(
    ModelRole Role, string Text,
    IReadOnlyList<ModelContentPart>? Parts = null,
    string? ToolCallId = null, string? Name = null,
    IReadOnlyList<ModelToolCall>? ToolCalls = null);
public sealed record ModelCapabilities(
    bool StreamingText, bool Cancellation, bool Vision = false, bool Tools = false);
public sealed record ModelRequest(
    Guid ResponseId, IReadOnlyList<ModelMessage> Messages,
    int MaxOutputTokens = 512, double? Temperature = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null);
public abstract record ModelGenerationEvent;
public sealed record ModelTextDelta(string Text) : ModelGenerationEvent;
public sealed record ModelToolCallEvent(ModelToolCall Call) : ModelGenerationEvent;
public sealed record ModelCompleted(ModelStopReason Reason,
    int? InputTokens = null, int? OutputTokens = null) : ModelGenerationEvent;
public sealed record ModelFailed(ProviderFailure Failure) : ModelGenerationEvent;
public interface ILanguageModel
{
    ModelCapabilities Capabilities { get; }
    IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request, CancellationToken cancellationToken = default);
}
```

Exactly one terminal Completed/Failed event per successful enumeration, followed by EOF. Caller cancellation may throw `OperationCanceledException` instead. Unexpected exceptions are normalized at the supervised application boundary; provider exception details never go to Contracts. Empty EOF is `Unavailable`, never implicit success. Model events have request-scoped identity; the pump adds ResponseId from its captured request, never from current mutable state. Tool calls and structured-output generation are outside MVP; unsupported requested capabilities fail before making a request.

The Language Model reasons over normalized text messages only. ModelRequest has no vendor model ID; the configured adapter instance owns DefaultModel. Selecting another model means selecting/configuring a logical provider alias, never inspecting OpenRouter model IDs in Agent Runtime. Hidden provider reasoning fields are not spoken content and never become ModelTextDelta.

## Independent speech ports

```csharp
public sealed record AudioFormat(string Encoding, int SampleRateHz, int Channels);
public sealed record AudioFrame(long FrameSequence, long SampleOffset,
    ReadOnlyMemory<byte> Data);
public sealed record RecognitionCapabilities(bool StreamingAudio,
    bool PartialTranscripts, bool SpeechBoundaryEvents, bool Cancellation);
public sealed record SynthesisCapabilities(bool StreamingAudio, bool TimingMarks,
    bool Cancellation, bool VoiceSelection, bool SpeakingRate,
    IReadOnlyList<AudioFormat> SupportedFormats);
public sealed record RecognitionOptions(AudioFormat Format, string Language);
public abstract record SpeechRecognitionEvent(Guid UtteranceId);
public sealed record SpeechStarted(Guid UtteranceId) : SpeechRecognitionEvent(UtteranceId);
public sealed record SpeechPartial(Guid UtteranceId, int Revision, string Text,
    double? Confidence) : SpeechRecognitionEvent(UtteranceId);
public sealed record SpeechFinal(Guid UtteranceId, string Text,
    double? Confidence) : SpeechRecognitionEvent(UtteranceId);
public sealed record SpeechEnded(Guid UtteranceId) : SpeechRecognitionEvent(UtteranceId);
public sealed record RecognitionFailed(Guid UtteranceId, ProviderFailure Failure)
    : SpeechRecognitionEvent(UtteranceId);
public enum SpeechBoundary { Started, Ended }
public interface ISpeechRecognizer
{
    RecognitionCapabilities Capabilities { get; }
    ValueTask<ISpeechRecognitionSession> OpenAsync(RecognitionOptions options,
        CancellationToken cancellationToken = default);
}
public interface ISpeechRecognitionSession : IAsyncDisposable
{
    ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    ValueTask ObserveBoundaryAsync(Guid utteranceId, SpeechBoundary boundary,
        CancellationToken cancellationToken = default);
    ValueTask CompleteInputAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
        CancellationToken cancellationToken = default);
}
public sealed record SpeechRequest(Guid ResponseId, int SegmentIndex,
    int TextStart, string Text, string Voice, double SpeakingRate, AudioFormat Format);
public abstract record SpeechSynthesisEvent;
public sealed record SpeechAudio(AudioFrame Frame) : SpeechSynthesisEvent;
public sealed record SpeechTimingMark(int TextEndExclusive, long SampleOffset)
    : SpeechSynthesisEvent;
public sealed record SpeechSynthesisCompleted(long TotalSamples) : SpeechSynthesisEvent;
public sealed record SpeechSynthesisFailed(ProviderFailure Failure) : SpeechSynthesisEvent;
public interface ISpeechSynthesizer
{
    SynthesisCapabilities Capabilities { get; }
    IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(SpeechRequest request,
        CancellationToken cancellationToken = default);
}
```

Canonical AudioFormat is `("pcm_s16le", 24000, 1)`. AudioFrame memory is owned by the producer until awaited push completes; push must copy if it retains the buffer. Yielded synthesis memory must remain valid until the consumer advances enumeration, which copies before queuing. One audio writer and one event reader per recognition session; these may execute concurrently. Boundary observations share the ordered audio writer, not a parallel call into the recognizer. `CompleteInputAsync` half-closes audio and drains finals; disposal cancels and releases all resources and is idempotent. Cancellation of OpenAsync creates no usable session.

Browser VAD assigns an utteranceId through `crypto.randomUUID()` (an injectable deterministic browser ID factory in tests). That application `utteranceId` is authoritative. The STT adapter binds the current provider transcription item (for OpenAI realtime transcription, `item_id`) to the active utteranceId when browser `ObserveBoundaryAsync(Started)` is observed, and commits that item on `Ended` (OpenAI `input_audio_buffer.commit` when provider turn detection is disabled). Provider transcript deltas for that item become `SpeechPartial`; the committed/completed transcript becomes `SpeechFinal`. If the provider emits an item before browser Started, buffer it until the browser ID exists. Browser VAD / Interaction Controller remain authoritative for conversation semantics. Provider-native turn detection must not autonomously create Agent responses: if a concrete API requires turn-detection events, map them only as speech-activity evidence (optional `SpeechStarted`/`SpeechEnded` hints). They never authorize `Speak`, never allocate a response, and never override browser utterance segmentation. The future native speech-to-speech mode owns its own ID mapping instead. Revisions increase per utterance; one final wins and later corrections or duplicate finals are ignored. Boundary events carry no audio.

STT adapter lifecycle (streaming hosted path): open a provider transcription session; configure canonical PCM16 mono 24 kHz input; continuously forward admitted PCM; map transcript deltas to `SpeechPartial`; map committed/final transcript to `SpeechFinal`; map provider errors to `RecognitionFailed`/`ProviderFailure`; cancel/dispose on voice stream end, mode exit to text, detach or disconnect; reconnect by creating a **new** provider session and streamId (never splice PCM). Late partials after the utterance is terminal, timed out, cancelled, or replaced by a newer utteranceId are ignored. A provider final after the 2 s/20 s final-transcript deadline (measured from Ended; not `Voice.MaxUtteranceSeconds`) is ignored. Starting another utterance binds a new utteranceId; unfinished previous provider items are cancelled and cannot emit a second user turn. Recognition cancellation disposes the session and produces no `SpeechFinal` and no user history entry.

StreamingAudio on RecognitionCapabilities means SupportsStreamingInput; PartialTranscripts, SpeechBoundaryEvents and Cancellation describe independently **effective** support. The current hosted MVP/default STT path is `OpenAICompatibleBatch` (no streaming input, no interim partials). Prefer streaming input with partials for the planned, currently **unselectable** `OpenAiSpeechRecognizer` realtime transcription adapter when that live session is implemented; do not require partials from every adapter. Confidence is optional evidence, never a guaranteed comparable score.

`ISpeechRecognizer.Capabilities` / `ISpeechSynthesizer.Capabilities` / `ILanguageModel.Capabilities` are **effective capabilities**: runtime facts returned by the selected adapter instance after it loads. Configuration may declare `RequiredCapabilities` as flags that **must be true** (a subset of what the adapter actually provides). It may set `DisabledCapabilities` to turn off optional adapter features. Do not set `RequiredCapabilities` to false to “require absence”; choose a batch/degraded adapter instead. Built-in adapters (`OpenAI`, `Synthetic`/`Scripted`) define or negotiate their own capabilities. Generic OpenAI-compatible/local adapters whose protocol cannot be auto-discovered may accept operator **capability assertions**; those assertions are validated by Infrastructure contract tests against the declared protocol, and startup still fails if `RequiredCapabilities` exceed what the adapter class can actually do. `session.ready` exposes only effective capabilities.

PushAudioAsync/ObserveBoundaryAsync await bounded local admission only, not a network transcription. A batch adapter runs one supervised transcription at a time and may buffer at most two pending utterances; overflow yields RecognitionFailed(Unavailable) and resets the voice stream. Non-streaming recognizers buffer at most 30 seconds per utterance and submit after Ended; they still implement this session port and advertise no partials. Ended must flush a buffered utterance, while CompleteInput closes the entire voice stream. Capability flags describe underlying quality/latency, not whether the interface exists. Discovery occurs when loading configured adapters, validates formats, and is included as effective capabilities in session.ready. No external capability probing is needed in synthetic mode. Text compatibility does not imply speech support. See [Controller](05-interaction-controller.md) for degraded barge-in.

Public `voiceAvailable` for an agent is:

```text
voiceAvailable =
    AgentDefinition.Voice.Enabled
    && configured STT path is structurally resolvable
    && configured TTS path is structurally resolvable
```

Resolvable includes Synthetic `serverAudio` adapters, Browser `clientTranscript`/`clientSpeech` paths, `OpenAICompatibleBatch` when Recognition Adapter and a backend API key are present, and OpenAI TTS when Synthesis Adapter=`OpenAI` and a backend API key are present. It does not mean “a Synthetic backend port happens to be registered.” Gated adapters (deferred OpenAI realtime STT; hosted names without a structurally present key) are not resolvable. Server-advertised `voiceAvailable` for Browser does not require browser feature detection; clients AND that later.

A backend with a valid text-only configuration must start without STT/TTS adapters. Creating or switching to voice when `voiceAvailable` is false yields typed recoverable `VoiceUnavailable`; the session remains in text mode. Apply the same gate for `session.mode.set` while attached or paused (pending voice is not queued when unavailable). Public availability follows the effective speech plan, not the process profile.

TTS capability discovery also reports VoiceSelection and SpeakingRate. When unsupported, only the configured default voice and rate 1.0 are accepted; an explicitly requested unsupported non-default fails validation rather than pretending it worked. StreamingAudio=false still permits phrase-level synthesis and playback between phrases, never a requirement to wait for the entire agent response. Hosted speech selection is independent of the text gateway; OpenAI speech is the initial hosted selection, not a permanent architectural requirement.

Timing marks use UTF-16 text end offsets relative to the segment and canonical sample offsets relative to that segment. The runtime adds segment offsets to obtain response-wide coordinates. Completion must follow the last audio/mark; all TTS events are tagged by the worker with captured ResponseId and SegmentIndex.

## Agent decisions and interruption

```csharp
public enum InteractionDecision {
    Ignore, Continue, Queue, Interrupt, InjectEvent,
    RequestInterruptionClassification, RequestAgentDecision
}
public sealed record InterruptionContext(Guid ResponseId, Guid UtteranceId,
    string PartialText, string HeardText, TimeSpan SpeechDuration,
    double? ActivityScore, double? TranscriptConfidence, bool HasPartialTranscripts);
public interface IInterruptionClassifier
{
    ValueTask<InteractionDecision> ClassifyAsync(InterruptionContext context,
        CancellationToken cancellationToken = default);
}
public enum TriggerKind { UserTurn, LongSilence, EnvironmentUpdate, UnfinishedInteraction }
public sealed record AgentTrigger(Guid EventId, TriggerKind Kind, string? Text,
    string? EnvironmentKind = null);
public sealed record SessionAttachmentManifestItem(
    Guid AttachmentId, string DisplayName, string ContentType, long UploadedWithEntrySequence);
public sealed record AgentContext(
    AgentDefinition Definition,
    IReadOnlyList<ConversationEntry> History,
    string Summary,
    UserProfile? Profile,
    SessionMode Mode,
    string? PendingTopic,
    bool HelpOfferedDuringSilence,
    string? InterruptedHeardText,
    AgentTrigger Trigger,
    IReadOnlyList<AttachmentProcessResult>? AttachmentContents = null,
    IReadOnlyList<SessionAttachmentManifestItem>? SessionAttachments = null,
    int ConsecutiveProactiveSpeaks = 0,
    int SilentEvaluations = 0,
    int SpeaksThisSilencePeriod = 0,
    bool InitiativeHeld = false,
    bool InactivityExceeded = false,
    bool ModelSupportsTools = true,
    DateTimeOffset UtcNow = default,
    DateTimeOffset? LastUserActivityAt = null);
public enum InitiativeIntent { Hint, Rephrase, Clarification, Reminder, FollowUp, Other }
public sealed record InitiativePlan
{
    public InitiativeIntent Intent { get; }
    public string PlannerNote { get; }
    public static bool TryCreate(string? intentWire, string? plannerNote, out InitiativePlan? plan);
    public static InitiativePlan Create(InitiativeIntent intent, string plannerNote);
}
public abstract record AgentDecision;
public sealed record StaySilent(string Reason, bool CountsTowardSilentCap = true,
    int? NextWaitMs = null) : AgentDecision;
public sealed record Speak(
    ModelRequest Request,
    int? NextWaitMs = null,
    InitiativePlan? Plan = null) : AgentDecision;
public sealed record RequestDeactivate(string Reason) : AgentDecision;
public interface IAgentBrain
{
    ValueTask<AgentDecision> DecideAsync(AgentContext context, Guid responseId,
        CancellationToken cancellationToken = default);
}
public interface IIdGenerator
{
    Guid NewId();
    Guid NewSessionId();
}
```

`RequestInterruptionClassification` invokes `IInterruptionClassifier` only. `RequestAgentDecision` invokes `IAgentBrain` only. Do not call AgentBrain to classify microphone events. `InterruptionContext.ActivityScore` is the browser VAD observation; `TranscriptConfidence` is optional STT evidence (null when the adapter does not provide it). IAgentBrain is an application policy/context-builder boundary. User turns build a normalized ModelRequest directly. Proactive triggers run hard gates first, then `IInitiativeEvaluator` (`initiative-decision-v2` JSON on a dedicated `ILanguageModel` instance); only a well-formed `Speak` (required `intent` and `objective`) proceeds to the full generation request on the foreground model. Malformed `speak` plans fail closed to `StaySilent` with `CountsTowardSilentCap=false` (`invalid_plan`). Generation applies trusted server-owned `intent` (`InitiativeIntent` / `InitiativePlan.TryCreate`) as fixed framework instructions; planner `objective` is untrusted `initiative_planner_observation` JSON only. That JSON is delivered as a final `ModelRole.User` message after transcript turns so adapters stay provider-shaped; it is framework-owned internal context, not a human user turn—do not treat it as the latest conversational request. Session Runtime cancels in-flight brain evaluation on user activity, supersession, detach, and pause; initiative failures fail closed to `StaySilent`. Decisions must pass the same runtime policy recheck. `StaySilent.CountsTowardSilentCap=false` marks hard-gate denials that must not advance silent-evaluation pause. Allocate a candidate response ID before deciding; only Speak makes it live and emits `agent.response.started`. StaySilent allocates no visible response. RequestDeactivate cancels live output, rotates the runtime epoch, persists Paused, and is not archive or v1 end. Role `environment.toolAllowlist` is runtime-enforced (`RolePermissions`); `process`/`shell` stay denied. Approved knowledge retrieval returns identity, title, citation, and current file body under the pinned source identity (`GET /api/v2/sessions/{id}/knowledge/{identity}`). Classifier defaults to deterministic heuristics; optional model fallback is bounded by [Controller](05-interaction-controller.md).

Use injected `TimeProvider` for UTC timestamps, monotonic elapsed time, and timers (`Task.Delay(delay, timeProvider, token)` or `CreateTimer`). Do not define IClock. Production NewId calls `Guid.CreateVersion7(timeProvider.GetUtcNow())`; NewSessionId calls `Guid.NewGuid()` for cryptographically random UUIDv4 local/demo bearer session IDs. Tests use reproducible sequences for both. IDs are serialized as strings at browser boundaries.

## Persistence, definitions and output

```csharp
public interface IAgentDefinitionStore
{
    ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<AgentDefinition?> GetAsync(string id, int? version = null,
        CancellationToken cancellationToken = default);
}
public interface IMemoryStore
{
    ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<SessionSnapshot?> LoadMetadataAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(Guid sessionId,
        long afterEntrySequence, int limit, CancellationToken cancellationToken = default);
    ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision,
        CancellationToken cancellationToken = default);
    ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default);
}
public interface ISessionOutput
{
    ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default);
}
```

SessionSnapshot/UserProfile fields and atomic save semantics are specified in [Persistence](15-persistence-and-configuration.md); SessionOutput is the application output family in [Event Model](07-event-model.md). Save with expectedRevision=0 inserts; subsequent saves compare stored revision and write expectedRevision+1. Conflict is an application persistence conflict, not last-write-wins. `RecoverCrashedSessionsAsync` runs at process startup for SQLite: Attached becomes Paused, Ending becomes Ended, Streaming entries become Interrupted, and PendingMode is cleared. There is no generic repository interface. Definition lookup returns null for missing versions, throws a normalized validation failure for malformed data, and pins a version for the full session.

**Observed history load:** `LoadMetadataAsync` returns session/snapshot coordinates without conversation rows. `LoadAsync` restores a bounded runtime window (prompt keep plus the complete trailing unresolved user suffix and any streaming rows). `ReadHistoryAsync` is forward-only (`after`, `limit`) and pages from durable rows. `LastEntrySequence` is stored on the snapshot and must not be derived from a window's last in-memory entry. Bounded `SaveAsync` upserts supplied entries and does not delete or renumber older `ConversationEntries` rows.

**Follow-on P1 planned until verified:** extend history reads for newest, `before`, and existing `after` pages; reject `before`+`after`. Speech locale/voice support is evaluated on speech abstractions/adapters, not with Browser/OpenAI branches in SessionRuntime. See [Technology Decisions](10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence).

Environment data enters Application only through this narrow ingress. It is not a message bus and is not a public HTTP `/events` endpoint:

```csharp
public sealed record EnvironmentEvent(Guid EventId, string Kind,
    IReadOnlyDictionary<string, string> Data);
public interface IEnvironmentEventIngress
{
    ValueTask PublishAsync(Guid sessionId, EnvironmentEvent input,
        CancellationToken cancellationToken = default);
}
```

Implementations allowlist kinds, validate data, scope the event to `sessionId`, reject executable/instruction-like payloads, and admit a normalized `EnvironmentReceived` into the session mailbox. Synthetic demo fixtures and future CRM/calendar adapters use this boundary. Unknown sessions or kinds fail without affecting other runtimes.

## Native realtime extension contract (future only)

This is a future design escape hatch only. Do not implement, register, negotiate or test a native provider in the MVP milestone path. The composed pipeline does not branch on native capability availability. The signatures below reserve a separate optimized path after MVP.

```csharp
public sealed record NativeRealtimeCapabilities(bool AudioInput, bool AudioOutput,
    bool Reasoning, bool Transcription, bool TurnDetection, bool Interrupt);
public sealed record NativeSessionOptions(AudioFormat Format, AgentDefinition Definition);
public sealed record NativeTurnRequest(Guid ResponseId, IReadOnlyList<ModelMessage> Context);
public abstract record NativeRealtimeEvent;
public sealed record NativeRecognition(SpeechRecognitionEvent Event) : NativeRealtimeEvent;
public sealed record NativeText(Guid ResponseId, ModelGenerationEvent Event) : NativeRealtimeEvent;
public sealed record NativeSpeech(Guid ResponseId, SpeechSynthesisEvent Event) : NativeRealtimeEvent;
public sealed record NativeTurnSuggested(Guid EventId) : NativeRealtimeEvent;
public interface INativeRealtimeProvider
{
    NativeRealtimeCapabilities Capabilities { get; }
    ValueTask<INativeRealtimeSession> OpenAsync(NativeSessionOptions options,
        CancellationToken cancellationToken = default);
}
public interface INativeRealtimeSession : IAsyncDisposable
{
    ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    ValueTask BeginTurnAsync(NativeTurnRequest request, CancellationToken cancellationToken = default);
    ValueTask InterruptAsync(Guid responseId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<NativeRealtimeEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}
```

Native audio goes directly to this session without decomposing it into STT/LLM/TTS. The adapter maps vendor response IDs to application-issued ResponseIds. Native turn detection suggests a turn; the runtime authorizes BeginTurnAsync. An adapter unable to gate autonomous output must buffer until authorization or advertise UnsupportedCapability. Interrupt must retain identity filtering even if the provider cannot cancel promptly. Native events normalize into the same internal and wire semantics; native implementation, extension detection and protocol negotiation are all deferred beyond MVP.

## Post-MVP

Observed owner capability and session catalog/lifecycle are in Application ports (`IOwnerCapabilityService`, `IMemoryStore.ListCatalogAsync`). Observed `IAttachmentStore` covers pending upload, pending-to-bound claim, staged-next-turn bind for speech, authorized content streams, TTL sweep, and session-delete blob cleanup. Observed `IAttachmentProcessor` extracts bounded text (plain/Markdown/JSON/CSV/PDF with page provenance), validates PNG/JPEG/WebP/GIF with 32 MP decoded-pixel checks and stripped provider-facing metadata, caches by AttachmentId+processor version, never fetches remote URLs, and returns a typed unsupported result for unread or out-of-reader files. Extraction runs off the mailbox against immutable blobs. `ILanguageModel.Capabilities.Vision` is truthful: production OpenAI-compatible adapters map normalized image parts when Vision is true, otherwise return `UnsupportedCapability`.

Observed `ISessionWorkspace` uses logical paths only (`/agent`, `/attachments`, `/workspace`). Infrastructure `FileSessionWorkspace` maps `/workspace` onto `data/workspaces/{sessionId}/workspace/{working,artifacts,state}`, overlays read-only `/agent` (pinned identity, harness names, knowledge citations—not corpora) and `/attachments` (immutable originals), applies an optional role template from `agents/templates/{templateId}` into `working` without copying harness/knowledge trees or secret files, enforces 250 MiB writable quota under concurrency, rejects traversal/absolute/symlink/other-session paths, and deletes on versioned durable delete only. Application never sees host paths.

Observed `IArtifactStore` keeps metadata in SQLite (or the in-memory equivalent) and binaries under `data/artifacts/{sessionId}` outside row payloads and outside `local/`. Artifacts are a distinct type from Attachments. Explicit `materialize` copies an attachment into `/workspace/working` with sanitized deterministic collision names, preserves SHA-256, and records `SourceAttachmentId`. Caps are 50 MiB each and 250 MiB per session under concurrency. Envelope authorization accepts stored ArtifactIds plus the Phase C fixture `fixture-artifact-1`. Export/download uses the trusted-local owner capability.

Observed typed tools: Application-normalized `ModelToolCall` / `ModelStopReason.ToolCalls` / `ModelCapabilities.Tools`. Session Runtime runs an off-mailbox tool loop (max 12 steps, 30 s per tool, 120 s overall, 8 MiB tool output) with epoch/response guards; `outputState=runningTools` holds initiative like in-flight extraction. Scoped capabilities are `knowledge.retrieve`, `attachments.read` by AttachmentId, logical workspace paths, `artifacts.create`/`verify`, and optional `sandbox.run`. Host paths, `process`/`shell`, and Session/history mutation arguments fail closed. Production OpenAI-compatible adapters map `tools` / `tool_calls` when tools are offered and still return `UnsupportedCapability` for unexpected tool_calls. ScriptedLanguageModel is not a substitute for that adapter mapping. Current-turn text attachments are included in the user prompt up to a dedicated attachment context budget (16 KiB characters shared, with a minimum slice per text file); the user's own instruction is the first multipart text part when attachments are present. `attachments.read` is for overflow, prior-turn files, or agents that need targeted rereads—not for basic reading of a file the user just attached. Effective tool offering is gated on `ILanguageModel.Capabilities.Tools`; tool-less models receive no tools even when the session has attachments, so current-turn embedded text still works but later attachment recall requires a tool-capable model. When `attachments.read` is not offered, overflow and manifest wording tell the model that full historical reread is unavailable rather than instructing a tool call. Upload MIME types are normalized on intake (textual extension inference when the browser sends blank or generic types; binary formats require signatures). Uploads are never executed. No durable WorkItems until the [Phase I future trigger](10-technology-decisions.md#post-mvp-planned-until-verified).

Observed `ISandboxExecutor`: Infrastructure `DockerSandboxExecutor` runs `busybox:1.36` with `--network none`, `--read-only`, `--user 65534:65534`, `--cap-drop ALL`, `--security-opt no-new-privileges`, 64 MiB / 0.5 CPU / 32 PIDs / 8 s, bind-mount of that session's `/workspace/working` only, normalized `echo`/`true`/`cat`/`sleep`, cancellation kill/reap, bounded output, and export only through `IArtifactStore` for `/workspace/working` paths. Unit fakes may supplement Application allowlist tests; they do not replace isolation/resource checks. Missing Docker skips those facts; it is not Phase H success. Shipped Support/Compliance allowlists do not include `sandbox.run` or process/shell.

Observed rich envelope: parent `ResponseId` owns `reply.text` (stored as entry `Text` after marker strip), optional `reply.speech`, Markdown/attachment/artifact/unknown blocks, independent display vs speech-coordinate receipts, and fixture-only artifact authorization (`fixture-artifact-1`). Unknown and unauthorized artifact refs persist a safe fallback without leaking the id.

Quota numbers: [resource table](10-technology-decisions.md#planned-resource-limits).
