# Persistence and Configuration

## Persistence model

EF Core 10 with SQLite is the MVP durable store, implemented behind IMemoryStore in Infrastructure. The base Synthetic/testing profile defaults to InMemoryMemoryStore; the normal native developer workflow explicitly selects SQLite as shown in [Operations](17-observability-and-operations.md#running-after-implementation). SQLite contract/integration suites use a temporary database. Real profile defaults to SQLite. No raw microphone frames, TTS chunks, credentials, provider request bodies, every token delta, or high-frequency controller internals are persisted.

| Entity | Key and fields | Rules |
| --- | --- | --- |
| Session | SessionId UUID string PK; AgentId, AgentVersion, DefinitionJson, Mode, PendingMode nullable, Status, PauseReason nullable, CreatedAtUtc, UpdatedAtUtc, Revision | Store pinned validated definition; terminal Ended is irreversible; PauseReason set when Status is Paused |
| ConversationEntry | EntryId UUID PK; SessionId FK; EntrySequence; SourceEventId nullable; Role; Text (display); ResponseId nullable; Status; DeliveryMode; HeardTextEndExclusive (speech coordinate); ReceivedTextEndExclusive (display); EnvelopeJson nullable; AttachmentRefsJson nullable; CreatedAtUtc | Unique (SessionId,EntrySequence); unique (SessionId,SourceEventId) when not null; response ID unique per assistant entry |
| SessionSnapshot | SessionId PK/FK; SchemaVersion=1; Summary; SummarizedThroughEntrySequence; PendingTopic nullable; ProfileId nullable; LastEntrySequence; LastUserActivityAtUtc nullable; UpdatedAtUtc | Persist coarse semantic continuity and last meaningful user activity for inactivity policy, never tasks/timers/active provider streams |
| UserProfile | ProfileId UUID PK; PreferencesJson; Revision; UpdatedAtUtc | <=16 allowlisted preferences, <=2,000 total characters; MVP uses one local profile |

Store timestamps as UTC Unix milliseconds (`long`) with explicit EF conversions to DateTimeOffset, and GUIDs as canonical text. Do not depend on SQLite ordering arbitrary DateTimeOffset strings or rowversion support. Session.Revision is an application-managed optimistic concurrency token. Enable foreign keys, WAL and 5-second busy timeout; keep writes short. No DbContext instance is shared by workers. SQLite is a single local file, not a network filesystem/distributed store.

Domain snapshot contracts used by IMemoryStore:

```csharp
public enum ConversationRole { User, Assistant }
public enum EntryStatus { Streaming, Completed, Interrupted, Failed }
public enum SessionMode { Text, Voice }
public enum SessionStatus { Created, Attached, Paused, Ending, Ended }
public sealed record ConversationAttachmentRef(Guid AttachmentId, string DisplayName, string ContentType);
public sealed record ConversationEntry(Guid EntryId, long Sequence,
    Guid? SourceEventId, ConversationRole Role, string Text, Guid? ResponseId,
    EntryStatus Status, SessionMode DeliveryMode,
    int HeardTextEndExclusive, int ReceivedTextEndExclusive,
    DateTimeOffset CreatedAt, ResponseEnvelope? Envelope = null,
    IReadOnlyList<ConversationAttachmentRef>? Attachments = null);
public sealed record UserProfile(Guid ProfileId, long Revision,
    IReadOnlyDictionary<string, string> Preferences, DateTimeOffset UpdatedAt);
public sealed record SessionSnapshot(int SchemaVersion, Guid SessionId,
    long Revision, AgentDefinition Definition, SessionMode Mode, SessionMode? PendingMode,
    SessionStatus Status,
    IReadOnlyList<ConversationEntry> Entries, string Summary,
    long SummarizedThroughEntrySequence, string? PendingTopic, Guid? ProfileId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```

For bounded personal MVP sessions, LoadAsync returns all stored entries; cap a session at 1,000 entries and require a new session at the limit. Runtime prompts select a smaller window. ReadHistoryAsync provides stable entry cursor pagination. SaveAsync atomically updates Session, Snapshot and upserts supplied entries in one transaction; it never deletes unseen entries. Snapshot Entries is the full current session list, frozen before dispatch. Store revision must match expectedRevision; snapshot.Revision must equal expectedRevision+1. Insert uses expectedRevision=0 and snapshot.Revision=1. A retry of the same committed revision/content is idempotent; a differing stale write fails Conflict. InMemoryMemoryStore enforces identical rules.

Stored Text retains full generated text. DeliveryMode records the interaction mode in which the entry was produced and never changes if the session later switches mode. ReceivedTextEndExclusive is the last validated browser-rendered prefix; public history projects only that prefix for assistant entries, so reconnect never newly reveals an unreceived generated tail. User entries set both offsets to Text.Length. On supersession, freeze HeardTextEndExclusive and ReceivedTextEndExclusive at their last validated values; late playback events/receipts do not revise that response. HeardTextEndExclusive remains separately conservative for voice-delivered context using [Spoken Until](06-realtime-voice.md#spoken-until); rendering text is not evidence of hearing it. Prompt construction uses received prefix when DeliveryMode is Text and heard prefix when DeliveryMode is Voice.

## Write ordering and recovery

One persistence write is in flight per Session Runtime. Capture immutable snapshots and serialize write dispatch, coalescing pending periodic checkpoints to the newest state. The mailbox still accepts interrupt/end events while writes execute. After a store write, persist completion returns through the mailbox with persist-token identity; only a still-pending token may apply. Advance durable revision only for that acknowledged snapshot, then start the next write. Apply subsequent dirty state to the next revision. Never let an old snapshot overwrite new spoken-until or terminal state. Checkpoints waiting behind an in-flight write collapse to the newest queued checkpoint; user-turn, pause, terminal, and other callback-bearing saves are not coalesced.

Durability boundaries: save on creation, accepted final user turn, response terminal, mode change (including queued `PendingMode` while the session remains attached), pause/disconnect and end. On pause/disconnect persist `PendingMode=null` even if a voice request was queued; Mode stays the last **applied** mode (Text if voice never started). During active generation checkpoint at most once per second, preserving only currently assembled text and conservative heard/received offsets. A 1-second streaming checkpoint **must not** rewrite every historical conversation row. Persistence identifies/upserts changed or currently streaming entries plus changed snapshot/session metadata. Immutable completed historical entries are not rewritten every checkpoint. Transactional SaveAsync, expectedRevision and optimistic-concurrency semantics are unchanged; Snapshot.Entries in the in-memory contract may still list the full session, but the Infrastructure writer must not issue no-op UPDATEs for unchanged completed rows. User turn generation waits for its save acknowledgement; final response completion waits for terminal save acknowledgement. Checkpoints cannot mark a still-streaming response completed. Temporary storage failure pauses new generation, emits recoverable SessionPersistenceUnavailable and retries local save after 1/2/5 seconds (maximum 3 retries); continued failure cancels active output, invalidates the attachment and pauses a non-ending session for explicit reconnect. An Ending session remains Ending until its terminal save succeeds; it cannot resume. Conflict is fatal for that runtime because it indicates a second writer or bug.

A crash may lose up to the last checkpoint interval of streaming text/progress, but not a user turn whose save succeeded. On startup/load, reconcile Attached→Paused and Streaming→Interrupted transactionally; set `PendingMode=null` so crash recovery does not auto-enter voice; keep the last recorded heard offset, never assume unacknowledged audio was heard. After attach, a trailing completed-user suffix with no later started assistant entry is one next user-turn; if an assistant entry for that suffix already exists (including Interrupted after recovery), do not start another. Ending recovers by completing the end transaction before attach; it cannot return to active. Provider streams, audio, timer generations and connection leases are never resumed.

Memory categories: ephemeral working state; persistent conversation history; persistent session summary; minimal persistent user profile. Initially only language and preferredName are allowed profile keys. Local demo setup through `IMemoryStore` seeds `language=en` and does not invent a `preferredName`. The historical seed value `friend` is treated as absent, not as a user-supplied name, including when it remains in an older SQLite profile. Preferred name is trusted profile data only, not inferred from conversation. No profile CRUD UI/API, embeddings, vector search or Redis.

## Post-MVP planned until verified

Observed A–H persistence/layout; Phase I WorkItems are not-applicable (no extra durable job schema). MVP Session.Status `Ended` remains irreversible terminal-end.

- **Owner capability grant:** hashed trusted-local token in SQLite; validate HTTP/hub callers; survive process restart.
- **Catalog fields:** Title, ArchivedAt, DeletedAt/pending-cleanup, WorkspaceOwnership (SessionId key), pinned AgentId+AgentVersion. Rename/archive/deactivate/delete take the same revision-checked save path as snapshots so they cannot be overwritten by a concurrent runtime checkpoint. Deactivate persists `Paused` and increments `RuntimeEpoch` without setting `ArchivedAt` or clearing history.
- **Attachments (observed store):** metadata and MessageAttachment relation in SQLite; bytes in `IAttachmentStore` opaque blob keys **outside** row payloads and outside developer `local/` scratch (`data/attachments` by default). Pending TTL 1 hour; bound originals immutable; SHA-256 unchanged after bind. Durable session delete removes that session's blobs and metadata. Derived extraction cache is keyed by AttachmentId plus processor version and is not conversation history.
- **Session workspace (observed):** lazy physical tree under `data/workspaces/{sessionId}` (never under `local/` or `local/tdp-workspace`). Logical `/workspace` writes cap at 250 MiB. `/agent` and `/attachments` are overlays, not copies. Optional templates come from `agents/templates/{templateId}` into `working` only. Archive/reopen/deactivate keep files; durable delete removes them. Do not persist CTS, provider streams, sockets, leases, timer handles, DbContext, live queues, or process handles. Docker sandbox bind-mounts only `workspace/working` and does not leave `acsbx-*` residue outside that session tree.
- **Rich receipts (observed):** persist display, speech-coordinate, and block delivery atomically with response status (R2) in `EnvelopeJson` plus heard/received offsets. Do not store a single offset that is applied to both text and speech.
- **Artifacts (observed store):** distinct metadata/blobs from Attachments under `data/artifacts/{sessionId}` (never under `local/`). 50 MiB each / 250 MiB per session, concurrent fail-closed. Explicit attachment materialize copies into `/workspace/working` with provenance (`SourceAttachmentId`) and matching SHA-256. Archive/deactivate keep blobs; durable delete cancels writers and removes them. Envelope authorization includes stored ids and `fixture-artifact-1`.
- **Migrations:** backfill workspace-ownership; preserve pins, revisions, receipts, and irreversible Ended rows on populated fixtures and fresh databases.

PostgreSQL migration later replaces EF provider, revisits GUID/time conversions, migrations and concurrency tests; business interfaces stay stable. Provider change alone does not magically make SQLite-specific SQL portable, so avoid provider SQL outside Infrastructure and test migration data explicitly.

## Strongly typed options

Bind/validate on startup with standard .NET options and ValidateOnStart. These option type names match the root sections below. Nested providers use capability-specific records; do not inject IConfiguration into Domain/Application. Session factory receives a validated effective policy snapshot.

| Options type/section | Fields and purpose |
| --- | --- |
| AgentCoreOptions / AgentCore | Profile (Synthetic default), definition directory, session limits (`MaxActiveSessions` counts in-memory runtimes only), reconnect grace, mailbox capacity, context budgets |
| ProvidersOptions / Providers | LanguageModels map plus independent `Speech.Recognition` / `Speech.Synthesis` records (defaults Adapter=Synthetic). SpeechRecognizers / SpeechSynthesizers maps remain aliases: `primary-stt` / `primary-tts` bind when the nested Speech Adapter is unset. Recognition names after P1: Synthetic, Browser, OpenAI, OpenAICompatibleBatch. Synthesis: Synthetic, Browser, OpenAI. |
| InteractionOptions / Interaction | Candidate thresholds, classifier deadline, ducking, degraded policy, `PendingVoiceTimeoutMs` (default 30000) |
| VoiceOptions / Voice | Canonical format, frame size, queue budgets, utterance limit, playback progress |
| PersistenceOptions / Persistence | Provider, connection string, checkpoint interval, busy timeout, attachment blob root (`data/attachments`), workspace root (`data/workspaces`), template root (`agents/templates`), artifact blob root (`data/artifacts`); never under `local/` |
| ObservabilityOptions / Observability | Logging level, timeline limit, content logging opt-in, OTLP enable/endpoint |
| HostingOptions / Hosting | Same-origin/default local binding, allowed development origins, development proxy behavior, Compose published-port gateway trust (`TrustPublishedPortGateway`, default false; resolves only inside a container) |

Complete conceptual appsettings.json example, **Markdown only**:

```json
{
  "AgentCore": {
    "Profile": "Synthetic",
    "AgentDirectory": "agents",
    "MaxActiveSessions": 10,
    "MaxEntriesPerSession": 1000,
    "ReconnectGraceSeconds": 60,
    "MailboxCapacity": 256,
    "Context": {"MaxHistoryEntries": 20, "MaxHistoryCharacters": 24000, "MaxSummaryCharacters": 2000, "MaxProfileCharacters": 2000}
  },
  "Providers": {
    "LanguageModels": {
      "primary-llm": {
        "Adapter": "Scripted",
        "BaseUrl": "https://provider.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-model",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120}
      }
    },
    "Speech": {
      "Recognition": {
        "Adapter": "Synthetic",
        "BaseUrl": "https://speech.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-stt-model",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120}
      },
      "Synthesis": {
        "Adapter": "Synthetic",
        "BaseUrl": "https://speech.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-tts-model",
        "AdditionalHeaders": {},
        "Voices": {"default": "configured-voice"},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120}
      }
    }
  },
  "Interaction": {
    "Classifier": "heuristic",
    "NoPartialPolicy": "speechAndFinal",
    "ActivityScoreThreshold": 0.7,
    "CandidateMinMs": 120,
    "BackchannelMaxMs": 700,
    "AmbiguousEvidenceMs": 250,
    "ClassifierTimeoutMs": 200,
    "SemanticFallbackMs": 500,
    "DegradedInterruptMs": 250,
    "FinalTranscriptTimeoutMs": 2000,
    "BatchFinalTranscriptTimeoutMs": 20000,
    "DuckEnabled": true,
    "DuckGain": 0.2,
    "DuckTimeoutMs": 600,
    "PendingVoiceTimeoutMs": 30000
  },
  "Voice": {
    "Encoding": "pcm_s16le", "SampleRateHz": 24000, "Channels": 1,
    "FrameDurationMs": 20, "InputQueueMs": 500, "OutputQueueMs": 2000,
    "PrebufferMs": 60, "MaxUtteranceSeconds": 120,
    "PlaybackProgressMs": 100, "PlaybackAckTimeoutMs": 5000,
    "Vad": {
      "NoiseFloorAdapt": 0.05, "StartThreshold": 0.7, "StartHangFrames": 3,
      "EndThreshold": 0.4, "EndHangFrames": 15, "MinActivityMs": 120,
      "ActivityScoreSmoothing": 0.3
    },
    "Segmentation": {"MinCharacters": 20, "ClauseMinCharacters": 40, "MaxDelayMs": 300, "SoftMaxCharacters": 120, "HardMaxCharacters": 240, "MaxPendingSegments": 4}
  },
  "Persistence": {"Provider": "InMemory", "ConnectionString": "Data Source=data/agent-core.db", "CheckpointMs": 1000, "BusyTimeoutMs": 5000, "AttachmentRoot": "data/attachments", "WorkspaceRoot": "data/workspaces", "TemplateRoot": "agents/templates", "ArtifactRoot": "data/artifacts"},
  "Observability": {"LogLevel": "Information", "TimelineCapacity": 500, "LogConversationContent": false, "OtlpEnabled": false, "OtlpEndpoint": "http://localhost:4317"},
  "Hosting": {"BindUrl": "http://localhost:5080", "AllowedOrigins": ["http://localhost:5173"], "UseViteProxy": true, "TrustPublishedPortGateway": false}
}
```

Configuration fields for inactive adapters are ignored after structural validation; Synthetic must never attempt example URLs or require ApiKey. Bind operator secrets from environment or user-secrets only: `OPENROUTER_API_KEY` into the OpenRouter-configured language-model `ApiKey`, and `OPENAI_API_KEY` into the OpenAI STT and TTS `ApiKey` fields. Nested `Providers__...__ApiKey` remains valid. Neither variable is required for Profile=Synthetic, default tests, or application boot in synthetic mode. Real profile requires an explicitly configured non-synthetic LLM. STT/TTS adapters are required only when a loaded definition has `Voice.Enabled`. Preferred **Real/demo** configuration: OpenAICompatibleLanguageModel (Adapter=OpenAICompatible) pointing at OpenRouter for text with an operator-selected **fixed** `DefaultModel` (placeholder `<operator-fixed-openrouter-model-id>`); hosted STT via `OpenAICompatibleBatch` (no interim partials) until `OpenAiSpeechRecognizer` realtime transcription is implemented and selectable; OpenAiSpeechSynthesizer (Adapter=OpenAI) for TTS. Recognition Adapter=`OpenAI` remains unselectable while the realtime session is a no-op. `openrouter/free` is reserved for opt-in adapter smoke tests, not the demo profile. A Real **voice** process still needs speech keys when those adapters are selected; a Real **text** process needs only the OpenRouter key. Missing `OPENAI_API_KEY` must not fail synthetic/offline tests or block completing the text-conversation path. Browser STT/TTS need no backend speech key; they also do not make recognition or synthesis a guaranteed local/offline service. OpenAICompatibleSpeech is an optional compatible TTS adapter, not the primary streaming demonstration.

**Effective capabilities** come from the selected adapter. Optional `RequiredCapabilities` lists flags that must be **true**. Optional `DisabledCapabilities` may turn off optional adapter features (for example disable partials to test `speechAndFinal`). Do not use `RequiredCapabilities: false` to describe a batch adapter; select `OpenAICompatibleBatch`, which reports streaming/partials as false. Do not use a configuration `Capabilities` object to make an adapter claim unimplemented behavior. Built-in `OpenAI`/`Synthetic`/`Scripted` adapters define their own capabilities (Synthetic may withhold partials via adapter-specific test options). Generic compatible/local adapters may document operator assertions validated by contract tests. `heuristic` is the built-in classifier alias. `MaxActiveSessions` applies to in-memory runtimes on attach/activation; durable Created/Paused/Ended rows do not consume a slot.

AgentCore__Profile=Real selects hosted-oriented defaults. When `Persistence__Provider` is not set in the environment, the API host upgrades the base `InMemory` persistence entry to **Sqlite** so durable sessions survive restart; set `Persistence__Provider=InMemory` explicitly to keep ephemeral storage (for example Real Compose smoke). Example .NET environment overrides for OpenRouter text plus an **optional batch-STT degraded speech configuration** (operator supplies actual secrets later; these are placeholders):

```text
AgentCore__Profile=Real
Persistence__Provider=Sqlite
Persistence__ConnectionString=Data Source=data/agent-core.db
Providers__LanguageModels__primary-llm__Adapter=OpenAICompatible
Providers__LanguageModels__primary-llm__BaseUrl=https://openrouter.ai/api/v1/
Providers__LanguageModels__primary-llm__DefaultModel=<operator-fixed-openrouter-model-id>
OPENROUTER_API_KEY=<backend-secret>
Providers__Speech__Recognition__Adapter=OpenAICompatibleBatch
Providers__Speech__Synthesis__Adapter=OpenAI
Providers__Speech__Synthesis__DisabledCapabilities__TimingMarks=true
OPENAI_API_KEY=<backend-secret-when-using-openai-speech>
Hosting__AllowedOrigins__0=http://localhost:5173
```

Operators must also set real speech BaseUrl/DefaultModel/voice mapping for their selected endpoints. Environment keys with hyphens are supported by .NET configuration but are not valid shell variable assignment names; supply via a process environment map or quoted `env 'key=value'` arguments during implementation. Keep keys in backend user-secrets or environment, never VITE_* or React bundles. Production same-origin serving needs no permissive CORS. Restrict development origins; do not combine wildcard origins with credentials.

## Local personal workspace

Repository root `local/` is gitignored scratch (personal notes, temp files). Nothing under `local/` is shared in git. [Repository Structure](11-repository-structure.md) records the folder.

`local/` is **not** an application configuration source. Do not bind options from files in `local/`, do not load it with `IConfiguration`, and do not copy its contents into committed `appsettings*.json`. A gitignored root `.env` (copied from committed `.env.example`) is Compose interpolation for Real/hosted Compose and optional shell export for native processes; it is not loaded by ASP.NET. Bind `OPENROUTER_API_KEY` / `OPENAI_API_KEY` from process environment or `dotnet user-secrets` on the API project (for example `dotnet user-secrets set OPENROUTER_API_KEY <value> --project src/AgentCore.Api`). Never print keys in chat, logs, traces, docs, commits or shared command history. Missing hosted keys are not a test failure and must not block Synthetic/default suites.

## Hosted and on-prem provider configurations

Portable capability aliases live in Agent Definition/provider selection; concrete endpoints, model IDs, headers and secrets live only in backend Infrastructure options. Independent `Providers.Speech.Recognition` and `Providers.Speech.Synthesis` records are the primary speech selection. Keep LanguageModels plus optional SpeechRecognizers/SpeechSynthesizers maps: several text aliases can target different models for different identities, and `primary-stt`/`primary-tts` remain map fallbacks when nested Speech Adapter is unset. Do not replace them with one gateway-wide AI provider.

Preferred hosted text override (merge into the synthetic example when Profile=Real; configure speech separately):

```json
{
  "Providers": {
    "LanguageModels": {
      "primary-llm": {
        "Adapter": "OpenAICompatible",
        "BaseUrl": "https://openrouter.ai/api/v1/",
        "ApiKey": "<OPENROUTER_API_KEY>",
        "DefaultModel": "<operator-fixed-openrouter-model-id>",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120}
      }
    }
  }
}
```

DefaultModel is the concrete model field; it is not copied into ModelRequest or public DTOs. Real/demo must use a **fixed** operator-selected OpenRouter model ID. These docs use the placeholder `<operator-fixed-openrouter-model-id>` rather than pinning a catalog snapshot. Opt-in adapter smoke may set `DefaultModel` to `openrouter/free`; do not assert quality or structured-output correctness of whatever free model is routed. To compare identities later, configure aliases `examiner-llm` and `support-llm` with different DefaultModel values, then select those aliases in each definition's ProviderPreferences.LanguageModel. No Agent Runtime condition branches on a provider/model name.

To migrate text inference on-prem, retain Adapter=OpenAICompatible and replace BaseUrl with e.g. `http://localhost:8000/v1/`, DefaultModel with the served local model name, and ApiKey/AdditionalHeaders with the local server's requirements. A vLLM-compatible endpoint is an example, not mandatory infrastructure. Direct OpenAI can use `https://api.openai.com/v1/` with its own credentials/model and verified compatibility settings. OpenRouter remains a hosted gateway; changing its model ID does not make inference local.

For hosted STT, select `OpenAICompatibleBatch` (no streaming input, no interim partials). Recognition Adapter=`OpenAI` (`OpenAiSpeechRecognizer` realtime transcription, recommended DefaultModel `gpt-live-transcribe`) remains unselectable while the live session is a no-op. For hosted TTS, select Adapter=`OpenAI` (`OpenAiSpeechSynthesizer`). Then independently replace the adapter alias, endpoint, model, credentials and required/disabled capability constraints as needed. Prefer streaming STT with partials and streaming TTS when a selectable adapter actually provides them; effective capabilities come from the adapter. Default automated tests keep Synthetic speech until `OPENAI_API_KEY` is supplied for a selected hosted speech adapter. Local speech may need a different concrete adapter if its protocol differs, but never changes ISpeechRecognizer/ISpeechSynthesizer or controller logic. All secrets/endpoints can be overridden using the same .NET double-underscore syntax, including:

```text
Providers__Speech__Recognition__BaseUrl=<hosted-or-local-stt-endpoint>
Providers__Speech__Recognition__DefaultModel=<stt-model>
Providers__Speech__Recognition__ApiKey=<backend-secret-or-empty>
Providers__Speech__Synthesis__BaseUrl=<hosted-or-local-tts-endpoint>
Providers__Speech__Synthesis__DefaultModel=<tts-model>
Providers__Speech__Synthesis__ApiKey=<backend-secret-or-empty>
Providers__Speech__Synthesis__Voices__default=<tts-voice>
```

[Technology Decisions](10-technology-decisions.md) owns the provider rationale. No actual configuration files are created by this documentation update; React receives only safe effective capabilities, never credentials, model IDs or endpoints.


## Provider selection and DI

Bind strongly typed SpeechRecognitionProviderOptions and SpeechSynthesisProviderOptions on `Providers.Speech.Recognition` / `Providers.Speech.Synthesis`, and LanguageModelProviderOptions as the values of the LanguageModels map. SpeechRecognizers / SpeechSynthesizers maps still bind the same option types for `primary-stt` / `primary-tts` when nested Speech Adapter is unset. Infrastructure `SpeechFactory` resolves those Adapter names into an `EffectiveSpeechPlan` (input/output transports, resolvability, and effective capabilities) plus optional `ISpeechRecognizer`/`ISpeechSynthesizer`. `Synthetic` registers the synthetic ports and `serverAudio`. `Browser` is a client transport (`clientTranscript`/`clientSpeech`) with no backend port; the plan still carries explicit client capabilities (partials and speech-boundary events for Browser STT; cancellation, voice selection and speaking rate for Browser TTS). `OpenAI` realtime recognition is not selectable while the live session is deferred; it is not replaced by Synthetic or by batch STT. `OpenAICompatibleBatch` is the supported hosted STT path: it resolves `OpenAICompatibleBatchSpeechRecognizer` on `serverAudio` when Adapter=`OpenAICompatibleBatch` and a backend API key is present, with StreamingAudio=false and PartialTranscripts=false. A missing key stays not resolvable. `OpenAI` TTS resolves `OpenAiSpeechSynthesizer` on `serverAudio` when Adapter=`OpenAI` and a backend API key is present; a missing key stays not resolvable and is not replaced by Synthetic. Default Synthetic/Browser suites still start without `OPENAI_API_KEY`. Live OpenAI TTS HTTP remains opt-in (`AGENTCORE_LIVE_OPENAI_TTS`). Selected non-Synthetic adapters are not silently replaced by Synthetic. An unknown or uninstalled **speech** adapter, or a gated hosted adapter, must not abort Synthetic or Real **text-only** host startup. Language-model selection remains independent of speech.

These are Markdown-only selection overrides merged with the existing full options example, not additional configuration schemas or actual files. Each selected adapter reports effective capabilities; optional RequiredCapabilities/DisabledCapabilities and timeouts/model/voice configuration still apply. Provider-specific model IDs and secrets stay in backend options.

Initial hosted configuration:

```json
{
  "Providers": {
    "LanguageModels": {"primary-llm": {"Adapter": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1/", "DefaultModel": "<operator-fixed-openrouter-model-id>", "ApiKey": "<OPENROUTER_API_KEY>"}},
    "Speech": {
      "Recognition": {"Adapter": "OpenAICompatibleBatch", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "<stt-model>", "ApiKey": "<OPENAI_API_KEY>"},
      "Synthesis": {"Adapter": "OpenAI", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "<tts-model>", "Voices": {"default": "<voice>"}, "ApiKey": "<OPENAI_API_KEY>"}
    }
  }
}
```

Synthetic configuration (Profile=Synthetic; no keys or external calls):

```json
{
  "Providers": {
    "LanguageModels": {"primary-llm": {"Adapter": "Scripted"}},
    "Speech": {"Recognition": {"Adapter": "Synthetic"}, "Synthesis": {"Adapter": "Synthetic"}}
  }
}
```

Future local configuration (service names illustrate a Compose network):

```json
{
  "Providers": {
    "SpeechRecognizers": {"primary-stt": {"Adapter": "Local", "BaseUrl": "http://local-stt:8000/", "DefaultModel": "<local-stt-model>"}},
    "LanguageModels": {"primary-llm": {"Adapter": "OpenAICompatible", "BaseUrl": "http://local-llm:8000/v1/", "DefaultModel": "<local-text-model>"}},
    "SpeechSynthesizers": {"primary-tts": {"Adapter": "Local", "BaseUrl": "http://local-tts:8000/", "DefaultModel": "<local-tts-model>", "Voices": {"default": "<local-voice>"}}}
  }
}
```

Use the documented double-underscore environment overrides for endpoints and secrets in native processes or Compose. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns the Real overlay file (`docker-compose.real.yml`) that applies hosted OpenRouter text (the same shape as the `http-openrouter` launch profile); Compose interpolates `OPENROUTER_API_KEY` from the host, never from committed files. When moving local, explicitly clear/replace any inherited hosted ApiKey and headers; the examples do not imply a public unauthenticated local service. React never receives credentials. Updating configuration/DI bindings is not an Agent Runtime code change. A provider lacking partials or timing marks selects existing capability-based policies; it does not require a new controller design. A new protocol may require a new Infrastructure adapter only. `ISandboxExecutor` is composed in Infrastructure as `DockerSandboxExecutor` using the same `ISessionWorkspace` / `IArtifactStore` registrations; it is not a second application service.
