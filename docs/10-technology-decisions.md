# Technology Decisions

These decisions are the implementation baseline. Resolve package patches at implementation time to the latest supported compatible releases; do not scatter transient patch pins across these docs. .NET 10 is LTS and pairs with C# 14. See the [official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) and [.NET 10 download/version information](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

| Area | Decision | Rationale / trade-off | Future migration path |
| --- | --- | --- | --- |
| Backend | .NET 10 LTS, C# 14, ASP.NET Core 10 Minimal APIs | One supported runtime, simple HTTP surface | Upgrade supported runtime as one unit |
| Structure | Modular monolith, Domain/Application/Contracts/Infrastructure/Api | Explicit boundaries without distributed operations | Services only for measured scaling needs |
| Concurrency | System.Threading.Channels, one async mailbox reader per session | Deterministic state ownership; bounded queue design required | Scale independent sessions, preserve ownership |
| Live transport | ASP.NET Core SignalR + MessagePack, WebSockets | Small bidirectional binary surface; application handles PCM buffering | WebRTC adapter if media constraints justify it |
| JSON | System.Text.Json | Built into platform; explicit versioned records | Schema-version migration |
| Persistence | EF Core 10 + SQLite | Single-app operation, transactional history; one-writer constraints | PostgreSQL EF provider with tested conversions/migrations |
| Providers | Narrow application ports, first LLM direct OpenAI-compatible HttpClient/SSE | Vendor neutrality; own parser/error handling | More adapters, no runtime vendor objects |
| HTTP lifetime/resilience | IHttpClientFactory + Microsoft.Extensions.Http.Resilience | Managed clients and explicit failure budgets | Tune measured policy; no blind stream replay |
| Time/IDs | TimeProvider, injectable IIdGenerator; UUIDv7 event/response IDs, random UUIDv4 session IDs | Deterministic tests; session IDs retain random bearer entropy | Keep wire UUID strings stable |
| Speech | Independent STT/TTS, canonical PCM16 mono 24 kHz | Canonical composed text-first pipeline; format conversion at adapters | Future-only INativeRealtimeProvider optimization |
| Definitions | Versioned JSON files via store | No YAML dependency; immutable identities | Alternative store preserving schema/version semantics |
| Frontend | React SPA, Vite, pnpm, strict TypeScript, Ant Design v6, minimal app-specific CSS | Simple personal chat; client-only audio; generic controls come from AntD rather than a custom visual system | Expand UI only for product needs; do not add a second UI kit |
| Client state/API | Zustand; fetch wrapper; @microsoft/signalr + @microsoft/signalr-protocol-msgpack | Small active state store, few HTTP endpoints | Add data caching only with evidence |
| Observability | Microsoft.Extensions.Logging, OpenTelemetry via ActivitySource/Meter | Correlate conversational latency with privacy defaults | Optional OTLP export |
| Tests | xUnit, WebApplicationFactory; Vitest, React Testing Library, Playwright | Offline deterministic behavior and boundary tests | Explicit opt-in live-provider smokes; skip when keys are missing |
| Follow-on history | Evolve `IMemoryStore`; durable `LastEntrySequence`; newest/`before`/`after` pages | Open long sessions without a full transcript | Implement in follow-on P1A ([decision](#decision-bounded-history-and-durable-lastentrysequence)); **observed** for P1A restore, paging, and Load earlier messages |
| Follow-on lifecycle | Additive `lifecycleStatus`; one `TransitionLifecycle` | Semantic outcomes stay distinct from protocol-v1 `status` | Implement in follow-on P1B ([decision](#decision-additive-semantic-lifecycle-beside-protocol-v1-status)); **observed** |
| Follow-on speech locale | Effective locale: session override > agent default > fallback | Speech locale stays independent of text language and of SessionRuntime vendor branches | Implement in follow-on P1C ([decision](#decision-provider-neutral-effective-speech-locale)); **observed** including real Chrome `fr-FR` STT/TTS smoke |
| P2D session model | Trusted catalog, system default, persisted per-session resolved choice, session-aware resolver, reasoning effort | Existing sessions stay pinned when the operator default changes | Implement in P2D ([decision](#decision-session-model-selection-and-inference-controls)); **observed** |

MessagePack is case-sensitive; use explicit camelCase string keys and binary DTOs tested with the JavaScript client, as described in [Microsoft's SignalR MessagePack documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-10.0). Http resilience policies must explicitly account for unsafe methods; see [Microsoft HTTP resilience guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience). Project-specific timeout/retry choices are specified in [Backend Implementation](12-backend-implementation-spec.md), not implied library defaults.

## Decision: composed voice pipeline

**Decision:** The primary and default MVP voice architecture is STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS. It does not use speech-to-speech reasoning. Input capture remains active while output plays.

**Rationale:** Independent capabilities simplify testing, debugging and provider replacement; preserve Agent Core's control of interruption; support different models for different identities; permit deterministic synthetic providers and per-stage latency measurement; and support a future fully on-prem deployment.

**Trade-off:** Extra recognition, segmentation and synthesis stages can add latency compared with a native end-to-end speech model.

**Mitigation:** Stream STT and use partials when available, apply fast controller heuristics, stream model text into natural Speech Segments, stream TTS into AudioWorklet playback, optionally duck locally, and measure each stage separately.

**Future migration:** INativeRealtimeProvider may later offer an optional optimized path while preserving normalized events and response/session invariants. It is excluded from all MVP implementation milestones and must not complicate the composed path.

## Decision: OpenRouter first for hosted text reasoning

**Decision:** Prefer OpenRouter as the initial hosted text gateway, configured through OpenAICompatibleLanguageModel. It is a configuration choice, never an Agent Core dependency or reasoning interface. The gateway exposes an OpenAI-style endpoint at `https://openrouter.ai/api/v1/`; see the [OpenRouter quickstart](https://openrouter.ai/docs/quickstart). Implement and configure this adapter during the existing milestone plan (Milestone 3). `openrouter/free` (the [Free Models Router](https://openrouter.ai/docs/guides/routing/routers/free-router)) selects free models at random, so it is **not** a Real/demo `DefaultModel`. Opt-in adapter smoke still uses it. The shipped Real catalog also offers it and `openai/gpt-4o-mini-2024-07-18` as explicit session choices; the system default remains `deepseek/deepseek-v4.1-flash` (development/demo only). Set `AGENTCORE_LLM_MODEL` to another catalog `ModelId` to retarget `DefaultKey`; extend the catalog for IDs that are not listed. Do not set it to `openrouter/free`. The demo/Real profile requires an operator-selected **fixed** OpenRouter model ID as that default. OpenRouter rate limits and catalog terms still apply. Exact smoke-routed quality and structured-output correctness are not milestone gates. Read `OPENROUTER_API_KEY` from environment or `dotnet user-secrets` only; do not commit secrets and do not specify a named secret drop-file. Default tests remain Synthetic/offline and must pass without this key. Real OpenRouter smoke tests are explicit opt-in and skip cleanly when the key is missing.

**Rationale:** A hosted gateway makes it practical to experiment with model families and compare conversation latency, quality and cost while retaining one adapter protocol. The free router unblocks live adapter smoke without treating catalog quality as an acceptance criterion. Identity consistency and latency are product qualities, so the demo path must not randomly switch models.

**Trade-off:** OpenRouter is hosted, not on-prem. Its model catalog, rate limits, parameters and streaming details vary; compatibility needs contract tests rather than assumptions. `openrouter/free` may route to different free models per request; do not treat a session that selected it as a quality or structured-output gate.

**Future migration:** Replace the configured endpoint and model mapping with a local OpenAI-compatible inference server; select local STT and TTS independently. vLLM is one example with a [compatible Chat API](https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/), not a required dependency. Direct OpenAI and other compatible hosted providers are also possible configurations. Operators choose the Real/demo `DefaultModel`. Agent Runtime and Interaction Controller do not change.

[Configuration](15-persistence-and-configuration.md#hosted-and-on-prem-provider-configurations) owns examples and alias/model resolution; [Operations](17-observability-and-operations.md#hosted-hybrid-and-on-prem-deployment) owns deployment topologies.

## Decision: STT/TTS are replaceable providers

**Decision:** Hosted STT and TTS remain independently replaceable Infrastructure adapters. Observed selectable hosted speech is OpenAI TTS (`OpenAiSpeechSynthesizer`) and OpenAI-compatible batch STT (`OpenAICompatibleBatchSpeechRecognizer`, no streaming input and no interim partials). OpenAI realtime transcription (`OpenAiSpeechRecognizer`, recommended model `gpt-live-transcribe`) stays the planned streaming STT adapter and is **not selectable** while its live session remains a no-op. Optional vendor transcription parameters such as `delay` are adapter-local and are sent only when the selected API/model documents them; they are not Agent Core defaults. Changing STT, LLM or TTS must not require changes to Agent Runtime or Interaction Controller. Do not block development, default tests or CI on a real OpenAI key. Automated tests use Synthetic STT/TTS plus fake Browser transports. Real OpenAI STT/TTS integration tests and manual voice verification wait for explicit opt-in plus `OPENAI_API_KEY`. Missing OpenAI credentials must not fail normal build/test.

**Rationale:** Support future on-prem deployment, deterministic testing, cost/quality experimentation, no vendor lock-in and independent evolution of speech and reasoning. Speech credentials arrive later than the text adapter without changing architecture.

**Consequence:** Application depends on capability interfaces; configuration and DI select Infrastructure adapters. Capabilities may differ, so existing degraded interaction policies remain supported. Vendor-specific code stays at the edge. [Provider ports](04-backend-interfaces.md#speech-provider-replacement-rule) and [configuration](15-persistence-and-configuration.md#provider-selection-and-di) own the concrete contracts and examples.

## Decision: speech provider is not speech transport

**Decision:** Independently configured STT and TTS adapters are Infrastructure providers. The session's input and output **transports** are provider-neutral: `serverAudio` (backend port + PCM), `clientTranscript` (browser STT, no `ISpeechRecognizer`), and `clientSpeech` (browser TTS, no `ISpeechSynthesizer`). `SpeechFactory` produces an `EffectiveSpeechPlan` and optional backend ports. Browser is a client transport, not a fake backend adapter; the plan still carries explicit effective client capabilities so Interaction Controller interruption timing is not degraded to no-partials. A selected non-Synthetic adapter must not be silently replaced by Synthetic. The deferred OpenAI realtime recognizer is not selectable while its live session remains a no-op; `OpenAICompatibleBatch` is the supported hosted STT adapter and must advertise no interim partials.

**Rationale:** Session Runtime and Interaction Controller stay replaceable-provider-agnostic. Mixing Browser STT with a future hosted TTS (or the reverse) is a transport pair, not a vendor mash-up inside the mailbox.

**Consequence:** [Architecture](03-system-architecture.md) owns the ownership split; [Interfaces](04-backend-interfaces.md#speech-provider-replacement-rule) owns ports versus Browser; [Voice](06-realtime-voice.md) owns how PCM versus client transcripts enter the pipeline; [Configuration](15-persistence-and-configuration.md#provider-selection-and-di) owns adapter binding.

## Decision: default verification is offline; live providers are explicit opt-in

**Decision:** Default verification is fully offline and deterministic (Synthetic adapters plus HTTP/SSE fixtures). External-provider smoke tests run only when the operator explicitly opts in. Presence of `OPENROUTER_API_KEY` or `OPENAI_API_KEY` in the environment must not cause `dotnet test` or other default suites to call hosted APIs. Opt-in smokes skip cleanly when the required key is missing. Normal development/test loops must not silently spend API credits. Deferred live-provider testing does not weaken or skip synthetic contract tests. Do not introduce additional external services solely for testing.

**Rationale:** Keep milestone gates reproducible without secrets or network, while still allowing a bounded live check of the real adapters when keys exist.

**Trade-off:** Hosted speech quality and free-router phrasing remain unverified until an explicit smoke or manual pass.

**Consequence:** [Testing Strategy](16-testing-strategy.md) owns fixtures, skip/opt-in mechanics and suite gates. [Implementation Plan](18-implementation-plan.md) applies this policy to existing milestones rather than adding test-only architecture.

## Decision: local git CLI now; GitHub Actions is intended CI

**Decision:** Day-to-day implementation uses the local `git` CLI. Intended repository CI is **GitHub Actions**. Do not create full application workflow files until the corresponding .NET/`web` projects and test scripts exist. When introduced, core CI must remain offline: `dotnet` build/test, frontend install/build/unit tests, then synthetic Playwright after those milestones, never requiring OpenAI/OpenRouter keys, internet inference, microphone, speaker or GPU. Real-provider smokes stay opt-in.

**Rationale:** Keep early work unblocked, then make the promised offline gates reproducible on the repository host.

**Consequence:** [Implementation Plan](18-implementation-plan.md) and [Testing Strategy](16-testing-strategy.md) own when workflows appear. Milestone 5 adds `.github/workflows/synthetic.yml` for key-free backend, frontend, and synthetic Playwright gates.

## Decision: Docker Compose for reproducible local integration

**Decision:** Docker is optional for development; Docker Compose is supported for reproducible integration/demo. The fast loop is native .NET + Vite. The production-like MVP artifact is one application container serving API, SignalR and built SPA, with a persistent SQLite volume.

**Rationale:** Provide a one-command demo environment, environment parity and a simple path to adding local inference services later.

**Trade-off:** Container builds/restarts are slower than the native hot-reload loop, so Compose does not block early runtime/frontend work.

**Consequence:** Add the first usable key-free Compose environment at Milestone 5 (after the synthetic browser text path). Milestone 12 hardens containers, SQLite volume/restart, real-provider configuration, hybrid topology and operations. Default `docker compose up` remains Synthetic. Real/hosted Compose is `docker-compose.real.yml` overlaid on that file; secrets stay in the host environment. Hosted, hybrid and on-prem topologies keep the same runtime semantics. Docker is deployment tooling, not an Agent Core dependency. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns topology and workflow details.

## Decision: Ant Design v6 as MVP generic UI system

**Decision:** The MVP generic UI system is **Ant Design v6** (`antd`), consumed directly by product components, with ConfigProvider and Ant Design `App` at the application root and near-default theming. App-specific CSS is limited to sizing, overflow, product layout/content, preview sizing, and necessary accessibility fixes. Do not add AppButton/AppInput/AppSelect or other generic wrappers, Ant Design Pro/ProComponents/X, another component or CSS framework, or a replacement custom design system.

**Status:** **Verified.** The SPA presents identity, session navigation, transcript, and composer with Ant Design v6. The retired custom visual system is not an active fallback. Session, realtime, voice, attachment, and catalog contracts remain with their existing owners. Evidence: [antd-migration-handoff](reports/antd-migration-handoff.md).

**Rationale:** A custom Pixel Dialogue Field visual system was unnecessary cost for validating Agent Core product behavior. Ant Design is an explicit UI-system change only.

**Consequence:** [Frontend Implementation](13-frontend-implementation-spec.md) owns screens and behavior. `.agents/context/DESIGN.md` may hold only lightweight visual guidance; if it conflicts with `/docs`, `/docs` wins.

## Decision: history-derived user-text queue

**Decision:** Wire clients express queued user text with additive `user.text.behavior` of `queue` or `interrupt`. Omitted `behavior` remains `interrupt` for protocol-v1 clients. Unknown values are rejected. Accepted user entries (and their attachment binds) persist on the existing ordered conversation history before success ACK. The pending batch is that history's trailing user suffix; P0 does not add a second durable queue store, `PendingUserEntryIds`, or `deliveryPolicy`. `queue` while a response is active keeps that response alive. `interrupt` supersedes the active response after the new user entry is persisted so already accepted queued entries stay earlier in sequence. The shipped first-party web app uses a client-side pending-send FIFO while a response is live ([Frontend](13-frontend-implementation-spec.md)); it does not send `behavior=queue` for that UX. Explicit stop is `agent.response.cancel` / `CancelResponse` with the top-level expected `responseId`; it creates no user entry and must not cancel a newer response.

**Status:** **Verified** in Application fake-time/gated-model tests, hub admission tests, and loopback Kestrel JavaScript SignalR/MessagePack scenarios.

**Rationale:** A parallel queue table would have to stay reconciled with history. Ordered entries already express arrival, attachments, and recovery without command replay.

**Consequence:** [Interaction Controller](05-interaction-controller.md) and [Protocol](14-api-and-realtime-protocol.md) own the live contracts. After a response is durably terminal (complete, fail, or explicit cancel), the runtime starts at most one next user-turn for the current trailing user suffix. Restart/attach uses the same helper on durable history. A second store remains forbidden unless a later proven invariant is documented first.

## Decision: bounded history and durable LastEntrySequence

**Decision:** Evolve `IMemoryStore` rather than adding a repository framework. Session metadata, one bounded runtime restore window, and one public history page must be loadable without materializing the full transcript. `LastEntrySequence` is a first-class durable snapshot coordinate, independent of the in-memory `Entries` window. Bounded saves retain older `ConversationEntries` rows and must not renumber them. Runtime restore includes required prompt context, interrupted response/delivery coordinates, and the complete trailing unresolved user suffix. The existing messages HTTP endpoint is extended for limit-only newest, `before` older, and existing `after` forward pages; `before`+`after` is rejected; newest/backward pages take `limit+1` internally, return items ascending, and expose `hasOlder` plus the next older cursor. One public history projection applies to active, paused, and terminal sessions. The first-party UI opens on the newest page and uses an explicit Load earlier messages control with merge/dedup by stable identity/sequence and scroll-anchor preservation on prepend.

**Status:** **Observed** for bounded `IMemoryStore` restore, HTTP newest/`before`/`after` paging, first-party Load earlier messages, Application `LifecycleTransition` with additive public `lifecycleStatus`, RequestComplete evaluation, minimal terminal UI, Application effective speech locale (override > agent default > fallback) on readiness/capability data, Browser/hosted adapter locale wiring, and Speech locale UI. P1-Final deterministic Synthetic/Compose gates and the unedited Chrome 153 Voice checklist re-run (`p1-final-chrome-probe-run.log`) are recorded on HEAD `df0a12cecb5b60a12499488eed9c101cb01b45b2`. The unedited `fr-FR` Browser STT/TTS smoke (`p1-final-fr-smoke-r2.json` / `p1-final-fr-smoke-r2.log`) is recorded on HEAD `5764010d989da965b252f5389e07669594c8bc29`. Optional hosted multilingual checks are unverified. **P1 freeze:** `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`, 2026-09-19) with CI/Synthetic + Compose green on that HEAD. Historical repair `15930985f54e2e6bf4019dd0d8040796883021c7` remains earlier evidence. P2A/P2B have not started.

**Rationale:** Opening a long session must not transfer or materialize the entire transcript. Deriving the cursor from `Entries[^1]` is wrong once `Entries` is a window.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns store operations; [Persistence](15-persistence-and-configuration.md) owns durable cursor and row retention; [Protocol](14-api-and-realtime-protocol.md) owns paging query shape; [Frontend](13-frontend-implementation-spec.md) owns lazy history UX; [Testing](16-testing-strategy.md) and [Implementation Plan](18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech) own gates.

## Decision: additive semantic lifecycle beside protocol-v1 status

**Decision:** Do not extend today's `created|attached|paused|ending|ended` enum as the semantic model. Introduce durable `lifecycleStatus` conceptually Active/Paused/Completed/Expired/Cancelled/Ended additively, while protocol-v1 `status` remains compatible during migration. Archive (`ArchivedAt`) stays orthogonal. Generic `SessionPurpose` is Ongoing|Goal with optional description (context, not an executable rule), optional absolute `deadlineAt` (resolve `maxDuration` once at create), and optional bounded opaque host metadata that is not public. Completion-authority policy covers agent disabled/advisory/allowed, user complete/cancel allowed/denied; host/system authority is always allowed. One Application `TransitionLifecycle` owns the graph, terminalization obligations, and idempotent same-outcome repeats. Attached deadlines use TimeProvider mailbox timers; detached admission checks expiry atomically with no durable scheduler. A separate configured completion evaluator may yield `continue` or `RequestComplete`; it is not `RequestDeactivate` and is not an inline response marker. Domain-specific completion rules stay in host integrations.

**Status:** **Observed** for purpose/policy persistence, `LifecycleTransition` graph/terminalization, attached TimeProvider deadlines, detached atomic expiry, the RequestComplete evaluator (continue / advisory intent / allowed-after-terminalization), and the first-party terminal read-only UI.

**Rationale:** Mixing transport attachment with semantic outcomes would break protocol-v1 clients and collapse Completed/Expired/Cancelled into one Stopped state.

**Consequence:** [Controller](05-interaction-controller.md) owns transition behavior; [Events](07-event-model.md) owns the corresponding lifecycle event; [Protocol](14-api-and-realtime-protocol.md) owns additive wire fields; [Persistence](15-persistence-and-configuration.md) owns migration including Ending recovery; [Frontend](13-frontend-implementation-spec.md) owns minimal terminal UI.

## Decision: provider-neutral effective speech locale

**Decision:** Effective speech locale precedence is session override > agent conversation-language default > provider/default fallback. Validate BCP-47-like tags at the Application boundary. Persist the session override without rewriting agent text-language settings. Expose the resolved locale on session readiness/capability data, not only the public agent descriptor. Evaluate language/voice support in speech abstractions/adapters; `SessionRuntime` has no Browser/OpenAI locale branches. Browser STT uses the effective tag. Browser TTS selects exact locale, then reasonable base language, then compatible configured/default voice, or fails Voice clearly. Hosted batch STT receives locale hints where supported; hosted TTS compatibility stays in adapters. Unsupported speech locale disables or fails Voice while text remains usable. Realtime OpenAI STT remains unselectable. No automatic language detection.

**Status:** **Observed** for Application validation/precedence, persisted session override without rewriting text language, public effective locale on session readiness/capability data, provider-neutral `ISpeechLocaleSupport` (SessionRuntime has no Browser/OpenAI locale branches), Browser STT/TTS locale selection, hosted batch STT language hints, hosted TTS locale/voice compatibility in adapters, a first-party Speech locale Select that does not hide text chat, and one real Chrome 153 `fr-FR` STT/TTS conversation (unedited `p1-final-fr-smoke-r2`: STT `Bonjour`; TTS Daniel French France) on HEAD `5764010`.

**Rationale:** Conversation language and speech locale must stay independent so missing voices never silently speak the wrong language or break text chat.

**Consequence:** [Voice](06-realtime-voice.md) owns Browser/hosted speech behavior; [Interfaces](04-backend-interfaces.md) owns capability evaluation; [Protocol](14-api-and-realtime-protocol.md) owns readiness fields; [Frontend](13-frontend-implementation-spec.md) owns override/fallback presentation.

## Decision: session model selection and inference controls

**Decision:** Operator configuration owns a trusted, provider-neutral model catalog and the system default. A new session may choose Default or an allowed catalog key; Default resolves to a concrete model when the session is created. Persist `SessionModelSelection` (catalog key, trusted provider alias, concrete model ID, selection source, reasoning effort). Default is never stored as a future dynamic pointer. Changing the global default, or the model behind the same catalog key, must not silently rewrite existing sessions. Legacy sessions without a selection pin the configured default before the first post-upgrade generation. Precedence is explicit host/user selection, then optional agent default/constraint, then the system default. Keep this generic; do not hard-code exam/application model IDs into Agent Core. The shipped Real catalog default is `deepseek-v41-flash` (`deepseek/deepseek-v4.1-flash`, medium). Allowed development choices also include `gpt-4o-mini-2024-07-18` (`openai/gpt-4o-mini-2024-07-18`, no reasoning-effort control) and `openrouter-free` (`openrouter/free`, not the system default).

Session-scoped LLM work resolves through `ILanguageModelResolver` from the persisted selection and a `ModelPurpose` (`Conversation`, `Initiative`, `CompletionEvaluation`). This first slice uses the session-selected model for all three purposes. Do not mutate singleton `LanguageModelProviderOptions` when a session changes model. Capture the model once when a generation or evaluation begins. Reasoning effort is request/session-scoped and validated by the selected catalog descriptor; it is not a global unchecked enum. Live changes persist first and activate second; an in-flight response, tool loop, or completion evaluation rejects the change with `SessionBusy`. Record per-turn model provenance on assistant generations. Public APIs accept only catalog-level choices. No per-message one-shot override, arbitrary provider JSON, credentials, or OpenRouter marketplace discovery in this slice.

**Status:** **Observed** as P2D after the key-free gate. P1 remains frozen on `dceaccb`. P2A is observed. P2B is **observed/frozen** on `e0e8a55`.

**Rationale:** Operators need a Codex-like default plus durable per-session control without letting browser input choose provider routing or silently retarget historical chats.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns catalog/resolver ports; [Persistence](15-persistence-and-configuration.md) owns durable selection and provenance; [Protocol](14-api-and-realtime-protocol.md) owns catalog and session model HTTP; [Frontend](13-frontend-implementation-spec.md) owns Default/effort UX; [Implementation Plan](18-implementation-plan.md) owns the P2D gate.

## Decision: first-class transient response progress

**Decision:** Live response progress is a first-class transient Application event (`ResponseProgressOutput`, wire `agent.progress`) owned by the outer envelope `responseId`. Nested tool or attachment work may carry a runtime-generated `operationId`. Kinds are `preparing`, `readingAttachments`, `runningTool`, `waitingExternal`, and `finalizing`; states are `started`, `updated`, `completed`, and `failed`. Progress is never durable history, `session.ready` replay, provider reasoning, conversational assistant text, response blocks, or TTS/`clientSpeech` input. Trusted bounded `message` values are status copy only. Coarse `session.state.changed.outputState` remains. The browser keeps one replaceable `activeProgress` status. Telemetry records `agent.progress.event` with `kind` and `state` tags only, plus optional kind-tagged `agent.progress.active_ms`.

**Status:** **Observed** as P2A after the key-free Synthetic plus Compose gate recorded on git HEAD `5effbb5e0942b2176c970c3a6f1b79fbaa985f8d` (`5effbb5`) with the P2A working tree (P2A-1..4 not a separate published commit). Domain 64; Infrastructure 133 passed / 12 skipped; Application 436 with `--blame-hang --blame-hang-timeout 5m`; API 155; web 376 unit tests and production build; `CI=1` Playwright 36 including progress-to-final, reload/history, disconnect/reconnect, and session-switch; `scripts/compose-sqlite-volume.sh` passed. Optional Real-provider probes were not run. P1 remains frozen on `dceaccb`. P2B is **observed/frozen** on `e0e8a55`.

**Rationale:** Operators and participants need a single understandable live status while attachments or tools run, without turning operational noise into history or speech.

**Consequence:** [Protocol](14-api-and-realtime-protocol.md) owns `agent.progress`; [Voice](06-realtime-voice.md) owns the TTS exclusion; [Operations](17-observability-and-operations.md) owns progress instruments; [Implementation Plan](18-implementation-plan.md) owns the P2A gate.

## Decision: validated provider-neutral assistant response

**Decision:** Conversational generation uses one validated semantic envelope (`displayText`, `speech.mode` `same`|`custom`|`none`, optional `blocks`). SessionRuntime always attaches `ModelResponseContract`. Native JSON is requested only when trusted catalog `StructuredOutput` is true; otherwise Infrastructure injects a bounded compatibility instruction and marker parser (including `[[speech:none]]`). Domain persists the envelope; provider JSON never crosses into Application. When Voice derives a playback coordinate that differs from display while `speech.mode` stays `same`, persistence stores that coordinate in `EnvelopeJson` `speech.text` without promoting it to public custom speech. Attachment references require a session-owned attachment id. Progress `finalizing` starts only at structured semantic validation (not during LLM streaming) and is never durable, reasoning, history, or TTS.

**Status:** **Observed** as P2B after the key-free Synthetic plus Compose gate on `e0e8a55b111b190b63dfe5a2a53d0c59d0a06a59` (`e0e8a55`, 2026-09-20; workflow run `35496178496`). Public/history `speechText` exposes custom semantic speech only. `agent.speech.projection` carries `mode` and may expose same-mode playback telemetry; clients must not promote same/none projections to public `speechText`. Do not reopen P2B without a reproducible regression. **P2E** multimodal image-input capability closure is **observed/frozen** (2026-09-21 gate on P2E freeze HEAD). **P2C** trusted personalization boundary is **observed/frozen** (2026-09-21 gate on P2C freeze HEAD). **P2 closed/frozen** on `47d6ff6` (2026-09-21) after mandatory whole-output review; closure repair and §23 gate on that HEAD (workflow run `35552740853`). Optional Real probes skipped/unverified. **P3B–P3E** (tools, public web, approval, email) freeze on `27efe17` was **reopened** for a focused email/approval correction (exact-draft Bcc, Gmail `drafts.send` with approved `message.raw`, indeterminate send consumption, MimeKit MIME, approval-clock isolation, policy-before-Gmail, multi-tool image wire order); correction freeze **`e255916`** (`general-assistant` v5 and `web.fetch` address fallback) — see the [P3 closure report](reports/p3-freeze-candidate.md). Public-web fetches validate every DNS answer used for connection and every redirect hop; private/local/metadata targets and IPv4-mapped equivalents are denied in Infrastructure before connect. Do not reopen P2 without a reproducible regression. Roadmap focus: **P4** after this P3 correction freeze. P6 has not started.

**Rationale:** Display, speech, and blocks must share one validated contract so Voice, history, and UI do not depend on inline markers or provider JSON.

**Consequence:** [Interfaces](04-backend-interfaces.md) owns semantic model events; [Voice](06-realtime-voice.md) owns TTS selection from `speech.mode`; [Protocol](14-api-and-realtime-protocol.md) owns public `speechText` and speech labels; [Implementation Plan](18-implementation-plan.md) owns the P2B gate.

No native speech-to-speech/realtime model in MVP. No microservices, Kafka, RabbitMQ, Redis requirement, Kubernetes requirement, Orleans/Akka actor framework, MediatR merely for layer forwarding, generic workflow engine, multi-agent system, vector database, RAG platform, plugin marketplace, OAuth/login system for MVP, WebRTC in the first version, mobile app, native desktop app, elaborate avatar system, SSR, or Next.js. Ant Design v6 is the verified MVP generic UI system (see the decision above); do not add another component or CSS framework, Ant Design Pro/ProComponents/X, or a replacement custom design system. No autonomous tools/platform, distributed event bus or generic repository framework. No extra hosted mock or third-party test-only inference service. Reconsider only when actual requirements justify the cost.

## Post-MVP planned until verified

Historical MVP acceptance is unchanged. Phases A–H and the Phase I decision are **observed** in the decisions below (and in [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified)). The heading is retained as a stable fragment. Do not invent a second numbered specification series. Product behavior is described here and in docs/01–18; the original proposal pack is not required to understand the system.

### Decision: trusted-local owner capability (R1)

**Decision:** One reusable **trusted-local owner capability** authorizes session catalog, lifecycle, hub attach, upload, bind, artifact access, workspace execution-view, and content retrieval. `SessionId` identifies the resource; it is not a credential. This is not tenant isolation, OAuth, or public multi-user hosting. Historical MVP still excludes those.

**Contract:**

- Obtain: `POST /api/v1/local/owner-capability` succeeds only for a trusted-local caller. Native processes require loopback `RemoteIpAddress`. Compose publishes `127.0.0.1:host:container` while Kestrel listens inside the container; Docker NAT may present that traffic as this container's IPv4 default gateway, so Compose sets `Hosting:TrustPublishedPortGateway=true` to trust **that one hop** when `DOTNET_RUNNING_IN_CONTAINER` is set (official runtime images). Gateway resolution is disabled outside a container even if the flag is true. Private ranges are not trusted. Response is an opaque token (not a SessionId) plus expiry metadata.
- Persist: hash the token in SQLite as a single local-owner grant so **API process restart** can still validate a restored token. The browser stores the token in `localStorage` (`agentcore.ownerCapability`) and restores it on reload; if missing, revoked, or hash-mismatch, re-obtain on loopback and fail closed.
- Present: HTTP header `X-AgentCore-Owner-Capability`. Hub `session.attach` requires the same token (capability field on the attach command, not the connection-lease `attachmentId`).
- Fail closed: unauthenticated → 401; wrong-owner, revoked, or cross-session → 403 or a uniform 404 that does **not** leak other sessions' existence. Deleted and archived sessions deny upload/bind/content without enumerating foreign ids. Safe errors omit other sessions' titles and ids.
- Tests: unauthenticated, wrong token, revoked, deleted, archived, cross-session, reload restore, API process restart restore.

Hub **connection lease** (`attachmentId` on SignalR commands) remains a per-connection protocol lease. It is not a user-uploaded **Attachment**. User-uploaded files use `AttachmentId` on HTTP upload/bind/content routes only.

### Decision: independent speech and display receipts (R2)

**Decision:** One parent `ResponseId` owns `reply.text`, optional `reply.speech`, Markdown, and attachment/artifact reference blocks. Speech-coordinate playback progress, display receipts, and block visibility/delivery persist atomically with response status.

**Contract:**

- Display receipts and speech-coordinate consumed samples are independent. Do not apply speech offsets to display text.
- Heard/voice context uses conservative speech receipts only.
- When `reply.speech` is absent, TTS synthesizes `reply.text` once. A later speech field on the same response must not duplicate TTS.
- Disconnect before render leaves display, speech, and block receipts undelivered; reconnect must not treat unseen content as shown.
- Late receipts/blocks after supersession or disconnect are rejected. Unknown blocks degrade to a safe fallback. Artifact-reference authorization in Phase C uses fixtures, not generated artifacts.

### Decision: bind and catalog mutations use runtime persistence order (R3)

**Decision:** Accepted user messages and attachment binds share the Session Runtime mailbox revision stream: stable command/entry identity, idempotent retries, all-or-nothing validation, and a race-safe pending-to-bound claim. Catalog rename/archive/unarchive/versioned-delete participate in the same revision ordering so a later snapshot cannot resurrect stale titles, archive flags, or deleted rows.

**Failure:** lost acknowledgement retries, duplicate command ids, failed commits, bind-versus-TTL, bind-versus-delete, concurrent quota reservation, and crash recovery must leave no duplicate entries and must not delete a just-bound blob.

### Decision: v1 terminal-end stays; durable delete is additive (R4)

**Decision:** `DELETE /api/v1/sessions/{id}` remains irreversible terminal-end. Existing Ended rows stay labeled Ended in the catalog; they are not archived, not reopenable, and not physically deleted by v1 DELETE. Read-only HTTP history (GET session / GET messages) remains available without attach. GET attachment list/metadata/content remains readable on that view; upload, stage, abort, and materialize stay rejected. Versioned durable deletion and runtime deactivation are additive routes. Archive preserves data and rejects activation/messages/uploads until unarchive. Deactivation is not archive and is not deletion. Migrations backfill workspace-ownership keyed by SessionId, preserve pins, revisions, and receipts, and keep Ended irreversible.

### Decision: finite silent initiative and at-cap deactivation (R5)

**Decision:** `MaxConsecutiveProactiveTurns` is read from the pinned Agent Definition (documented default only when omitted). Visible Speak increments the counter once; StaySilent consumes cooldown without increment; user activity resets to 0. Finite silent-evaluation/backoff and inactivity bounds stop unpaid and paid silent reasoning independently of the visible-speech cap. After a non-zero cap is reached, further Speak is denied and `RequestDeactivate` remains permitted. A zero-cap/passive role never Speaks; it does not auto-deactivate on the first silence, but silent-evaluation/inactivity bounds or explicit deactivate still apply. Environment events remain separately governable. No overlapping Speak. Stale timers/decisions/receipts are rejected. **Observed** in SessionRuntime + FakeTimeProvider tests and `POST /api/v2/sessions/{id}/deactivate`.

### Decision: A–H mandatory; Phase I conditional; concrete sandbox (R6)

**Decision:** Phases A–H are mandatory, including rename and archive/unarchive. Phase H is a concrete container sandbox behind the execution capability boundary (not unit fakes alone). **Observed:** `sandbox.run` via `DockerSandboxExecutor` (`busybox:1.36`, network none, read-only root, dropped capabilities, 64 MiB / 0.5 CPU / 32 PIDs / 8 s, session working-dir bind only, kill/reap, bounded output, `IArtifactStore` export). Broad `process`/`shell` remain disabled and are not granted by H. **Phase I observed not-applicable:** implemented G/H workflows do not require typed persisted WorkItems. In-flight tools are off-mailbox and epoch-guarded (`ToolWorkflowRuntimeTests.Deactivation_rejects_stale_workspace_write`); sandbox containers are killed and reaped; shipped Support/Compliance allowlists omit `sandbox.run`. Session-owned attachments, workspace files, and artifacts already survive deactivation through Phases D and F. **Future trigger:** implement WorkItems only if a later accepted Support, Compliance, or `sandbox.run` workflow must continue or resume after SessionRuntime deactivation without repeating the user turn. Until then do not leave untracked host processes as a substitute.

### Planned resource limits

Unless a later item records a tested change:

| Limit | Value |
| --- | --- |
| Attachments per message | 10 |
| Attachment size | 25 MiB each |
| Session attachment bytes | 250 MiB |
| Pending upload TTL | 1 hour |
| Decoded image pixels | 32 megapixels |
| Workspace writes | 250 MiB |
| Artifact size | 50 MiB each |
| Session artifact bytes | 250 MiB |
| Extraction output | 256 KiB |
| Parser timeout | 10 s |
| Parser memory | 256 MiB |
| Tool steps | 12 max |
| Per-tool timeout | 30 s |
| Overall tool deadline | 120 s |
| Tool output | 8 MiB |
| Sandbox memory | 64 MiB |
| Sandbox CPUs | 0.5 |
| Sandbox PIDs | 32 |
| Sandbox timeout | 8 s |
| Sandbox output | 64 KiB |

Owners: [Protocol](14-api-and-realtime-protocol.md) (capability, leases vs Attachment, additive routes); [Persistence](15-persistence-and-configuration.md) (revision/bind/cleanup); [Controller](05-interaction-controller.md) (initiative/receipts); [Implementation Plan](18-implementation-plan.md) (phase gates).

## What may still be measured

Provider selection within independently configured speech ports, VAD thresholds, frame size within the allowed range, TTS phrase segmentation and latency optimization are tuning variables. The default behavior and degraded paths are specified; measurement must not reopen project ownership, transport, storage or response identity decisions. No guaranteed provider-dependent SLA is implied.
