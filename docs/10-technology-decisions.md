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

MessagePack is case-sensitive; use explicit camelCase string keys and binary DTOs tested with the JavaScript client, as described in [Microsoft's SignalR MessagePack documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-10.0). Http resilience policies must explicitly account for unsafe methods; see [Microsoft HTTP resilience guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience). Project-specific timeout/retry choices are specified in [Backend Implementation](12-backend-implementation-spec.md), not implied library defaults.

## Decision: composed voice pipeline

**Decision:** The primary and default MVP voice architecture is STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS. It does not use speech-to-speech reasoning. Input capture remains active while output plays.

**Rationale:** Independent capabilities simplify testing, debugging and provider replacement; preserve Agent Core's control of interruption; support different models for different identities; permit deterministic synthetic providers and per-stage latency measurement; and support a future fully on-prem deployment.

**Trade-off:** Extra recognition, segmentation and synthesis stages can add latency compared with a native end-to-end speech model.

**Mitigation:** Stream STT and use partials when available, apply fast controller heuristics, stream model text into natural Speech Segments, stream TTS into AudioWorklet playback, optionally duck locally, and measure each stage separately.

**Future migration:** INativeRealtimeProvider may later offer an optional optimized path while preserving normalized events and response/session invariants. It is excluded from all MVP implementation milestones and must not complicate the composed path.

## Decision: OpenRouter first for hosted text reasoning

**Decision:** Prefer OpenRouter as the initial hosted text gateway, configured through OpenAICompatibleLanguageModel. It is a configuration choice, never an Agent Core dependency or reasoning interface. The gateway exposes an OpenAI-style endpoint at `https://openrouter.ai/api/v1/`; see the [OpenRouter quickstart](https://openrouter.ai/docs/quickstart). Implement and configure this adapter during the existing milestone plan (Milestone 3). `openrouter/free` (the [Free Models Router](https://openrouter.ai/docs/guides/routing/routers/free-router)) is allowed **only** for explicit opt-in **adapter smoke tests**. It selects free models at random, so it is not a Real/demo `DefaultModel`. The demo/Real profile requires an operator-selected **fixed** OpenRouter model ID (a configured placeholder, not a transient catalog pin in these docs). Exact smoke-routed quality and structured-output correctness are not milestone gates. Read `OPENROUTER_API_KEY` from environment or `dotnet user-secrets` only; do not commit secrets and do not specify a named secret drop-file. Default tests remain Synthetic/offline and must pass without this key. Real OpenRouter smoke tests are explicit opt-in and skip cleanly when the key is missing.

**Rationale:** A hosted gateway makes it practical to experiment with model families and compare conversation latency, quality and cost while retaining one adapter protocol. The free router unblocks live adapter smoke without treating catalog quality as an acceptance criterion. Identity consistency and latency are product qualities, so the demo path must not randomly switch models.

**Trade-off:** OpenRouter is hosted, not on-prem. Its model catalog, rate limits, parameters and streaming details vary; compatibility needs contract tests rather than assumptions. `openrouter/free` may route to different free models per request and is therefore smoke-only.

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

## Explicit non-goals

No native speech-to-speech/realtime model in MVP. No microservices, Kafka, RabbitMQ, Redis requirement, Kubernetes requirement, Orleans/Akka actor framework, MediatR merely for layer forwarding, generic workflow engine, multi-agent system, vector database, RAG platform, plugin marketplace, OAuth/login system for MVP, WebRTC in the first version, mobile app, native desktop app, elaborate avatar system, SSR, or Next.js. Ant Design v6 is the verified MVP generic UI system (see the decision above); do not add another component or CSS framework, Ant Design Pro/ProComponents/X, or a replacement custom design system. No autonomous tools/platform, distributed event bus or generic repository framework. No extra hosted mock or third-party test-only inference service. Reconsider only when actual requirements justify the cost.

## Post-MVP planned until verified

Historical MVP acceptance is unchanged. Phases A–H and the Phase I decision are **observed** in the decisions below (and in [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified)). The heading is retained as a stable fragment. Do not invent a second numbered specification series. Product behavior is described here and in docs/01–18; the original proposal pack is not required to understand the system.

### Decision: trusted-local owner capability (R1)

**Decision:** One reusable **trusted-local owner capability** authorizes session catalog, lifecycle, hub attach, upload, bind, artifact access, workspace execution-view, and content retrieval. `SessionId` identifies the resource; it is not a credential. This is not tenant isolation, OAuth, or public multi-user hosting. Historical MVP still excludes those.

**Contract:**

- Obtain: `POST /api/v1/local/owner-capability` succeeds only for a trusted-local caller. Native processes require loopback `RemoteIpAddress`. Compose publishes `127.0.0.1:host:container` while Kestrel listens inside the container; Docker NAT may present that traffic as this container's IPv4 default gateway, so Compose sets `Hosting:TrustPublishedPortGateway=true` to trust **that one hop**. Private ranges are not trusted. Response is an opaque token (not a SessionId) plus expiry metadata.
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
