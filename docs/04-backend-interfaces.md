# Backend Interfaces

## Activation and AgentRun admission foundation

`IAgentRunStore` admits a Session snapshot, accepted source entries, immutable Activation and initial queued AgentRun in one operation. It exposes no standalone Activation insert. SQLite shares `SqliteMemoryStore.StageSaveAsync` inside one transaction; InMemory shares the Session store's gate and execution state. `(SessionId, DedupeKey)`, background source receipts, unique accepted source-entry ownership and unique run-per-Activation identity prevent competing admissions. Replaying a background receipt returns the originally committed child/run even if the proposed child identifiers differ; changed source content or execution pins conflict.

Run and Activation reads are scoped by `AgentRunOwner`. `AgentRunCommand` is the shared transition vocabulary for claim, renewal, checkpoint, result, failure/retry, approval, cancellation, recovery, Skill/capability loading and effect state. Its revision and lease checks precede mutation; domain transitions additionally fence generation and exact action. Response completion must link to an already durable completed assistant entry in the same Session and response; NoAction has no invented entry. Immediate child admission requires an active owned user-turn source and matching Definition/persona/model pins. Execution capability authorization and background resource policy belong to the shared application admission and runtime services, not this persistence port.

`AgentRunAdmissionFactory.ForAcceptedUserBatch` freezes one ordered, accepted batch with stable input-derived dedupe, one response and pinned execution configuration. `AgentRunCoordinator` uses the same CAS claim/recovery path for direct admission dispatch and scheduler dispatch through `IAgentRunDispatcher`. The dispatcher contract requires delivery into the sole SessionRuntime mailbox and current eligibility checks. Expired approval resumes the same attempt; uncertain external effects do not replay. Unexpected dispatch failures retain their lease because mailbox delivery may already have occurred.

`AdmitOccurrenceAsync` commits the occurrence receipt and targeted Activation/AgentRun atomically. Its required pair is `ExecutionSessionId` and `AcceptedAgentRunId`; the Run link is unique, while multiple occurrences can share one existing Session. Background admission additionally creates the child graph. Existing-target admission checks current owner/lifecycle/snapshot under the same transaction or memory gate, preserves transcript and snapshot revision, and inserts zero source user entries. Replay returns the original accepted Run and Session.

`BackgroundOccurrenceIntake` uses the frozen `AutomationExecutionTarget` and `AutomationCompletionDelivery`, preserving Instructions and source times. Background targets create separate Sessions; existing targets require exact owned eligible metadata and `AutomationDestinationPolicy` model pins. Current owner/profile, scheduling/source policy, Automation state and required Tools/Vision are checked before admission. Native fallback retains SourceOccurrence origin and its original routing rules. Both InMemory and SQLite implement this production path.

