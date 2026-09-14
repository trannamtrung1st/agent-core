# Persistence and Configuration

## Persistence model

EF Core 10 with SQLite is the MVP durable store, implemented behind IMemoryStore in Infrastructure. The base Synthetic/testing profile defaults to InMemoryMemoryStore; the normal native developer workflow explicitly selects SQLite as shown in [Operations](17-observability-and-operations.md#running-after-implementation). SQLite contract/integration suites use a temporary database. Real profile defaults to SQLite. No raw microphone frames, TTS chunks, credentials, provider request bodies, every token delta, or high-frequency controller internals are persisted.

| Entity | Key and fields | Rules |
| --- | --- | --- |
| Session | SessionId UUID string PK; AgentId, AgentVersion, DefinitionJson, Mode, Status, CreatedAtUtc, UpdatedAtUtc, Revision | Store pinned validated definition; terminal Ended is irreversible |
| ConversationEntry | EntryId UUID PK; SessionId FK; EntrySequence; SourceEventId nullable; Role; Text; ResponseId nullable; Status; DeliveryMode; HeardTextEndExclusive; ReceivedTextEndExclusive; CreatedAtUtc | Unique (SessionId,EntrySequence); unique (SessionId,SourceEventId) when not null; response ID unique per assistant entry |
| SessionSnapshot | SessionId PK/FK; SchemaVersion=1; Summary; SummarizedThroughEntrySequence; PendingTopic nullable; ProfileId nullable; LastEntrySequence; UpdatedAtUtc | Persist coarse semantic continuity, never tasks/timers/active provider streams |
| UserProfile | ProfileId UUID PK; PreferencesJson; Revision; UpdatedAtUtc | <=16 allowlisted preferences, <=2,000 total characters; MVP uses one local profile |

Store timestamps as UTC Unix milliseconds (`long`) with explicit EF conversions to DateTimeOffset, and GUIDs as canonical text. Do not depend on SQLite ordering arbitrary DateTimeOffset strings or rowversion support. Session.Revision is an application-managed optimistic concurrency token. Enable foreign keys, WAL and 5-second busy timeout; keep writes short. No DbContext instance is shared by workers. SQLite is a single local file, not a network filesystem/distributed store.

Domain snapshot contracts used by IMemoryStore:

```csharp
public enum ConversationRole { User, Assistant }
public enum EntryStatus { Streaming, Completed, Interrupted, Failed }
public enum SessionMode { Text, Voice }
public enum SessionStatus { Created, Attached, Paused, Ending, Ended }
public sealed record ConversationEntry(Guid EntryId, long Sequence,
    Guid? SourceEventId, ConversationRole Role, string Text, Guid? ResponseId,
    EntryStatus Status, SessionMode DeliveryMode,
    int HeardTextEndExclusive, int ReceivedTextEndExclusive,
    DateTimeOffset CreatedAt);
public sealed record UserProfile(Guid ProfileId, long Revision,
    IReadOnlyDictionary<string, string> Preferences, DateTimeOffset UpdatedAt);
public sealed record SessionSnapshot(int SchemaVersion, Guid SessionId,
    long Revision, AgentDefinition Definition, SessionMode Mode, SessionStatus Status,
    IReadOnlyList<ConversationEntry> Entries, string Summary,
    long SummarizedThroughEntrySequence, string? PendingTopic, Guid? ProfileId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```

For bounded personal MVP sessions, LoadAsync returns all stored entries; cap a session at 1,000 entries and require a new session at the limit. Runtime prompts select a smaller window. ReadHistoryAsync provides stable entry cursor pagination. SaveAsync atomically updates Session, Snapshot and upserts supplied entries in one transaction; it never deletes unseen entries. Snapshot Entries is the full current session list, frozen before dispatch. Store revision must match expectedRevision; snapshot.Revision must equal expectedRevision+1. Insert uses expectedRevision=0 and snapshot.Revision=1. A retry of the same committed revision/content is idempotent; a differing stale write fails Conflict. InMemoryMemoryStore enforces identical rules.

Stored Text retains full generated text. DeliveryMode records the interaction mode in which the entry was produced and never changes if the session later switches mode. ReceivedTextEndExclusive is the last validated browser-rendered prefix; public history projects only that prefix for assistant entries, so reconnect never newly reveals an unreceived generated tail. User entries set both offsets to Text.Length. On supersession, freeze HeardTextEndExclusive and ReceivedTextEndExclusive at their last validated values; late playback events/receipts do not revise that response. HeardTextEndExclusive remains separately conservative for voice-delivered context using [Spoken Until](06-realtime-voice.md#spoken-until); rendering text is not evidence of hearing it. Prompt construction uses received prefix when DeliveryMode is Text and heard prefix when DeliveryMode is Voice.

## Write ordering and recovery

One persistence write is in flight per Session Runtime. Capture immutable snapshots and serialize write dispatch, coalescing pending periodic checkpoints to the newest state. The mailbox still accepts interrupt/end events while writes execute. On completion, advance durable revision only for the acknowledged snapshot; apply subsequent dirty state to the next revision. Never let an old snapshot overwrite new spoken-until or terminal state.

Durability boundaries: save on creation, accepted final user turn, response terminal, mode change, pause/disconnect and end. During active generation checkpoint at most once per second, preserving only currently assembled text and conservative heard/received offsets. A 1-second streaming checkpoint **must not** rewrite every historical conversation row. Persistence identifies/upserts changed or currently streaming entries plus changed snapshot/session metadata. Immutable completed historical entries are not rewritten every checkpoint. Transactional SaveAsync, expectedRevision and optimistic-concurrency semantics are unchanged; Snapshot.Entries in the in-memory contract may still list the full session, but the Infrastructure writer must not issue no-op UPDATEs for unchanged completed rows. User turn generation waits for its save acknowledgement; final response completion waits for terminal save acknowledgement. Checkpoints cannot mark a still-streaming response completed. Temporary storage failure pauses new generation, emits recoverable SessionPersistenceUnavailable and retries local save after 1/2/5 seconds (maximum 3 retries); continued failure cancels active output, invalidates the attachment and pauses a non-ending session for explicit reconnect. An Ending session remains Ending until its terminal save succeeds; it cannot resume. Conflict is fatal for that runtime because it indicates a second writer or bug.

A crash may lose up to the last checkpoint interval of streaming text/progress, but not a user turn whose save succeeded. On startup/load, reconcile Attached→Paused and Streaming→Interrupted transactionally; keep the last recorded heard offset, never assume unacknowledged audio was heard. Ending recovers by completing the end transaction before attach; it cannot return to active. Provider streams, audio, timer generations and connection leases are never resumed.

Memory categories: ephemeral working state; persistent conversation history; persistent session summary; minimal persistent user profile. Initially only language and preferredName are allowed profile keys, populated by local demo setup through IMemoryStore, not inferred freely from conversation. No profile CRUD UI/API, embeddings, vector search or Redis.

PostgreSQL migration later replaces EF provider, revisits GUID/time conversions, migrations and concurrency tests; business interfaces stay stable. Provider change alone does not magically make SQLite-specific SQL portable, so avoid provider SQL outside Infrastructure and test migration data explicitly.

## Strongly typed options

Bind/validate on startup with standard .NET options and ValidateOnStart. These option type names match the root sections below. Nested providers use capability-specific records; do not inject IConfiguration into Domain/Application. Session factory receives a validated effective policy snapshot.

| Options type/section | Fields and purpose |
| --- | --- |
| AgentCoreOptions / AgentCore | Profile (Synthetic default), definition directory, session limits (`MaxActiveSessions` counts in-memory runtimes only), reconnect grace, mailbox capacity, context budgets |
| ProvidersOptions / Providers | LanguageModels, SpeechRecognizers, SpeechSynthesizers maps keyed by logical aliases; each has Adapter and capability-specific configuration |
| InteractionOptions / Interaction | Candidate thresholds, classifier deadline, ducking and degraded policy |
| VoiceOptions / Voice | Canonical format, frame size, queue budgets, utterance limit, playback progress |
| PersistenceOptions / Persistence | Provider, connection string, checkpoint interval, busy timeout |
| ObservabilityOptions / Observability | Logging level, timeline limit, content logging opt-in, OTLP enable/endpoint |
| HostingOptions / Hosting | Same-origin/default local binding, allowed development origins, development proxy behavior |

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
    "SpeechRecognizers": {
      "primary-stt": {
        "Adapter": "Synthetic",
        "BaseUrl": "https://speech.example/v1/",
        "ApiKey": "",
        "DefaultModel": "configured-stt-model",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120}
      }
    },
    "SpeechSynthesizers": {
      "primary-tts": {
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
    "DuckTimeoutMs": 600
  },
  "Voice": {
    "Encoding": "pcm_s16le", "SampleRateHz": 24000, "Channels": 1,
    "FrameDurationMs": 20, "InputQueueMs": 500, "OutputQueueMs": 2000,
    "PrebufferMs": 60, "MaxUtteranceSeconds": 30,
    "PlaybackProgressMs": 100, "PlaybackAckTimeoutMs": 5000,
    "Vad": {
      "NoiseFloorAdapt": 0.05, "StartThreshold": 0.7, "StartHangFrames": 3,
      "EndThreshold": 0.4, "EndHangFrames": 15, "MinActivityMs": 120,
      "ActivityScoreSmoothing": 0.3
    },
    "Segmentation": {"MinCharacters": 20, "ClauseMinCharacters": 40, "MaxDelayMs": 300, "SoftMaxCharacters": 120, "HardMaxCharacters": 240, "MaxPendingSegments": 4}
  },
  "Persistence": {"Provider": "InMemory", "ConnectionString": "Data Source=data/agent-core.db", "CheckpointMs": 1000, "BusyTimeoutMs": 5000},
  "Observability": {"LogLevel": "Information", "TimelineCapacity": 500, "LogConversationContent": false, "OtlpEnabled": false, "OtlpEndpoint": "http://localhost:4317"},
  "Hosting": {"BindUrl": "http://localhost:5080", "AllowedOrigins": ["http://localhost:5173"], "UseViteProxy": true}
}
```

Configuration fields for inactive adapters are ignored after structural validation; Synthetic must never attempt example URLs or require ApiKey. Bind operator secrets from environment or user-secrets only: `OPENROUTER_API_KEY` into the OpenRouter-configured language-model `ApiKey`, and `OPENAI_API_KEY` into the OpenAI STT and TTS `ApiKey` fields. Nested `Providers__...__ApiKey` remains valid. Neither variable is required for Profile=Synthetic, default tests, or application boot in synthetic mode. Real profile requires an explicitly configured non-synthetic LLM. STT/TTS adapters are required only when a loaded definition has `Voice.Enabled`. Preferred real configuration: OpenAICompatibleLanguageModel (Adapter=OpenAICompatible) pointing at OpenRouter for text with default test `DefaultModel` `openrouter/free`; OpenAiSpeechRecognizer (Adapter=OpenAI, recommended DefaultModel `gpt-live-transcribe`) over realtime transcription; OpenAiSpeechSynthesizer (Adapter=OpenAI) for TTS. A Real **voice** process still needs speech keys when those adapters are selected; a Real **text** process needs only the OpenRouter key. Missing `OPENAI_API_KEY` must not fail synthetic/offline tests or block completing the text-conversation path. OpenAICompatibleBatch and OpenAICompatibleSpeech are optional degraded/compatible speech adapters, not the primary streaming demonstration.

**Effective capabilities** come from the selected adapter. Optional `RequiredCapabilities` lists flags that must be **true**. Optional `DisabledCapabilities` may turn off optional adapter features (for example disable partials to test `speechAndFinal`). Do not use `RequiredCapabilities: false` to describe a batch adapter; select `OpenAICompatibleBatch`, which reports streaming/partials as false. Do not use a configuration `Capabilities` object to make an adapter claim unimplemented behavior. Built-in `OpenAI`/`Synthetic`/`Scripted` adapters define their own capabilities (Synthetic may withhold partials via adapter-specific test options). Generic compatible/local adapters may document operator assertions validated by contract tests. `heuristic` is the built-in classifier alias. `MaxActiveSessions` applies to in-memory runtimes on attach/activation; durable Created/Paused/Ended rows do not consume a slot.

Example .NET environment overrides for OpenRouter text plus an **optional batch-STT degraded speech configuration** (operator supplies actual secrets later; these are placeholders):

```text
AgentCore__Profile=Real
Persistence__Provider=Sqlite
Persistence__ConnectionString=Data Source=data/agent-core.db
Providers__LanguageModels__primary-llm__Adapter=OpenAICompatible
Providers__LanguageModels__primary-llm__BaseUrl=https://openrouter.ai/api/v1/
Providers__LanguageModels__primary-llm__DefaultModel=openrouter/free
OPENROUTER_API_KEY=<backend-secret>
Providers__SpeechRecognizers__primary-stt__Adapter=OpenAICompatibleBatch
Providers__SpeechSynthesizers__primary-tts__Adapter=OpenAICompatibleSpeech
Providers__SpeechSynthesizers__primary-tts__DisabledCapabilities__TimingMarks=true
OPENAI_API_KEY=<backend-secret-when-using-openai-speech>
Hosting__AllowedOrigins__0=http://localhost:5173
```

Operators must also set real speech BaseUrl/DefaultModel/voice mapping for their selected endpoints. Environment keys with hyphens are supported by .NET configuration but are not valid shell variable assignment names; supply via a process environment map or quoted `env 'key=value'` arguments during implementation. Keep keys in backend user-secrets or environment, never VITE_* or React bundles. Production same-origin serving needs no permissive CORS. Restrict development origins; do not combine wildcard origins with credentials.

## Local personal workspace

Repository root `local/` is gitignored. Operators and agents may store personal configs, scratch files and secret drop-files there; nothing under `local/` is shared in git. [Repository Structure](11-repository-structure.md) records the folder; this section owns secret-handling rules.

`local/` is **not** an application configuration source. Do not bind options from files in `local/`, do not load it with `IConfiguration`, and do not copy its contents into committed `appsettings*.json`. Runtime continues to bind `OPENROUTER_API_KEY` / `OPENAI_API_KEY` from environment or ASP.NET user-secrets only.

Named drop-file for hosted text: `local/open-router-key.txt`. The operator pastes the OpenRouter API key as the first non-empty line, trimmed, with no quotes and no `Bearer ` prefix. This file is a **one-time bootstrap** only. When the API project first exists, copy it into user-secrets on `src/AgentCore.Api` (for example `dotnet user-secrets set OPENROUTER_API_KEY <trimmed-value> --project src/AgentCore.Api`) or into the equivalent process environment. After that, day-to-day Real runs and opt-in live tests use `OPENROUTER_API_KEY` from user-secrets or environment; do not keep reading the drop-file. Never print the key in chat, logs, traces, docs, commits or command history that will be shared. Missing or empty `local/open-router-key.txt` is not a test failure and must not block Synthetic/default suites. Other personal files may live in `local/`; do not invent additional named secret drop-files until a spec names them.

## Hosted and on-prem provider configurations

Portable capability aliases live in Agent Definition/provider selection; concrete endpoints, model IDs, headers and secrets live only in backend Infrastructure options. Keep the existing LanguageModels/SpeechRecognizers/SpeechSynthesizers maps: each capability can be selected independently and several text aliases can target different models for different identities. Do not replace them with one gateway-wide AI provider.

Preferred hosted text override (merge into the synthetic example when Profile=Real; configure speech separately):

```json
{
  "Providers": {
    "LanguageModels": {
      "primary-llm": {
        "Adapter": "OpenAICompatible",
        "BaseUrl": "https://openrouter.ai/api/v1/",
        "ApiKey": "<OPENROUTER_API_KEY>",
        "DefaultModel": "openrouter/free",
        "AdditionalHeaders": {},
        "Timeouts": {"SetupSeconds": 10, "StreamIdleSeconds": 20, "TotalSeconds": 120},
        "Capabilities": {"StreamingText": true, "Cancellation": true}
      }
    }
  }
}
```

DefaultModel is the concrete model field; it is not copied into ModelRequest or public DTOs. The default hosted test value is OpenRouter's Free Models Router id `openrouter/free`, a stable router slug rather than a transient catalog model. Quality and structured-output correctness of whatever free model is routed are not configuration or milestone gates. To compare identities later, configure aliases `examiner-llm` and `support-llm` with different DefaultModel values, then select those aliases in each definition's ProviderPreferences.LanguageModel. No Agent Runtime condition branches on a provider/model name.

To migrate text inference on-prem, retain Adapter=OpenAICompatible and replace BaseUrl with e.g. `http://localhost:8000/v1/`, DefaultModel with the served local model name, and ApiKey/AdditionalHeaders with the local server's requirements. A vLLM-compatible endpoint is an example, not mandatory infrastructure. Direct OpenAI can use `https://api.openai.com/v1/` with its own credentials/model and verified compatibility settings. OpenRouter remains a hosted gateway; changing its model ID does not make inference local.

For STT and TTS, initially choose the OpenAI adapters (STT: realtime transcription, recommended DefaultModel `gpt-live-transcribe`), then independently replace the adapter alias, endpoint, model, credentials and required/disabled capability constraints as needed. Prefer streaming STT with partials and streaming TTS; effective capabilities come from the adapter. Implement those adapters on schedule, but default automated tests keep Synthetic speech until `OPENAI_API_KEY` is supplied. Local speech may need a different concrete adapter if its protocol differs, but never changes ISpeechRecognizer/ISpeechSynthesizer or controller logic. All secrets/endpoints can be overridden using the same .NET double-underscore syntax, including:

```text
Providers__SpeechRecognizers__primary-stt__BaseUrl=<hosted-or-local-stt-endpoint>
Providers__SpeechRecognizers__primary-stt__DefaultModel=<stt-model>
Providers__SpeechRecognizers__primary-stt__ApiKey=<backend-secret-or-empty>
Providers__SpeechSynthesizers__primary-tts__BaseUrl=<hosted-or-local-tts-endpoint>
Providers__SpeechSynthesizers__primary-tts__DefaultModel=<tts-model>
Providers__SpeechSynthesizers__primary-tts__ApiKey=<backend-secret-or-empty>
Providers__SpeechSynthesizers__primary-tts__Voices__default=<tts-voice>
```

[Technology Decisions](10-technology-decisions.md) owns the provider rationale. No actual configuration files are created by this documentation update; React receives only safe effective capabilities, never credentials, model IDs or endpoints.


## Provider selection and DI

Bind strongly typed SpeechRecognitionProviderOptions, LanguageModelProviderOptions and SpeechSynthesisProviderOptions as the values of the existing SpeechRecognizers, LanguageModels and SpeechSynthesizers option maps. At startup the API composition root resolves each Adapter value to an Infrastructure implementation and registers the corresponding capability through DI. Within the speech maps, `OpenAI` selects OpenAiSpeechRecognizer/OpenAiSpeechSynthesizer; `Synthetic` selects their synthetic counterparts. `Local` below denotes a future installed local adapter, not automatic compatibility with every local server. An unknown/uninstalled adapter fails configuration validation.

These are Markdown-only selection overrides merged with the existing full options example, not additional configuration schemas or actual files. Each selected adapter reports effective capabilities; optional RequiredCapabilities/DisabledCapabilities and timeouts/model/voice configuration still apply. Provider-specific model IDs and secrets stay in backend options.

Initial hosted configuration:

```json
{
  "Providers": {
    "SpeechRecognizers": {"primary-stt": {"Adapter": "OpenAI", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "gpt-live-transcribe", "ApiKey": "<OPENAI_API_KEY>"}},
    "LanguageModels": {"primary-llm": {"Adapter": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1/", "DefaultModel": "openrouter/free", "ApiKey": "<OPENROUTER_API_KEY>"}},
    "SpeechSynthesizers": {"primary-tts": {"Adapter": "OpenAI", "BaseUrl": "https://api.openai.com/v1/", "DefaultModel": "<tts-model>", "Voices": {"default": "<voice>"}, "ApiKey": "<OPENAI_API_KEY>"}}
  }
}
```

Synthetic configuration (Profile=Synthetic; no keys or external calls):

```json
{
  "Providers": {
    "SpeechRecognizers": {"primary-stt": {"Adapter": "Synthetic"}},
    "LanguageModels": {"primary-llm": {"Adapter": "Scripted"}},
    "SpeechSynthesizers": {"primary-tts": {"Adapter": "Synthetic"}}
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

Use the documented double-underscore environment overrides for endpoints and secrets in native processes or Compose. When moving local, explicitly clear/replace any inherited hosted ApiKey and headers; the examples do not imply a public unauthenticated local service. React never receives credentials. Updating configuration/DI bindings is not an Agent Runtime code change. A provider lacking partials or timing marks selects existing capability-based policies; it does not require a new controller design. A new protocol may require a new Infrastructure adapter only.