Production conversation and detached execution use the registered SessionRuntime/SessionHost AgentRun path. `SessionRuntime` and `SessionRuntimeFactory` require `IAgentRunStore`; construction without it fails immediately, and no user-turn, occurrence, capability or retry fallback bypasses admission. Durable accepted-input intent is repaired after restart; occurrence intake admits real targeted Sessions; the shared tool loop persists checkpoints and fences dispatch, and background.start returns a committed independent child. Atomic background outcomes and same-Session user continuation are exercised. Report-back, Session-first APIs/UI and approved legacy/schema retirement are implemented. Closed/frozen on final behavior `8cec78c5d47a43e0236a5c38f2e312f4e36ce283` (2026-10-08), with all five required hosted Synthetic/Compose jobs [green](https://github.com/trannamtrung1st/agent-core/actions/runs/37756244306). The [verification report](reports/activation-agent-run-background-sessions-verification.md) records local/runtime evidence and prior failed gates. P10/P11 remain unopened.

Physical Infrastructure paths use one `Persistence:WorkspaceRoot`: `agent-<instanceN>/home/blobs/<opaqueBlobIdN>` for immutable home bytes and `agent-<instanceN>/sessions/session-<sessionN>/working/` for scratch. The model sees `/home` and `/working`, never these host paths. Artifacts, attachments and definition resources retain separate roots. The sibling `.provisioned` marker prevents repeat template seeding; no scratch artifacts/state or intermediate workspace directory is created.

## Capability projection interfaces

Domain owns `CapabilityAuthorization` and `CapabilityProjectionPolicy`. Application `CapabilityAuthorizationResolver` materializes Selected/All grants as exact publication snapshots. `ToolDescriptor` supplies trusted category, bounded summary, tags, discoverability and projection class. `ToolProjectionService` produces ordinary `ModelToolDefinition[]`; `PromptContextBuilder.OfferTools` stays a thin caller. Provider ports remain unchanged.

`capabilities.load` accepts only `{query,limit?}` (1–200 characters, default four / maximum eight matches). Search ranks exact name, category, tags and description overlap deterministically within authorized, discoverable, currently eligible tools. Results contain loaded names/summaries, bounded already-projected names and no configuration details or schemas. Eight load invocations bound one execution; this is an operational discovery bound, not an authority or projected-schema ceiling.

Mailbox-admitted `IAgentRunStore` commands atomically guards revision, claim generation, Running status and cancellation; Domain carries `LoadedCapabilityIds` and `CapabilityLoadCount` through recovery transitions. No loaded IDs enter mutable Agent Instance preferences or provider DTOs. AgentRuns reuse their bounded checkpoint and claim guards.

Native live receipts pin their target Session and settle quiet evaluation without a Run. Speaking evaluation atomically links an Activation/AgentRun. Loads use the same persisted run state and current authority checks.


## Managed workspace refinement ports

`WorkspaceTemplatePolicy` contains only an optional TemplateId. The unified workspace contract applies to every Definition. `ToolExecutionAdmission.WorkspaceCwd` carries a trusted runtime snapshot; `ToolExecutionResult.WorkspaceCwd` proposes a typed metadata effect committed only by the fenced Session mailbox. `workspace.cwd` has one get/set contract and requires actual owned workspace availability.

`AgentInstanceWorkspaceService.WriteAsync`, `PatchAsync` and `CopyAcrossScopesAsync` derive the owner from persisted Session metadata and recheck active eligibility inside lifecycle exclusion. Create rejects supplied stale tokens; replacement requires current expectedRevision and/or expectedSha256. Patch requires expectedSha256, strict UTF-8 and exactly one occurrence per oldText. Same-scope home restructuring still requires the whole-tree token.

`ISessionWorkspace` and `IAgentInstanceWorkspaceStore` export/import a bounded `WorkspaceTransfer` of relative entries, directory markers, MIME types and exact bytes. Neither returns a physical root/blob key. Scratch import stages the validated tree before exclusive destination installation; home import writes immutable blobs and commits tree metadata together, cleaning new blobs on failed commit. Home `ImportAsync` accepts a separate optional source Session id, supplied by the trusted copying Session; imported files, directories and newly materialized parents keep that provenance, including guarded file replacement. `WorkspaceTransfer` carries no ownership or Session identity. Single-file home import uses existing guarded replacement. Cross-store copy snapshots source bytes and then checks the destination; it grants no cross-store transaction or destructive move. Prior structural batch partial-result semantics remain unchanged.


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

Language-model Timeout failures use bounded `FailureReason` values `setupTimeout` (before response headers), `streamIdle`, or `totalTimeout`. Unclassified Timeout remains terminal. Caller cancellation uses Cancelled. [Backend timeout policy](12-backend-implementation-spec.md#timeouts-and-retry-policy) owns the durable AgentRun retry fences.

```csharp
public enum ProviderErrorCode
{
    Authentication, RateLimited, Timeout, Cancelled, InvalidRequest,
    InvalidResponse, Unavailable, UnsupportedCapability, Unknown
}
public sealed record ProviderFailure(
    ProviderErrorCode Code,
    string SafeMessage,
    TimeSpan? RetryAfter = null,
    string? FailureReason = null,
    string? ResponseChannel = null);
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
    bool StreamingText, bool Cancellation, bool Vision = false, bool Tools = false,
    bool StructuredOutput = false);
public sealed record ModelResponseContract(bool SpeechWillBeUsed);
public sealed record ModelSemanticResponse(
    string DisplayText, ModelSpeechProjection Speech, IReadOnlyList<ModelResponseBlock> Blocks);
public sealed record ModelRequest(
    Guid ResponseId, IReadOnlyList<ModelMessage> Messages,
    int MaxOutputTokens = 512, double? Temperature = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    string? ReasoningEffort = null,
    ModelResponseContract? ResponseContract = null);
public abstract record ModelGenerationEvent;
public sealed record ModelTextDelta(string Text) : ModelGenerationEvent;
public sealed record ModelDisplayDelta(string Text) : ModelGenerationEvent;
public sealed record ModelSemanticResponseReady(ModelSemanticResponse Response) : ModelGenerationEvent;
public sealed record ModelReasoningDelta(string Text) : ModelGenerationEvent;
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

Exactly one terminal Completed/Failed event per successful enumeration, followed by EOF. Caller cancellation may throw `OperationCanceledException` instead. Unexpected exceptions are normalized at the supervised application boundary; provider exception details never go to Contracts. Empty EOF is `Unavailable`, never implicit success. Model events have request-scoped identity; the pump adds ResponseId from its captured request, never from current mutable state. `InvalidResponse` is a distinct provider failure for malformed or unusable assistant output after the request was accepted; it is not `InvalidRequest` (the provider rejected the request). Optional `ModelRequest.ResponseContract` asks for provider-neutral semantic events (`ModelDisplayDelta`, `ModelSemanticResponseReady`); a null contract keeps `ModelTextDelta`. Resolved language models are wrapped by an Infrastructure semantic-response decorator: native completed JSON is validated when `StructuredOutput` is true, otherwise one bounded compatibility instruction and marker parser produce the same events. Raw JSON and marker fragments are never display or speech. Application types do not carry `response_format` or `json_schema`. Unsupported requested capabilities fail before making a request.

The Language Model reasons over normalized text messages only. ModelRequest has no vendor model ID. A trusted `IModelCatalog` exposes operator-configured descriptors (catalog key, display name, trusted provider alias, concrete model ID, capabilities including `StructuredOutput`, supported reasoning-effort values). `ILanguageModelResolver.Resolve(SessionModelSelection, ModelPurpose)` captures an immutable client for Conversation, Initiative, or CompletionEvaluation from the persisted session selection and copies trusted catalog `Tools`, `Vision`, and `StructuredOutput` onto cloned provider options and resolved `ModelCapabilities`. This P2D slice uses the session-selected model for all three purposes. Compaction reuses `ModelPurpose.Conversation`; it is not a separate provider port. Do not mutate singleton `LanguageModelProviderOptions` when a session changes model. Browser input never supplies provider alias, BaseUrl, ApiKey, or arbitrary provider JSON. Hidden provider reasoning fields are not spoken content and never become ModelTextDelta. See [Technology Decisions](10-technology-decisions.md#decision-session-model-selection-and-inference-controls).

## Terminal Agent Step

After Infrastructure emits `ModelSemanticResponse`, Application `AgentStepNormalizer` validates one terminal Agent Step before Chat delivery. The step is an Agent Core contract, not a provider DTO. `IAgentBrain` / `AgentDecision` (`StaySilent`, `Speak`, `RequestDeactivate`) stays the pre-generation decision and is not this step.

```csharp
public enum AgentStepDisposition { Continue, Wait, Complete, Blocked }
public abstract record AgentAction;
public sealed record ChatRespondAction(
    string DisplayText, ModelSpeechProjection Speech, IReadOnlyList<ModelResponseBlock> Blocks) : AgentAction;
public sealed record AgentStep(
    AgentStepDisposition Disposition,
    IReadOnlyList<AgentAction> Actions,
    IReadOnlyList<MemoryProposal> MemoryProposals);
```

The strict structured-output schema and the `agent_core_respond` response function both require `disposition` (`Continue`, `Wait`, `Complete`, `Blocked`) and `action` (`{"kind":"chat.respond"}` or null), plus display, speech, blocks, and memory. `additionalProperties` stays false. The strict schema uses singular `action`. The parser still accepts one matching `actions` item so a non-strict payload is not dropped, and that array is not part of the strict schema. An omitted disposition and an omitted action, including the plain-text compatibility channel, normalize to `Complete` and one `chat.respond`. An explicit null action does not invent `chat.respond`. Memory proposals are copied beside actions and are not actions. `chat.respond` is not a `ToolRegistry` entry and is not a fake `agentcore.wait`, `agentcore.complete`, or `agentcore.continue` tool. The action carries no session, profile, tenant, or recipient. A model-supplied `sessionId`, `destination`, `profileId`, `tenant`, or `recipient` rejects the whole step. Unknown disposition, unknown action, and malformed payload also reject the whole step as `ProviderErrorCode.InvalidResponse`, execute nothing, admit no memory, and use the existing diagnostic id path. `chat.respond` with empty or whitespace-only `displayText` is `InvalidResponse` with `missingDisplayText`; the runtime does not invent display text. When `SpeechWillBeUsed` is false, a missing or unusable speech object canonicalizes to `same` with null text, redundant `same` text is ignored, and a well-formed `none` or non-empty `custom` projection is kept. Voice turns ignore text on `same` and `none`, reject empty `custom` text as `missingCustomSpeechText`, and reject an unknown mode as `invalidSpeechMode`. Those reasons name the structure only. The strict schema and `agent_core_respond` instructions tell models that ordinary user answers use `Complete` plus non-empty `displayText`, and that `Wait` is only for real external waits. Hidden reasoning is not a step field.

`AgentStepController` chooses the effect and does not itself mutate Session lifecycle, AgentRun, approval, trigger registration, or memory. `Complete` or `Continue` with one Chat action delivers that action once. `Continue` has that same Chat effect and does not start another model generation. Further model work in the same user turn is the existing tool pump, including `app.message.send` and `skills.load`. Zero Chat actions return control with no Chat effect and no memory admission. `Wait`, and `Complete` or `Continue` with no Chat action, return control without delivering Chat, staging memory, or completing the assistant successfully. That is not an interruption: `Interrupted` remains reserved for preemptive stops such as user steer, user stop, supersession, and lifecycle cancellation. For allowed no-chat returns on non-user triggers, Session Runtime removes the in-flight assistant placeholder instead of persisting an empty interrupted row, and `ResponseCompletedOutput` omits `interruptReason`. The bound AgentRun commits its quiet outcome with the same placeholder removal. On a direct `TriggerKind.UserTurn` chat request, any no-chat Agent Step (`Wait`, `Complete`, or `Continue` with no `chat.respond`) is semantically invalid and uses the existing safe failure path with diagnostic id `InvalidAgentStep`; it must not masquerade as returned control, interruption, or a silent successful turn. Allowed no-chat returns remain for non-user triggers only. `Blocked` uses the same failure path without executing Chat or admitting memory. A following model completion for a returned activation does not succeed or fail it again.

`ChatActionAdmission` is the Chat allow/deny seam, not a generic action-authorization framework. Ordinary same-session Chat is an explicit allow and does not require approval. The live call passes a null model destination, `DetachedExecution: false`, `EpochMatches: true`, and `AlreadyAccepted: false`. Response identity is the check that call actually compares. Model destination rejection happens in normalization before this seam. A later application action should reuse only the parts that are still real once a second action type exists. The seam binds the Chat target to the runtime's current Session. A model-supplied session, destination, profile, tenant, or recipient is rejected. Detached work cannot gain a Session Chat effect. A later semantic result for the same live response refreshes that assistant entry and does not append a second one or fail the original response. After the response is terminal, a stale or duplicate observation is dropped and admits no memory. A stale epoch or response id is dropped before effects. The admitted `ChatRespondAction` is what `SemanticResponseMapper` turns into the envelope. Unauthorized attachment and artifact references still fall back inside that successful response. Failed, denied, or stale terminal results discard staged memory.

## Definition Skills

Optional `SkillSpec` values live on immutable `AgentDefinition` versions and mutable Definition candidates. A missing/null `skills` collection is empty; every entry explicitly requires `Projection` (`Always` or `OnDemand`) and `DefaultEnabled`, plus stable id, name, description, procedure, required capabilities and Definition resource paths. Names are metadata and do not shadow other Skills. Requirements never grant tools, credentials, approval or owner scope; `chat.respond` remains a semantic capability, not a registry tool.

`EffectiveSkillCatalogResolver` combines the exact pinned Definition, instance-owned stable-id enabled state and enabled Instance Skills. Missing initialized state is a persistence invariant failure. Origin-qualified keys are `definition:<stable-id>` and `instance:<skill-id>`. Admission freezes catalog metadata, procedures, requirements and resource metadata; `AgentRun` persists the catalog, active keys and load count. Its checkpoints persist the equivalent state before the first model request and resume it exactly. Always entries start active. OnDemand entries become active only through `skills.load`; there is no keyword activation. Procedures share an 8000-character aggregate budget, independent of active count.

`skills.load` operates inside the accepted live/durable tool pump when tools are supported and the pinned effective catalog is nonempty. It accepts `ids` containing canonical keys, admits only pinned OnDemand entries, deduplicates active keys, and permits at most four requested keys per call and two calls per execution within the 8000-character procedure budget. No global active-count ceiling exists. Disabled, unknown or created-after-admission keys fail; an updated row never replaces a pinned procedure. Tool-capable execution prompts contain compact catalog metadata without procedure bodies; active procedures remain available to models without tools. Loading never changes authorization. `app.message.send` is offered on a tool-capable user turn even when the Skill list is empty. The model supplies trimmed `text` of at most 2000 characters. Routing fields are rejected. Core binds the current session. The mailbox appends one completed `ApplicationMessage`, effect key `v1:{executionId}:{toolCallId}`, under a per-execution budget (default twelve messages, 2000 characters each, 8000 aggregate admitted characters). Admission is authoritative in Session Runtime; successful tool results return remaining message and character budget. Same-text or same-effect-key dedupe does not consume budget. When the budget is exhausted, `app.message.send` is omitted on the next model generation and hallucinated calls fail closed with `over_budget` without failing the user turn. That dedupe is exactly-once admission within one live or recovered durable snapshot. It is not a transactional external-delivery guarantee if the process stops before that session snapshot commits, and the effect key is not a unique index. Prompt history, compaction, and completion evaluation skip that role. Heard text stays empty. A direct `SessionToolExecutor` call does not apply either effect. A cancelled or superseded execution writes no late message; an already admitted message remains.

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
public interface IDiagnosticIdSource
{
    Guid NewId();
}
```

`RequestInterruptionClassification` invokes `IInterruptionClassifier` only. `RequestAgentDecision` invokes `IAgentBrain` only. Do not call AgentBrain to classify microphone events. `InterruptionContext.ActivityScore` is the browser VAD observation; `TranscriptConfidence` is optional STT evidence (null when the adapter does not provide it). IAgentBrain is an application policy/context-builder boundary. User turns build a normalized ModelRequest directly. Proactive triggers run hard gates first, then `IInitiativeEvaluator` (`initiative-decision-v2` JSON on a dedicated `ILanguageModel` instance); only a well-formed `Speak` (required `intent` and `objective`) proceeds to the full generation request on the foreground model. Malformed `speak` plans fail closed to `StaySilent` with `CountsTowardSilentCap=false` (`invalid_plan`). Generation applies trusted server-owned `intent` (`InitiativeIntent` / `InitiativePlan.TryCreate`) as fixed framework instructions; planner `objective` is untrusted `initiative_planner_observation` JSON only. That JSON is delivered as a final `ModelRole.User` message after transcript turns so adapters stay provider-shaped; it is framework-owned internal context, not a human user turn—do not treat it as the latest conversational request. Session Runtime cancels in-flight brain evaluation on user activity, supersession, detach, and pause; initiative failures fail closed to `StaySilent`. Decisions must pass the same runtime policy recheck. `StaySilent.CountsTowardSilentCap=false` marks hard-gate denials that must not advance silent-evaluation pause. Allocate a candidate response ID before deciding; only Speak makes it live and emits `agent.response.started`. StaySilent allocates no visible response. RequestDeactivate cancels live output, rotates the runtime epoch, persists Paused, and is not archive or v1 end. Role `environment.toolAllowlist` is runtime-enforced (`RolePermissions`); `process`/`shell` stay denied. Approved knowledge retrieval returns identity, title, citation, and current file body under the pinned source identity (`GET /api/v2/sessions/{id}/knowledge/{identity}`). Classifier defaults to deterministic heuristics; optional model fallback is bounded by [Controller](05-interaction-controller.md).

Use injected `TimeProvider` for UTC timestamps, monotonic elapsed time, and timers (`Task.Delay(delay, timeProvider, token)` or `CreateTimer`). Do not define IClock. Production NewId calls `Guid.CreateVersion7(timeProvider.GetUtcNow())`; NewSessionId calls `Guid.NewGuid()` for cryptographically random UUIDv4 local/demo bearer session IDs. Tests use reproducible sequences for both. IDs are serialized as strings at browser boundaries. `IDiagnosticIdSource.NewId()` is a separate `Guid.NewGuid()` source for one failure occurrence. It does not dequeue `IIdGenerator`. A failed assistant entry may carry one `FailureReference` (`DiagnosticId`, optional `CorrelationId`, bounded category and code, and when present the allowlisted `FailureReason` and `ProviderResponseChannel`). Non-failed entries and legacy failed rows leave it null. Those two detail tokens are the same closed set used in server logs. They name the violated contract. They are not model output, tool arguments, or prompts. A provider `LengthLimit` before a parseable envelope is `outputLimit`, not `missingDisplayText`. A `finish_reason=length` stream that has a partial tool-call draft is `toolCallTruncated` on channel `toolCall` and is not executed. A parsed malformed `agent_core_respond` payload keeps its precise `InvalidResponse` reason.

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
    ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(Guid sessionId,
        long? afterEntrySequence, long? beforeEntrySequence, int limit,
        CancellationToken cancellationToken = default);
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

**Observed P7A Admin read services:** Application `AdminReadService` (with `AdminEffectiveConfigurationResolver`) serves owner-protected inventory and effective-configuration reads. It lists built-in definitions through `IBuiltInAgentDefinitionStore`, lists durable publications through `IAgentDefinitionAdminStore`, and merges composite catalog rows for Admin inventory. It lists instances through `IAgentInstanceStore.ListAsync`, and resolves effective model selection via `SessionModelBinder`/`IModelCatalog`, offered tools via `ToolCatalog`/`IToolConfigurationGate`, and durable-work eligibility via `OccurrenceCompatibility` on the exact pinned `(definitionId, activeVersion)` and current persona. HTTP mapping lives in Api `AdminEndpoints` with allowlisted DTOs in Contracts; domain/persistence records and provider configuration never cross the wire. User-mode session APIs remain read-only for definition/persona mutation.

Effective configuration is shared resolution primitives plus use-case-specific composition. `EffectiveConfigurationComposer` is not a universal runtime configuration object: `ComposeAdmin` assembles the Admin projection, and `MemoryPolicyOf` owns the shared default `definition.MemoryPolicy ?? MemoryPolicy.Disabled`. `AdminEffectiveConfigurationResolver` returns that projection. `AdminMemoryService` and explicit user-memory admission use the same memory default. `SessionModelBinder.PinDefault` pins the catalog default for the Admin projection and for the conversation-default branch of `ExecutionModelPolicy`. Background scheduled and application-event admission store that policy's pin on the occurrence. `BackgroundOccurrenceIntake` copies the background pin into `AgentRunModelPin`; an exact ExistingSession target instead uses its authoritative Session pin after current capability validation. A missing tools capability for browser-allowlisted work, or a text-only pin when the registration sets `RequiresVision`, fails with `model-capability-unsupported` before execution. A pin whose catalog key, provider, model id, or effort no longer matches the catalog fails with `model-unavailable`. A live session may `Bind` a selected catalog key and reasoning effort. `ToolCatalog.For` supplies offered-tool names for the Admin projection and for prompt construction. `ToolPolicy.EvaluateExecution` stays at execution, and detached admission denies session-scoped tools. `RoleEnvironments` supplies harness references, knowledge sources, and the workspace template id on `AdminEffectiveConfiguration` and on prompt, tool-policy, and workspace reads. Trigger policy and `OccurrenceCompatibility` feed durable-execution eligibility at the Admin projection, the scheduler, routing, and session delivery. `BackgroundSessionAdmissionFactory` pins the child Session and AgentRun Definition, persona, model and Skills; SessionRuntime builds the execution context with the trusted profile and detached policy. It does not build the Admin synthetic `AgentContext`. Trusted profile is not a field on `AdminEffectiveConfiguration`. `DefinitionEvaluationHarness` builds a scripted prefix and a resource manifest. `SyntheticDefinitionDraftBehaviorEvaluator` pins the catalog default, offers tools with a null context, records `ToolPolicy.EvaluateExecution`, and returns a stub tool result. Neither calls `SessionToolExecutor`. The closure audit found no contradictory model, tool, resource, memory, or trigger default among these callers.

## P7B definition lifecycle (observed)

Application ports:

```csharp
public interface IAgentDefinitionAdminStore
{
    ValueTask<AgentDefinitionDraft> CreateDraftAsync(AgentDefinitionDraftCreate create, CancellationToken cancellationToken = default);
    ValueTask<AgentDefinitionDraft> UpdateDraftAsync(AgentDefinitionDraftUpdate update, CancellationToken cancellationToken = default);
    ValueTask DeleteDraftAsync(AgentDefinitionDraftDelete delete, CancellationToken cancellationToken = default);
    ValueTask<AgentDefinitionPublication> PublishDraftAsync(AgentDefinitionDraftPublish publish, CancellationToken cancellationToken = default);
    ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(AgentDefinitionPublicationDeprecate deprecate, CancellationToken cancellationToken = default);
    ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);
    ValueTask<AgentDefinitionPublication?> GetPublicationAsync(string definitionId, int version, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(string? definitionId = null, CancellationToken cancellationToken = default);
}
```

`IBuiltInAgentDefinitionStore` remains read-only file definitions. Runtime `IAgentDefinitionStore` is implemented by Infrastructure `CompositeAgentDefinitionStore` over built-ins and durable publications. `AgentDefinitionLifecycleService` validates candidates (structure, aliases, publish-time model/tool gates, secret scanning) before store mutations. Published payload is immutable; deprecation updates metadata only. Provider DTOs and raw store rows do not cross into Domain beyond normalized definition records.

## P7C definition resources (observed)

Application ports `IDefinitionResourceContentStore` and `IAgentDefinitionResourceAdminStore` own bounded draft/publication resource bindings and verified content blobs. `AgentDefinitionResourceService` coordinates draft revision checks, upload, single bind, one-revision batch bind, remove, and publication read APIs. `BindDraftResourcesAsync` accepts an already-stored manifest and commits every item in one revision, or leaves the revision and rows unchanged. `KnowledgeSourcePaths.ResolveBackingPath` uses optional `KnowledgeSourceRef.ResourcePath` when it is non-blank, otherwise `knowledge/{identity}`. A missing, wrong-kind, or non-textual binding fails validation and retrieval. Null `resourcePath` is omitted on the wire. `AgentDefinitionLifecycleService.CreateNewDraftAsync` stores `AgentDefinitionStarter` with `SourceKind.New`; the HTTP body is only the definition id. `DefinitionPublicationResourceReader` resolves exact `(definitionId, version)` bindings for `FileSessionWorkspace` `/agent/resources` projection and optional one-time template seeding into `/working`. Infrastructure implements InMemory and SQLite stores with migration `20260925084907_P7DefinitionResources`. See [Persistence](15-persistence-and-configuration.md#p7c-definition-resource-store-observed) and [P7C report](reports/p7c-harness-resources-workspace.md).

## P7D managed instances (observed)

`IAgentInstanceService` creates Agent Instances from exact durable publications. Admin create accepts an optional persona: omit or null stores the definition identity as `personaSource` Default; a present persona is stored as Custom on that insert and on the single `ManagedInstanceCreated` summary, with no following `PersonaChanged`. Later persona updates stay on name, role, description, and tone. The service applies revision-protected persona updates (`PersonaRevision` token), explicit active-version reassociation, and `Active`/`Archived` lifecycle transitions without touching session history, structured memory, Automations, or background Sessions/AgentRuns. `SessionManager.CreateForInstanceAsync` pins definition version, `PinnedPersona`, and `PinnedPersonaRevision`, and rejects archived managed instances. User chat inventory uses `ListChatEligibleAsync` (active managed only). Trigger and schedule admission consult managed lifecycle at registration create and due execution. Owner Admin HTTP maps persona, lifecycle, and active-version PATCH routes under `/api/v2/admin/agent-instances/{instanceId}/...` with expected revision fields. `IAgentInstanceStore` persists managed persona JSON and revision columns (InMemory/SQLite parity). See [Persistence](15-persistence-and-configuration.md#p7d-managed-instance-store-observed), [Backend implementation](12-backend-implementation-spec.md#p7d-managed-instances-observed), and [P7D report](reports/p7d-managed-instance-identity.md).

## P7E memory and automation Admin (observed)

`AdminMemoryService` and `AdminAutomationService` list and mutate learned memory and trigger registrations through existing store boundaries with owner capability, scope confirmation, and revision checks. Memory delete/reset and trigger cancel append allowlisted `AdminEvents` when an operation id is supplied. `TriggerInstancePolicyReconciliationService` suspends ineligible future registrations on managed version downgrade or archive without cancelling accepted P6 work. See [P7E report](reports/p7e-memory-automation-admin.md).

## P7F validation, evaluation, and publish gate (observed)

Application services resolve provider/model/tool/resource references, run layered validation, bind Synthetic evaluation evidence to exact draft revision, configuration fingerprint, and scenario version, produce safe section diffs, and publish transactionally only when validation, required evaluation, and expected draft revision align. Stale evidence and concurrent edits fail closed. See [P7F report](reports/p7f-validation-evals-publish-gate.md).

## P7G Admin history (observed)

`IAdminEventStore` append-only persists lifecycle events with unique `OperationId` idempotency and bounded summary JSON validated by `AdminEventSummaryPolicy`. Mutators record events in the same transaction as draft/publication/instance/memory/trigger changes at the store boundary. `GET /api/v2/admin/events` lists safe projections only. See [Persistence](15-persistence-and-configuration.md#p7g-admin-events-observed) and [P7G report](reports/p7g-history-rollback-final-gate.md).

SessionSnapshot/UserProfile fields and atomic save semantics are specified in [Persistence](15-persistence-and-configuration.md); SessionOutput is the application output family in [Event Model](07-event-model.md). Save with expectedRevision=0 inserts; subsequent saves compare stored revision and write expectedRevision+1. Conflict is an application persistence conflict, not last-write-wins. `RecoverCrashedSessionsAsync` runs at process startup for SQLite: Attached becomes Paused, Ending becomes Ended, Streaming entries become Interrupted, and PendingMode is cleared. There is no generic repository interface. Definition lookup returns null for missing versions, throws a normalized validation failure for malformed data, and pins a version for the full session.

**Observed P2C trusted profile mutation:** Application `ILocalUserProfileService` performs optimistic revision updates with server-side source stamping (`UserSet`, `HostSet`, `ApplicationProfile`). HTTP `GET|PATCH /api/v2/profile` requires owner capability; browser PATCH always stamps `UserSet` and cannot supply provenance. After durable save, `IProfileLiveUpdateNotifier` (SessionHost) applies newer matching profiles to live runtimes via urgent mailbox `ProfileUpdatedReceived`; stale or mismatched revisions are ignored without changing session snapshot revision, history, or in-flight responses. Profile prompt projection remains quoted data with fixed application framing, not model instructions. No generic profile-write tool; memory-derived personalization stays P4-owned.

**Observed history load:** `LoadMetadataAsync` returns session/snapshot coordinates without conversation rows. `LoadAsync` restores a bounded runtime window (prompt keep plus the complete trailing unresolved user suffix and any streaming rows). `ReadHistoryPageAsync` serves newest/`before`/`after` pages from durable rows (`before`+`after` rejected at SessionManager). `LastEntrySequence` is stored on the snapshot and must not be derived from a window's last in-memory entry. Bounded `SaveAsync` upserts supplied entries and does not delete or renumber older `ConversationEntries` rows.

**Observed additive lifecycle fields:** Session snapshots persist `lifecycleStatus`, generic `SessionPurpose`, completion-authority policy, and lifecycle source/reason/timestamps. Protocol-v1 `status` remains. Purpose metadata and policy are not public session-view fields. `RecoverCrashedSessionsAsync` still maps Ending → Ended (and `lifecycleStatus` Ended). Application `LifecycleTransition` is the persist path for pause/resume/terminal outcomes, attached TimeProvider deadline timers, and detached create/get/attach/reopen expiry. RequestComplete evaluation runs after a completed assistant turn when a Goal session has agent completion Advisory or Allowed.

**Observed speech locale:** Application `SpeechLocale` resolves session override > fixed agent conversation-language tag (`auto` is not a speech locale) > provider fallback (`en`), validates BCP-47-like tags, and persists `SpeechLocaleOverride` without rewriting agent conversation `language`. Effective locale is public on session views and `session.ready` `capabilities.speechLocale`. `ISpeechLocaleSupport` evaluates recognition/synthesis support; `SessionRuntime` has no Browser/OpenAI locale branches. Browser STT uses the effective tag; Browser TTS selects exact locale, then base language, then a compatible configured/default voice, or fails Voice without speaking the wrong language. Hosted batch STT sends an adapter-local language hint; hosted TTS locale/voice compatibility stays in adapter configuration.

**Follow-on P1 observed and frozen:** Speech locale/voice selection in Browser and hosted adapters is observed. Real Chrome 153 `fr-FR` Browser STT/TTS smoke is observed. See [Technology Decisions](10-technology-decisions.md#decision-provider-neutral-effective-speech-locale).

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

## Browser v2 contract

Application owns `IBrowser`, its explicit `BrowserProviderDescriptor` and feature IDs, provider-neutral requests/results, the profile lease and secure password sink. Infrastructure owns `PlaywrightBrowser` and every Playwright object. The canonical tool/feature/schema/effect registry is `BrowserToolCatalog`. Definition authorization, contextual policy, configuration readiness and provider feature support are intersected both during discovery/projection and execution. Unsupported features return structured `unsupported_operation`; there are no retired-tool aliases, translators or alternate browser runtime.

| Group | Model capabilities |
| --- | --- |
| Core | `browser.navigate`, `snapshot`, `find`, `click`, `hover`, `drag`, `drop`, `type`, `fill_form`, `select_option`, `press_key`, `upload`, `fill_credential`, `wait_for`, `tabs`, `dialog`, `resize`, `screenshot`, `close` (all with the `browser.` prefix) |
| Configuration / environment | `browser.get_config` (ReadOnly); `browser.set_geolocation` (SensitiveWrite, attached UserTurn and exact approval) |
| Diagnostics | `browser.console_messages`, `browser.network_requests`, `browser.network_request` |
| Network control | `browser.route`, `browser.routes`, `browser.unroute`, `browser.network_state` |
| Storage | `browser.cookies`, `browser.local_storage`, `browser.session_storage`, `browser.storage_state` |
| Testing / vision | `browser.verify`, `browser.generate_locator`, `browser.mouse` |
| Developer / privileged | `browser.pdf`, `browser.trace`, `browser.highlight`, `browser.emulate_media`, `browser.video`, `browser.evaluate` |

Native ARIA snapshots are the primary observation. Snapshot content is clipped at 8000 characters; the internal search index covers the full permitted accessibility tree, without a 40-element discovery ceiling. `browser.find` returns up to 20 current indexed targets by bounded text or regex. Element refs are opaque, scoped to the current snapshot, tab/frame and session, and resolve through native Locators across DOM replacement. A fresh snapshot invalidates previous refs. Snapshot options support subtree/depth responses and bounded boxes. All browser content is untrusted. Ordinary tools accept no selector, path or script. Locator-generation output is informational and cannot be supplied as target authority.

Focused interaction schemas replace the combined action union. Type and form values are bounded to 500 characters; forms contain at most 20 fields, uploads at most eight approved Artifact/Definition resources. `tabs` supports `list/new/select/close` with opaque tab refs and host-policy checks. Dialogs are retained for explicit `inspect/accept/dismiss`; a triggering action yields `dialog_pending` rather than deadlocking. `wait_for` supports stable, text/textGone, target, URL and load conditions with a 100–5000 ms deadline. Native keyboard combinations are supported beyond the old three keys. Action results carry fresh snapshot evidence where appropriate.

Screenshot supports PNG, JPEG and WebP, viewport, bounded full-page and target captures. It masks credential controls, reflected protected values and child-frame pixels; the output cap is 1,500,000 bytes and four successful captures per scope. Text-only models receive an Artifact receipt; vision models additionally receive image content. `browser.mouse` requires an authorized vision-capable execution. Downloads and screenshots become immutable Artifacts owned by the real Session, at most two files per execution and 5 MiB per file, using the existing accepted MIME set. No provider filesystem path is exposed. AgentRun checkpoints retain Artifact IDs and tool text, never image bytes. `SessionCaptureRehydration` reloads vision screenshots through `IArtifactStore` under the same Session owner after restart; unavailable bytes require a fresh screenshot. Text-only executions retain Artifact delivery without receiving image content. Session Artifact lifecycle owns cleanup; no separate work-capture store or retention rule remains.

Navigation, interaction and resource-origin policy are separate host authorities and apply to all tabs, frames and new paths. Network routes and storage mutations are privileged exact-approval capabilities with non-replayable effects; read summaries do not reveal headers, cookie values or request bodies. Storage output is bounded and protected values are redacted. The active Playwright host advertises raw storage-state export/import, PDF, trace/video export and evaluate as unsupported because their protected-output or isolated-evaluation boundary is not implemented. These operations are absent from configured discovery and cannot silently fall back to selectors, JavaScript, another provider or web search.

Ordinary Locator, focused keyboard, coordinate and typed interactions recheck the snapshot's protected-field classification at execution time, including linked labels and drag destinations. Changing an existing ref's metadata to password, OTP, passcode or API-key semantics does not grant ordinary interaction authority. Local/session storage and cookie metadata redact decoded strings before truncation and JSON serialization. Cookie list/delete/clear cover the active host's exact-domain and applicable parent-domain cookies across all paths; unrelated hosts remain untouched. Removing a shared parent-domain cookie also removes that same cookie for sibling hosts that use it within the owner's context.

Vision-only `browser.mouse` wheel actions use safe viewport `x/y` target coordinates and independent signed `deltaX/deltaY` bounded to ±2000; at least one delta is required.

Dialog inspection returns kind and a fully masked message (`messageRedacted=true`). An open native modal blocks the live page reads needed to discover storage/password secrets, so stale evidence cannot safely authorize message disclosure. Accept/dismiss and explicit prompt replies remain available. Snapshot/navigation observations and tab listing use one URL projection: it masks known cookie/storage/credential values (including URL encoding), sensitive query/fragment parameters and user information. Tabs collect secrets across currently permitted open pages; disallowed pages expose origin metadata only. Diagnostic network URLs remain origin-only.

`Configuration` and `Geolocation` are independent negotiated features. `browser.get_config` reports safe provider/engine/readiness/features, current native viewport, creation environment, page media overrides, online state, permission presence, restrictions and limits. It opens no context; unopened contexts report a null environment. Safe Configuration inspection remains available for a disabled or unavailable engine, subject to provider support and Definition/context authorization; page actions still require readiness. No paths, private origin lists or coordinate values are exposed. `browser.set_geolocation` requires an exact current allowed origin and an approved attached UserTurn; set/clear changes only the provider-owned live context and grants no origin navigation authority. Shipped Definition versions keep their exact existing grants; new capabilities require a new authorized Definition version.

`browser.emulate_media` preserves omitted settings; nullable media/colorScheme/reducedMotion/forcedColors/contrast clear individual overrides. `browser.type` supports `slowly` for native character events. `browser.highlight` supports show and hide (hide without ref clears page overlays). Device/viewport/locale/timezone/touch/pixel-ratio defaults are host-controlled creation settings; no active context is silently replaced. Service workers are blocked; WebSocket destinations obey mapped HTTP(S) resource-origin policy, with no message inspection capability. [The completeness matrix](reports/browser-v2-completeness-and-polish.md) classifies all upstream and advanced operations.

Geolocation is projected only for an attached UserTurn, matching execution admission. Initial grants and updates at the same origin preserve unrelated permission overrides. Playwright 1.63 has no selective revoke or permission-override enumeration API: explicit clear and switching the granted origin reset all context permission overrides, reported as `permissionsReset=true` and in safe configuration policy information. No CDP workaround or permission subsystem is added.

Credential-name classification normalizes separators and camel case in protected fields and URL parameter names. Standard API/private/access-key names, OTP spellings, bare URL keys and signature parameters remain masked or excluded from ordinary targets even when their exact values are not yet known.

Persistent profiles remain Agent-Instance-owned and are independent of credentials. `browser.close` releases live state/locks but retains the profile directory. A competing process receives `profile_busy`. Profile reset is a separate owner action. Caller cancellation propagates its original token; timed-out/cancelled native actions must be fenced before another operation acquires the session gate. Core retains the durable approval acknowledgement, receipt and uncertain-effect recovery boundaries.

The historical P9/P9.5/P9.6 reports describe their original runtime at frozen SHAs. Browser v2 acceptance is tracked independently in the [implementation plan](18-implementation-plan.md#browser-v2-full-cutover-active-migration).

## System credentials and browser profiles

`ICredentialStore` owns reusable protected resources; `IAgentCredentialBindingStore` owns explicit grants from an Agent Instance to a Credential. `CredentialService` returns safe projections and resolves `AgentInstanceId + Reference` internally. One Credential may serve multiple agents under distinct aliases. `ICredentialProtector` protects/unprotects payloads bound to CredentialId, purpose and protection version. Infrastructure provides .NET Data Protection with a persistent local key ring; no provider-specific DTO or protection API crosses that port. Kind is immutable; status is Active/Disabled. Create, safe edit, replacement, delete, bind and unbind use revision checks. Bound resources cannot be deleted. Archived owners can inspect grants but cannot mutate or resolve them; instance deletion removes grants and its browser state, retaining shared resources.

`credentials.list` is a Core context-only tool, excluded from Definition capability authorization and fingerprints. A managed agent with an eligible active binding and a tools-capable model can receive alias, display name, Kind and non-secret metadata only. Discovery uses deterministic alias pagination and fits complete records to the remaining tool-output budget; see [the protocol contract](14-api-and-realtime-protocol.md#credential-tool-boundary). Binding does not grant Browser. Browser follows Definition authorization and always/Skill/loaded projection, then execution-time host policy.

`IBrowserPasswordSink.FillCredentialAsync` accepts an opaque password element ref and an internal resolver callback. Only a direct UserTurn may use `browser.fill_credential` with `credentialRef`; it accepts no value. Under the provider page gate, the sink validates the current ref, existing-account password field, page intervention state and exact current origin. The resolver requires the current active owner's binding, an Active Password Credential and matching AllowedOrigin. BrowserTargetPolicy remains authoritative. Known protected values are registered for redaction before the fill and stay out of observation text, control state, page titles/URLs, capture and downloads. Ordinary fill cannot write password fields. Registration, new/change/reset passwords and human verification require intervention.

Generic persistent browser profiles are keyed by AgentInstanceId and work without a credential or connection record. Detached Schedule/Event execution may lease and reuse its owner's authenticated profile under normal capability/host policy, but cannot inject protected material. A login wall produces owner attention. Claim recovery keeps the existing observation/checkpoint fences for uncertain browser actions. `work.complete` records the fixed owner's summary and attention flag without a recipient; live notification failure does not delete the durable result. Profile reset explicitly clears only that owner's browser state and does not alter credentials or grants. `browser.close` retains state.

The P9.5 nopCommerce Application Connection was a bounded proving slice and is retired by System Credentials. Historical P9.5/P9.6 freeze evidence remains unchanged; current behavior uses explicit credential grants, generic capabilities, and Agent-Instance-owned browser profiles.

## Bounded browser settle

Empty `browser.snapshot` captures immediately. `browser.wait_for` with `{condition:"stable",timeoutMs:2500}` requests bounded quiet-page evidence, not proof of page completion. The automatic post-navigation/action settle remains provider-owned. Every settle read uses the remaining deadline; stalled or late reads cannot report `settled:true`. Caller cancellation remains distinct from a timeout. Two repeated snapshots/waits with unchanged evidence end the tool loop and ask for an answer from observed evidence. Successful navigation and browser effects clear the repetition streak. This preserves the deadline regression contract independently of historical freeze reports.

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

## P5 trigger ports (observed)

`ITriggerStore` and `IAutomationService` are Application ports. Infrastructure supplies the in-memory and SQLite stores. Schedule tools do not accept owner, provenance, or authorization origin. A current user turn authorizes only the schedule actions that turn requested (create, list, update, or cancel); a confirmation turn authorizes only the stored proposal. `IDurableApplicationEventIngress` admits one allowlisted `order_status_changed` shape into the same occurrence store. `ExternalEventIngress` admits one allowlisted envelope from `POST /api/v1/hooks/{sourceKey}`. The bearer token is checked against the SHA-256 hash on the Event Source before any row is written. A new `sourceId + sourceEventId` persists one External Event and one pending `ExternalEventDeliveries` row per active `order.placed` subscription at that moment. Later subscription changes do not alter that snapshot. The occurrence dedupe key is `order.placed:{sourceId}:{sourceEventId}:{automationId}`, so separate Automations and Event Sources admit distinct owned occurrences. Redelivery and the scheduler pass finish pending deliveries. A subscriber who joins afterward does not receive the historical event. The HTTP body is that event id. It does not call the router or the browser. A definition that does not allow `applicationEvent` cannot subscribe, and an ineligible subscriber is skipped without failing the other subscribers or the admission. A subscription does not create an application connection. `IEnvironmentEventIngress` remains the live-only environment path. `IOccurrenceMailbox` reserves, then commits `AcceptedLive` before a runtime speaks. Begin reports whether that runtime actually started the occurrence. A failed begin returns the row to `Pending` and abandons the reservation. Occurrence dedupe is scoped by Agent Instance, profile, and dedupe key.

## Canonical AgentRun execution ports

`IAgentRunStore` atomically admits Session/input/Activation/Run and occurrence receipts, applies revision/generation-fenced commands, commits outcome entries, and pages owned runs. `GetLatestForAutomationAsync` reads the initial run for the Automation independently of unrelated recent activity. `IAgentRunDispatcher` routes claimed runs and accepted-input repair to the common SessionHost mailbox. `AgentRunCoordinator` claims runnable/retrying rows, recovers expired leases and expires approvals. `BackgroundOccurrenceIntake` admits the exact existing Session or a real child Session per accepted occurrence; `BackgroundCompletionReporter` emits or skips its initial report once. Current authority is checked again before execution. No standalone legacy runner/store/handoff port is registered.

`work.complete` validates Response, NoAction or NeedsAttention and a bounded summary. NoAction cannot conceal an accepted mutation or fabricate an assistant entry. Background execution checkpoints retain the same tool, Skill/capability, approval, budget and external-effect fences as attached execution. Owner-scoped list/detail responses exclude checkpoint/evidence/prepared-action JSON.

## Post-MVP

Observed owner capability and session catalog/lifecycle are in Application ports (`IOwnerCapabilityService`, `IMemoryStore.ListCatalogAsync`). Observed `IStructuredMemoryStore` and `IStructuredMemoryService` store session-scoped learned items apart from `IMemoryStore` and from trusted profile rows. Write proposals carry no owner id. Conversation writes start as `MemoryProposal` values from the model envelope or an application command. The runtime stages model proposals when semantic output arrives and admits that staged set once, when the response reaches a successful terminal boundary, before the durable assistant entry is finalized. A memory policy allows recall and storage. A reliable proposal channel is separate: native structured output, or provider function calling used only as the adapter transport for the assistant response. Plain-text `[[memory:...]]` markers are best effort. When the selected model has neither structured output nor tools, recall still works and autonomous memory creation is unavailable. The prompt says so. The model must not claim a durable save. Phrase matching is not a fallback. A later semantic response for the same turn replaces the staged set. Failure, interruption, and supersession discard it without a write. `MemoryAdmission` chooses the scope from `MemoryPolicy` and an advisory scope hint, then calls `StructuredMemoryService`. Enabling a memory policy does not admit every proposal. Phrase matching is not the write authority. Model output may set source to `userExplicit` or `agentInferred` only. Application and Admin callers stamp `application` or `admin` outside that contract. A user source entry is attached only for the current user turn. Delete resolves each permitted scope on its own, so a promoted IdentityUser or User item can be removed from a later session that has no local copy. User-scope correction is create-or-update by exact subject. The controller stores admission receipts on the completed assistant envelope, apart from display text and speech text. A user-explicit success can surface as a quiet indicator. A user-explicit rejection or unavailable write stays visible. Agent-inferred outcomes stay silent. The receipt is the authoritative outcome. Model wording is guidance and is not rewritten. Search returns at most 20 active items and 6,000 characters. Correction supersedes the named active item. Deletion leaves a content-free tombstone. Durable session delete removes that session's Session-scoped items and leaves promoted IdentityUser rows. A definition `MemoryPolicy` defaults every gate off. `SessionMemory` and enabled `IdentityUserRetrieval` together project at most eight active items and 6,000 characters as a separate learned-data prompt block that states identity and trusted profile values outrank it on a conflict. Each learned subject and content stay on one line. Voice unheard suffixes are taken from speech text. `IdentityUser` promotion copies an active session item to a new row owned by the trusted agent instance and profile, keeping origin ids, and does not retarget the source. Retrieval uses that same trusted owner. A missing instance id denies both paths. `User` promotion copies an active Session or IdentityUser item to a new row owned only by the trusted profile. `UserRetrieval` is a separate gate: a definition that leaves it off, including the examiner, does not receive those items. Observed `AgentInstance` is the durable actor for a reusable definition. Session creation requires an active existing instance and pins its active Definition version and trusted persona. Upgrading the instance changes only later Sessions; current Sessions retain their immutable pins. Observed `IAttachmentStore` covers pending upload, pending-to-bound claim, staged-next-turn bind for speech, authorized content streams, TTL sweep, and session-delete blob cleanup. Observed `IAttachmentProcessor` extracts bounded text (plain/Markdown/JSON/CSV/PDF with page provenance), validates PNG/JPEG/WebP/GIF with 32 MP decoded-pixel checks and stripped provider-facing metadata, caches by AttachmentId+processor version, never fetches remote URLs, and returns a typed unsupported result for unread or out-of-reader files. Extraction runs off the mailbox against immutable blobs. `ILanguageModel.Capabilities.Vision` is truthful: production OpenAI-compatible adapters map normalized image parts when Vision is true, otherwise return `UnsupportedCapability`.

Observed `ISessionWorkspace` is the private scratch/overlay adapter behind the unified Application execution view. Model paths `/home` and `/working` resolve against the mailbox-owned cwd before authorization. The private `/workspace/working` adapter prefix and sandbox mount are never a public alias. Infrastructure maps scratch onto `WorkspaceRoot/agent-<instanceN>/sessions/session-<sessionN>/working`, resolving required trusted Session metadata even for direct writes, transfers, structure and sandbox path queries before provisioning. It overlays read-only `/agent` and `/attachments`, seeds optional templates into working once, enforces the 250 MiB quota, denies traversal and symlink escapes, and removes only that Session tree on durable delete. Structure and hash-guarded text patch operations run under the Session filesystem gate. Application/Domain/Contracts never receive physical paths.

Observed `IArtifactStore` keeps metadata in SQLite (or the in-memory equivalent) and binaries under `data/artifacts/{sessionId}` outside row payloads and outside `local/`. Artifacts are a distinct type from Attachments. Explicit `materialize` copies an attachment into `/workspace/working` with sanitized deterministic collision names, preserves SHA-256, and records `SourceAttachmentId`. Caps are 50 MiB each and 250 MiB per session under concurrency. Envelope authorization accepts stored ArtifactIds plus the Phase C fixture `fixture-artifact-1`. Export/download uses the trusted-local owner capability.

Observed typed tools: Application-normalized `ModelToolCall` / `ModelStopReason.ToolCalls` / `ModelCapabilities.Tools`. `SessionToolExecutor` returns `ToolExecutionResult` with UTF-8 `Text` (budgeted for the 8 MiB tool-output cap) and optional ephemeral `Parts` (`ModelImageContent` and future non-text types) that are not serialized into ordinary JSON tool text. Session Runtime runs an off-mailbox tool loop. Core resolves the budget before the loop: standard work is 24 steps, 30 s per tool, and 180 s overall; a user turn authorized for browser tools is 48 steps and 300 s overall; a background AgentRun holding the persistent instance-browser lease is 32 steps and 240 s overall. Tool output stays 8 MiB. Every AgentRun tool loop additionally caps textual results to remaining serialized 64 KiB checkpoint headroom and returns terminal `checkpoint-capacity` when the checkpoint cannot fit, while preserving side-effect fences. The model cannot raise the budget. Production transient provider failures use the durable AgentRun retry transition before visible output and only when no uncertain external effect is in flight. Unavailable/rate-limited failures and classified setup/stream-idle timeouts may wait for another attempt within the pinned maximum; total/unclassified timeouts remain terminal. A retry retains Session, Activation, response, checkpoint and effect receipts rather than repeating an internal generation request outside run ownership. A completed generation whose `InvalidResponse` reason is classified `Repairable` (`missingDisplayText` or `invalidBlocks`; speech omissions stay `Normalize`, and every other reason stays `Terminal`) gets one display-only protocol repair when nothing visible was published, no tool call from that generation was admitted, cancellation is not pending, and the execution deadline is still open. The repair keeps the same messages and tool results, offers no tools, clears the response contract, and asks for plain answer text only. Application wraps non-empty text as `Complete` / `chat.respond` with speech `same` and no blocks or memory. A JSON object or array is not accepted as that text. It does not persist that instruction or the malformed body. A failed repair records `protocolRepair=attempted` and `protocolRepairOutcome=failed` on the failure reference. A transient `Unavailable` during repair follows the same durable Run retry policy. The private checkpoint preserves the repair reason, so resumed repair still offers no tools and does not repeat completed effects. A second contract violation fails closed. Any other `InvalidResponse` fails closed. A tool-offered conversational request sets `max_tokens` to 8192. Tool-less turns and compact evaluators keep their own smaller `MaxOutputTokens`. Definition `maxOutputTokens` stays 1..4096 and is not that tool-argument budget. The loop keeps epoch/response guards; `outputState=runningTools` holds initiative like in-flight extraction. The per-tool and overall clocks are machine-execution budgets: `RequireApproval` pauses them for the human wait (`ToolApprovalLimits.Lifetime`, advertised as `expiresAt`) and starts a fresh per-tool timer after approve; response cancel, detach, supersession, and epoch changes still abort the wait. Execution policy is evaluated before any `email.send` Gmail draft read or `http.request` dispatch. Harness eligibility is bounded by the registered tool catalog rather than a fixed 64-tool ceiling, so a generated authorized policy remains valid when frozen or reconfigured. Role allowlists and capability snapshots have no generic count ceiling; exact registered authority and projection are separate as described above. Scoped capabilities are `knowledge.retrieve`, `attachments.read` by AttachmentId, logical workspace read/list/write/patch/search/move, `artifacts.create`/`artifacts.create_from_workspace`/`artifacts.verify`, optional `sandbox.run`, bounded `web.search`/`web.fetch`, approval-gated `http.request`, and provider-neutral `email.search`/`email.read`/`email.create_draft`/`email.send`. `workspace.search` matches filenames and bounded UTF-8 text, skips binary contents, and uses the same working-directory paths. `web.search` is offered only when the configured search provider is available (Brave when Profile=Real and a key is present; Synthetic otherwise). `web.search`/`web.fetch`/`http.request` projections are current-request tool data only—they are not persisted as conversation history, attachments, or page bodies. Host paths, `process`/`shell`, and Session/history mutation arguments fail closed. `ToolEffect` and `ToolRegistry` classify effects (`readOnly`, `write`, `sensitiveWrite`, `destructive`); execution rechecks role allowlists and configuration gates. Tools marked `RequireApproval` pause the loop until the live session receives `agent.approval.respond` for the pending approval id bound to the owning response; stale ids, expiry, supersession, detach, and runtime-epoch bumps fail closed without executing the effect. `http.request` is `SensitiveWrite`: approval binds the normalized method, URL, permitted headers, and body SHA-256. Methods are GET, HEAD, POST, PUT, PATCH, and DELETE. Ordinary public GETs should use `web.fetch`. Model-supplied Authorization, Cookie, Proxy-Authorization, and API-key headers are rejected. Credentials never enter the model context. GET and HEAD may follow public redirects; other methods do not, and non-idempotent requests are not retried. Every returned body is untrusted. The same public-DNS, SSRF, rebinding, and redirect checks as `web.fetch` apply, including rejection of localhost, private, link-local, and metadata targets. Production OpenAI-compatible adapters map `tools` / `tool_calls` when tools are offered and still return `UnsupportedCapability` for unexpected tool_calls. Image-bearing tool results flatten a tool-call round as: every textual `role=tool` message first (JSON metadata only, no base64), then Infrastructure-only multipart `role=user` continuation(s) with trusted tool-data framing and mapped `image_url` parts; text-only tool results keep the original one-message shape. Non-vision models reject image-bearing tool `Parts` before HTTP and receive a `vision_required` admission result instead of silent image drop. A provider 400/422 is a safe public message (`Provider rejected follow-up request (status)` or the initial-request equivalent). Logs record status, model, phase, provider code, and provider type. The provider's free-form message is logged only when `LogProviderErrorMessages` is explicitly enabled, and it is still redacted. ScriptedLanguageModel is not a substitute for that adapter mapping but does provide a deterministic tools+vision continuation for historical image reread in Synthetic tests. Current-turn text attachments are included in the user prompt up to a dedicated attachment context budget (16 KiB characters shared, with a minimum slice per text file); the user's own instruction is the first multipart text part when attachments are present. `attachments.read` is for overflow, prior-turn files, or agents that need targeted rereads—including historical session images by `attachmentId` when the session manifest lists them—not for basic reading of a file the user just attached. Historical image reread additionally requires `ILanguageModel.Capabilities.Vision`; Tools alone is insufficient and the runtime never pretends the model saw the image. Effective tool offering is gated on `ILanguageModel.Capabilities.Tools`; tool-less models receive no tools even when the session has attachments, so current-turn embedded text still works but later attachment recall requires a tool-capable model. Legacy Definitions retain the implicit `attachments.read` offer when the session has bound attachments. Capability-aware Definitions require exact authorization before that contextual projection. When `attachments.read` is not offered, overflow and manifest wording tell the model that full historical reread is unavailable rather than instructing a tool call. Upload MIME types are normalized on intake (textual extension inference when the browser sends blank or generic types; binary formats require signatures). Uploads are never executed. Historical Phase I remained not-applicable. Current detached execution uses the real Session/AgentRun path; no WorkItem engine remains.

Observed `ISandboxExecutor`: Infrastructure `DockerSandboxExecutor` runs `busybox:1.36` with `--network none`, `--read-only`, `--user 65534:65534`, `--cap-drop ALL`, `--security-opt no-new-privileges`, 64 MiB / 0.5 CPU / 32 PIDs / 8 s, bind-mount of that session's `/workspace/working` only, normalized `echo`/`true`/`cat`/`sleep`, cancellation kill/reap, bounded output, and export only through `IArtifactStore` for `/workspace/working` paths. Unit fakes may supplement Application allowlist tests; they do not replace isolation/resource checks. Missing Docker skips those facts; it is not Phase H success. Shipped Support/Compliance allowlists do not include `sandbox.run` or process/shell.

Observed public web ports: Application `IWebSearchProvider`, `IPublicWebFetcher`, and `IHttpRequestClient` with normalized bounded results. Infrastructure `PublicWebFetcher` and `HttpRequestClient` share `SocketsPublicWebTransport`: DNS is resolved per connect attempt, private/local/link-local/metadata/machine-local targets (including IPv4-mapped private answers) are denied, each remaining permitted address is tried until one connects, redirects/cookies/auto-decompression are disabled, policy denials map to bounded `forbidden_host`, and connection failures map to `transport_error`. `web.fetch` is GET-only. `http.request` allows GET, HEAD, POST, PUT, PATCH, and DELETE; only GET and HEAD follow redirects, and non-idempotent methods are not retried. Textual bodies decode as bounded UTF-8 for `http.request`. Provider DTOs and raw page bytes never cross into Application. Profile=Synthetic registers `SyntheticWebSearchProvider`; Profile=Real registers `BraveWebSearchProvider`, which stays unavailable until `BRAVE_SEARCH_API_KEY` or `BraveSearch:ApiKey` is present, and `web.search` is then hidden by the configuration gate. That absence is expected, not a missing tool implementation.

Observed email port: Application `IEmailProvider` (`email.search`/`read`/`create_draft`/`send`) with Synthetic fixtures under Profile=Synthetic and `GmailEmailProvider` under Profile=Real using refresh-token OAuth (`Gmail:*` or `GMAIL_*` environment keys). `email.send` requires live approval bound to the exact normalized draft hash (DraftId, To, Cc, Bcc, Subject, Body); the approval preview surfaces bounded To/Cc/Bcc/Subject/body. Header-bearing input rejects CR/LF/NUL; Gmail MIME is built and reread with MimeKit including Bcc, and GetDraft fails closed unless Gmail returns RFC822 raw. Gmail HTTP JSON is parsed unredacted so token/MIME payloads are not corrupted before use. `email.send` passes the hash-validated normalized draft to `SendDraftAsync`; Gmail issues `POST /users/me/drafts/send` with draft `id` and `message.raw` built from that approved snapshot so update-and-send is one provider operation. A definite provider rejection may retry the same approval; `Indeterminate` outcomes and cancellation after the send has been dispatched permanently consume it. Provider failures surface as normalized tool results without leaking secrets.

Observed rich envelope: parent `ResponseId` owns validated `displayText` (stored as entry `Text`), optional public `speechText` only when `speech.mode` is `custom` and it differs from display (same-mode derived playback coordinates are internal and never projected as public `speechText`), Markdown/attachment/artifact/unknown blocks, optional controller `memoryReceipts` persisted in envelope JSON apart from display and speech text, independent display vs speech-coordinate receipts, attachment authorization from bound conversation attachment ids plus `fixture-attachment-1`, and fixture-only artifact authorization (`fixture-artifact-1`). Unknown and unauthorized attachment/artifact refs persist a safe fallback without leaking the id. Legacy rows that stored marker-era speech still project through the same public `speechText` field.

Quota numbers: [resource table](10-technology-decisions.md#planned-resource-limits).

## P9.7 semantic Authoring contracts

Optional `HarnessManagementState` on `AgentInstance` defaults to Disabled. Policy includes independent scopes, mode, frozen state and a derived eligible configured-tool set. Legacy explicit source/eligible fields remain compatible for advanced owner candidate APIs; neither is required in normal Chat. Eligibility derives from previously authorized instance selections and currently configured registered ordinary tools, never from model proposals. This is a deliberate P9.7 limitation: Chat cannot propose a tool solely because it exists in the server catalog. A broader server-owned configured, safe-to-propose catalog remains future work; actual selection would still need owner approval. The default policy grants no scopes, so instruction replacement is unavailable until the owner explicitly enables its scope.

Contextual `HarnessChatContext` carries policy revision and active version. `ToolExecutionAdmission` carries execution kind, attached instance, model tools support, execution-local source receipts and current owner turn text. Fixed tools are `harness.inspect`, `harness.knowledge.upsert/remove`, `harness.instructions.update`, `harness.tool.select/configure`. They use `HarnessAuthority`, separate from Definition permissions, and non-replayable mutation admission. Semantic arguments carry expectedVersion/policyRevision and the change; all mutations also require expected/observed assessment. The server binds operation kind and current candidate revision. No operation can change authority, credentials, model/provider configuration or install executable plugins. Skill requirements never grant capabilities.

`HarnessManagementService.AuthorChatAsync` gates the instance, validates exact approval and legitimate source provenance, and performs pure shape/policy validation before a persistent fork. Identical knowledge/instruction/tool-state writes return `saved=true, changed=false` without forking or publishing. It uses existing Authoring owners, records revision-bound evidence, verifies and promotes internally. Blocking verification returns bounded field/code/message findings with `saved=false` and leaves the active version unchanged. A failed post-fork candidate is retained as a failed preparation for audit and a fresh call forks from active state; malformed preflight requests leave no draft. `ReassociateActiveVersionAsync` commits active version, published result and actor-aware history through existing CAS stores. Safe inspection excludes credentials/provider DTOs. Full bounded change previews use existing Chat approval grants; hashes remain runtime-owned. In-memory and SQLite stores retain existing atomic audit/CAS behavior. Instance Skill mutations use the separate shared service and ordinary capability authorization described below; they never fork or publish a Definition.

Publication validation permits a fork to retain registered, configuration-gated tools already present in its published source, even when the current host cannot configure them. Runtime offering and execution still require current host configuration. A new unconfigured tool grant remains a blocking publication finding. This lets a knowledge change proceed from an existing published General Assistant version without broadening tool authority.

Runtime exact-version resolution, harness fork-source selection and knowledge readback use one built-in-first source resolver. A same-version durable publication cannot silently replace the built-in baseline or its knowledge; a durable version without a built-in collision remains the source of its own resources.

## P9.8-P9.9 continuity ports

Retrospective Session projection reconstructs only Core-known successful browser-close and email-send receipts with constant safe labels, drops unknown/failed receipts, and caps projected receipts at 20 per entry. Persisted labels never enter model input. Experience recall is delimited JSON historical data beneath an explicit System trust boundary; it cannot grant tools, weaken approvals or override the current task. Harness guidance names the actual execution origin and its pinned Definition.

`IExperienceStore` owns per-instance enabled/revision settings, idempotent source admission, owned bounded listing/lookup, pending request recovery, immutable completion and revisioned visibility/reset. InMemory and SQLite implement the same contract. Optional `AdminEventAppend` on settings/visibility/reset commits safe history metadata with the mutation. Source history remains owned by `IMemoryStore`; learned-memory admission remains owned by `IStructuredMemoryService`.

`ILanguageModel` is the sole provider-neutral generation port for ordinary live and durable tool loops. Manual Experience review uses the generic durable loop, semantic inspection/recording and work.complete; no special generation method or restricted synthesis runtime remains.

Response-function guidance uses `agent_core_respond` with structured `blocks`, `speech`, and `memory`; an Artifact reference is an `artifactReference` block using the successful Session Artifact tool result. It never advertises compatibility markers or calls the function path a plain-text channel. Structured `displayText` is ordinary text, not marker-parsed. Marker compatibility guidance remains separate, with `MarkerSemanticResponseParser` as its only marker interpreter; no UI marker parsing is introduced. Workspace paths are not Artifact IDs and the existing unauthorized-reference fallback remains.

The OpenAI-compatible adapter maps returned function names using the request's offered contracts as well as registered tools. `experience.source` and `experience.record` are registered executable semantic tools; their offering and execution require the current Experience authority and owned Run context. When the assistant response function is offered, the HTTP adapter buffers bounded provider prose until the finish reason is known: a tool-call round discards that preamble, while a plain-text completion preserves the compatibility fallback. The validated response function supplies the answer once. Ordinary text and Synthetic streaming retain their existing incremental behavior.

`ITriggerStore.SaveAutomationAsync` atomically enforces expected revision, immutable provenance, active capacity and safe history. `AdmitAutomationNowAsync` checks current revision/lifecycle and overlap and snapshots instructions/model. The scheduler and router retain occurrence ownership; `BackgroundOccurrenceIntake`, atomic AgentRun storage, the coordinator and SessionHost dispatch every accepted occurrence through its selected SessionRuntime.

## Continuity retrieval and owner schedule ports

`ContinuityService` is an Application read projection over `IStructuredMemoryStore`, `IExperienceStore` and `IMemoryStore`; it writes none of them. `SearchAsync` produces `ContinuityItem` (kind, id, bounded summary, relevance, update date and provenance); `GetAsync` rechecks eligibility and returns bounded content, `HasMore` and a Session cursor. Core supplies the current instance and pinned Definition. Managed-instance eligibility and the local profile are rechecked; there is no model-provided owner override. User-scoped memory follows existing shared-user policy; IdentityUser memory, Experience and history remain instance scoped. Retained facts and derived observations keep their existing admission paths.

`IMemoryStore.ListOwnedSessionsAsync` lists up to 100 owned, undeleted metadata snapshots, optionally active/attached only. Historical entry retrieval stays in `ReadHistoryAsync`; no full transcript is loaded automatically. Search examines the latest 100 Sessions and latest 100 eligible Experience candidates; explicit search uses persisted title/summary plus at most the last 100 durable entries per Session. Automatic recall ranks title/summary metadata first and reads entries for at most five Sessions, preserving compacted topic recall. It excludes structured Memory because the existing learned-memory prompt path already supplies it. Context keeps at most five ranked hints, search at most ten, within a 6000-character serialized budget. `continuity.get` reads at most 20 entries, clips each visible text to 700 characters and bounds detail to 4000 characters. Tool wire output also respects the existing byte budget, capped at 6000 bytes. These are bounded recall/inspection windows, not exhaustive archive search.

`ExperienceService.ReconcileAsync` repairs deterministic pending Experience admissions whose background AgentRun graph is missing. It performs no semantic periodic evaluation.

`AdminAutomationAuthoringService` validates both Schedule and Event variants. Current owner edits retain original Chat/Admin provenance. Chat authoring requires a current owned user turn. Event configuration checks active owned Event Source and allowlisted type; Schedule uses existing Definition timing policy. Run now admits ManualInvocation through the normal frozen durable path.

`continuity.search` fits complete `ContinuityItem` prefix records to the current result budget, preserving IDs, kind, summary and provenance (including Memory scope). The existing `result` array shape and 6,000-character service bound remain unchanged; `truncated` explicitly signals omitted items. When no useful record and metadata fit, Core returns a small `finish_required` capacity notice rather than generic `output_limit`. Maintenance guidance requires exact scope/kind inspection before consolidation, skips mismatches without a mutation attempt, and stops on incomplete inspection. Guidance asks autonomous Automation to finish after one confirmed narrow change; different integration qualifiers remain distinct.

## P9.10 identity maintenance ports

`IStructuredMemoryStore.ConsolidateAsync(sources, result)` atomically supersedes two to eight unique unchanged Active sources and inserts one Active replacement. The owner, exact scope and kind must match. Filtered subject uniqueness applies to the replacement after supersession, within the same transaction. An unrelated subject conflict rolls back all changes. `MemoryProvenance.DerivedFromMemoryIds` holds exact sorted immediate parents; `MaintenanceOrigin` records UserTurn, Schedule, ApplicationEvent or ManualInvocation. Optional Core-owned `MaintenanceAgentInstanceId`, `MaintenanceSessionId` and `MaintenanceAgentRunId` identify the initiating execution independently of semantic ownership, including shared User Memory. These fields supplement, rather than overload, promotion's singular origin fields.

`IExperienceStore.ConsolidateAsync` similarly requires completed Eligible observations, same instance/profile, exact sorted lineage and unchanged revisions. It supersedes sources and creates a separate Consolidation observation. `MaintenanceSettingsAsync` and `ConfigureMaintenanceAsync` own the default-off, revisioned `IdentityMaintenanceSettings`; configuration uses CAS and the existing Admin audit transaction. No generic mutation port or Experience-to-Memory promotion is added.

`IdentityMaintenanceService` owns semantic validation and Core-derived authority. `SessionToolExecutor.EvaluateExecutionPolicyAsync` resolves maintenance policy before approval and again at execution; existing static policy continues to handle other tools. `AgentContext.AllowAgentConsolidation` is trusted context, never model input authority. [Backend behavior](12-backend-implementation-spec.md#p910-semantic-identity-maintenance) owns execution details.

### Semantic Experience source and record ports

`ExperienceService.SourceToolAsync` derives owner/context from Core admission and returns stable bounded evidence. `RecordAsync` verifies the source, cursor, instance setting, eligibility, content bounds and sensitivity before immutable recording through `IExperienceStore`. Explicit selected-source writes require the cursor returned by inspection. Request identity is instance/source kind/source id/cursor. Neither tool accepts authority or owner overrides.

## Agent Instance workspace contracts

`IAgentInstanceWorkspaceStore` owns durable metadata/blob semantics independently from `ISessionWorkspace` and `IArtifactStore`. `AgentWorkspaceItem` contains stable ItemId, AgentInstanceId, normalized LogicalPath, ContentType, ByteSize, SHA-256, Revision, timestamps and optional source Session id. `AgentWorkspacePage` includes bounded metadata, total usage and an after-path cursor; content returns metadata with exact bytes. `IAdminLifecycleDeletion.RecoverWorkspaceCleanupAsync` retries cleanup from committed instance-deletion receipts after logical removal, always skipping existing owners. Application `AgentInstanceWorkspaceService` owns managed-instance eligibility, trusted Session-owner derivation, path checks, direct CAS-safe writes/patches and cross-root copy orchestration.

`IAgentInstanceWorkspaceStore.WriteFileAsync` is the sole durable file-write primitive: new destinations reject expected tokens, replacement requires current revision/hash and preserves ItemId. `CopyAcrossScopesAsync` imports exact source bytes/trees with separately trusted sourceSessionId. Owner Delete requires expectedRevision. Lifecycle purge runs after committed logical deletion and receipt creation; no port exposes physical roots or grants Artifact access to ItemIds.

## Workspace structure ports

`ISessionWorkspace.StructureAsync` and `IAgentInstanceWorkspaceStore.StructureAsync` accept `WorkspaceStructuralOperation` records (`op`, `path`, `source`, `destination`, `recursive`) and return `WorkspaceStructureResult`. The shared `WorkspaceTreePlanner` preflights the whole ordered logical tree. Source/destination entries, implicit parents, exact-case conflicts, file/directory collisions, recursive intent, cycles and cumulative quotas are validated before mutation. Adapters retain physical enumeration and storage commits. File and complete-tree moves use `StructureAsync`; there is no separate narrow move contract.

`AgentWorkspaceItem.Directory` distinguishes persisted empty folders from files. `AgentWorkspacePage.TreeSha256` is a whole-owner tree token, independent of prefix/cursor. The tool directory projection reads every bounded metadata page under lifecycle exclusion, with a matching token across pages, so large first folders cannot hide later entries. Managed home mutations require `expectedTreeSha256`; a changed tree returns Conflict before mutation. Stable home file ids survive move with a revision increment; copy creates new ids/revisions and exact immutable bytes. Content reads reject directory entries. Writes and imports create explicit parent metadata.

Results contain completion state, requested/completed counts, failed index, bounded per-operation kind/status/file/directory/byte summaries and a safe failure message. Unexpected failure includes `failed`/`notExecuted` outcomes and `mutationsMayHaveOccurred:true`; counts for incomplete outcomes describe the plan, not a rollback guarantee. Cancellation propagates and callers inspect current state before retrying. Read/list/write/patch/structural mutation serialize under the existing Session gate; durable structure/write/delete serialize under the instance gate and lifecycle exclusion. No interface accepts a caller-selected physical root or alternate owner.

## Unified harness inspection

`InspectChatAsync(instanceId, currentSessionPinnedDefinitionVersion, ct)` reports `activeDefinitionVersion`, `policyRevision`, `authoringEligibleTools`, `activeDefinitionAuthorizedCapabilities`, `currentSessionPinnedDefinitionVersion` and `changesApplyToFutureSessions:true`. Authoring eligibility concerns future Definitions, never current execution authority. Authorized capability names are distinct from the contextual turn projection. Inspection clips instruction text to 1024 UTF-8 bytes with `instructionsTruncated`; full owner review remains available in Admin. Current Sessions retain their immutable pin after publication; fresh Sessions use the updated active version.

Canonical `ToolRegistry` model definitions supply both discovery summaries and execution schemas. `capabilities.load` must describe the same interface execution exposes. Copy describes exact file/tree transfers between home and working; move describes file/tree rename.


## Agent Instance Skill management ports

`AgentInstanceSkillService` is the shared semantic owner for Admin and ordinary `skills.list`, `skills.inspect`, `skills.create`, `skills.update`, `skills.set_enabled`, `skills.delete` and `skills.customize`. Normal capability authorization determines model access; there is no Harness Skill scope or owner bypass. Trusted execution supplies the instance and pinned Definition; models cannot submit an owner. List returns bounded metadata without procedures; inspect reads current durable content, including after a write, while execution projection remains pinned. Revisions fence state/local writes. Archived owners are read-only. Create accepts an optional readable `id`; omission or an empty value generates a name-based ID with an owner-local numeric collision suffix. IDs are unique within each Agent Instance, can be reused by other instances, and remain fixed during update. Create/update validate bounded procedural content and authorized registry requirements. Definition content cannot be updated/deleted from an instance. Customize atomically creates an independent local copy with source provenance and disables the source; Definition resource bindings reject customization rather than being dropped. Customize returns `{instanceSkill, definitionSkill:{key,enabled:false,revision}}` from the same committed mutation, so both resulting states are immediately observable without another read.

`IAgentInstanceStore` initializes Definition Skill state on creation and version change in the same transaction/lock as the owner revision and optional Admin history. `ReadSkillsAsync` reads both owned collections; `MutateSkillsAsync` commits state/local changes, owner revision and optional safe history together. Matching stable ids preserve enabled choices, newly encountered ids use explicit defaults, removed ids remain dormant and local Skills are untouched. Version transitions also enforce the combined Always procedure budget. SQLite and InMemory have the same CAS, rollback and deletion semantics.

When an older Session/Work Definition remains pinned after an instance upgrade, management authorization and explicit copy content use that trusted execution Definition. Combined enabled Always budget validation uses the owner's current active Definition because writes affect future executions; the owner revision fence prevents a concurrent version change from bypassing the check.

## Automation destination and delivery ports

Domain validates BackgroundSession with no target ID, or ExistingSession with one nonempty ID. CompletionDelivery is None or ToSession; ExistingSession requires None. `ITriggerStore.RejectAwaitingDurableWorkAsync` atomically rejects unavailable target input and records its reason; recurring configurations suspend, while one-shot lifecycle remains inspectable. Target suspension requires an explicit eligible owner edit to recover.

`IAgentRunStore.GetCompletionDeliveryAsync` projects NotRequested, Pending, Claimed, Handled, Admitted, Delivered, Failed or Skipped from immutable child origin and its unique receipt. Delivered requires a completed parent Run and durable assistant entry, not merely report admission. Immediate and Automation initial-child terminal outcomes share `BackgroundCompletionReporter`; NoAction settles quietly. `AgentRunCommand.DeferDispatch` is valid only before checkpoint/effects and preserves identity and provider attempt budget. `AgentContext.OutputContract` and authored-source identity separate conversational scheduling from native occurrence evaluation.

## Completion handoff and typed waits

`IAgentRunStore` owns bounded inbox list/inspect, revision-fenced take and acknowledgment intent, terminal accounting, and typed wait recovery alongside admission and outcome transactions. Ownership is exact Agent Instance, trusted profile, immutable parent/child origin and initial child Run. `background.list`, `background.inspect`, `background.take`, `background.acknowledge` and `execution.wait` are explicit authorizable capabilities. Tool results are untrusted bounded evidence. A take token binds Run, generation, tool call and expiry. Valid parent lease renewal extends an unexpired consumption claim atomically, retaining its token and provisional acknowledgment; expired claims are never revived. A signal resume transfers an unexpired claim to the resumed generation and lease. Acknowledgment requires a nonempty bounded usage statement and is provisional until successful durable final output.

WaitingForSignal has no execution claim and carries the tool call identity, frozen condition/deadline and checkpoint. Duration seconds and background timeout must be finite and positive, at most 300 seconds each; background targets are unique, nonempty, at most eight owned initial children, with `until` all or any. One Run may wait at most eight times and 900 seconds in total. Validation fails with ValidationError; foreign ownership uses NotFound; stale revisions/generations, busy parent, competing or expired claims use Conflict. Timeout returns normally and never acknowledges a result.

Exact GetCompletionInboxAsync lookup and ListBackgroundPageAsync cursor reads retain owner/parent scoping beyond bounded pages. HasCompletionClaimAsync gates acknowledgment projection on the current Run and generation; HasCompletionAcknowledgmentAsync selects the atomic successful-outcome path.
