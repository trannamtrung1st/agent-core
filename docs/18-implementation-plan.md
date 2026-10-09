# Implementation Plan

## Activation, AgentRun and background Sessions cutover

Accepted follow-on requirement reviewed against `bd44046896df6f3e0fc2e7d15d60dd479a5349cd` on 2026-10-08. This does not reopen historical freezes or start P10/P11. The approved target and migration policy are owned by [Architecture](03-system-architecture.md#accepted-activation-and-agentrun-cutover) and [Technology Decisions](10-technology-decisions.md#decision-unified-activation-agentrun-and-background-sessions).

Execute in order: A domain/contract proof; B atomic input, Session, Activation, run and occurrence receipt persistence; C unified live/detached execution; D background creation and Automation intake; E trusted conditional report-back; F same-Session Continue in chat, APIs and UI; G old-engine deletion, bounded Impeccable, canonical documentation and exact-SHA closure.

Production conversation and detached execution now use the registered SessionRuntime/SessionHost AgentRun path. Durable accepted-input intent is repaired after restart; occurrence intake creates real background Sessions; the shared tool loop persists checkpoints and fences dispatch, and background.start returns a committed independent child. Atomic background outcomes and same-Session user continuation are exercised. Report-back, Session-first APIs/UI and approved legacy/schema retirement are implemented. Closed/frozen on final behavior `8cec78c5d47a43e0236a5c38f2e312f4e36ce283` (2026-10-08), with all five required hosted Synthetic/Compose jobs [green](https://github.com/trannamtrung1st/agent-core/actions/runs/37756244306). The [verification report](reports/activation-agent-run-background-sessions-verification.md) records local/runtime evidence and prior failed gates. P10/P11 remain unopened.

Acceptance requires all AC01–AC20 and J1–J12 in the approved proposal: batched live admission; same-run retry; one child per immediate receipt/occurrence; one Session writer; safe approval, cancellation and effect recovery; frozen execution configuration with current authorization; quiet outcomes; non-recursive conditional report-back; same-Session continuation/foreground races; owner-scoped pagination/control; no mentions; and zero old production execution surface. Run focused/full backend, order-event plugin, frontend tests/build, Synthetic browser, SQLite/Compose restart and existing voice/initiative/browser/Automation/Skill/workspace/artifact regressions. Complete the bounded 1440/768/390 Impeccable pass and synchronize canonical docs.

The final behavior commit must have all required hosted Synthetic/Compose jobs green on its exact SHA before closure. The baseline's hosted result does not verify this migration. [Verification](reports/activation-agent-run-background-sessions-verification.md) records phase/AC evidence, failed gates and final closure.

## Native Playwright wrapper full cutover

User-authorized on `develop/branch-1` at baseline `75bf40aaaaba5a256d528d9e3feeb25c7a967b0d`. The implementation replaces indexed Browser v2 with one typed neutral port, direct native Locator discovery, native subtree observations, bounded opaque refs and unchanged owner/security/effect boundaries. General Assistant v18 and Secretary v6 publish the new guidance. P10/P11 remain unopened. Historical Browser v2 acceptance and freezes remain in their reports; they do not verify this cutover.

**Closed/frozen on verified behavior `345e8f7a1b31a336964e78b00be7fce8d88f9e4b`**, 2026-10-09. All five [exact-SHA hosted Synthetic jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37876708714) passed: backend 2,833 tests (17 opt-in skips), frontend 764/build, core Playwright 126/126, acceptance 16 and Compose volume survival. The explicitly authorized configured DeepSeek generic SPA journey passed with actual final DOM assertions against unchanged native behavior. The failed `ab118790` core run and the Admin evaluation-read publication race are retained in [lifecycle verification](reports/admin-evaluation-lifecycle-verification.md); obsolete reads now cancel before draft consumption without weakening API 404 or publication gates. [The verification ledger](reports/native-playwright-wrapper-verification.md) records closure, prior runs, exclusions and optional follow-ups. No owner data was reset. PR #4's conflicts with main remain separate integration work; P10/P11 remain unopened.

The user-authorized bounded discovery-recovery follow-up addresses malformed-query diagnostics and loss of discovery evidence during model-history compaction. It uses the existing native port and security boundaries; [verification](reports/browser-discovery-recovery-verification.md) records current checks and remaining gates. The original cutover freeze stays on `345e8f7a`; this does not launch another migration or P10/P11.

## Historical Browser v2 completeness and polish

Authorized as a targeted audit of the implemented Browser v2 on 2026-10-08. The [72-operation matrix and verification ledger](reports/browser-v2-completeness-and-polish.md) compare current official MCP and native operations against real equivalents. Scope is safe inspection, structured host environment defaults, origin-approved geolocation, media correctness and practical operation gaps, preserving existing port, owner, credential and durable-execution boundaries. No migration rerun or P10/P11 work. Acceptance requires real native environment/SPA/security behavior, affected Admin MCP interaction and responsive checks, canonical docs and green required hosted Synthetic gates. Runtime context replacement and advanced protected-output/code capabilities remain deliberate exclusions or deferrals, with no new default privilege grants. Targeted key-free acceptance passed on behavior SHA `c5bc6143322559e818e290bd87934c2391bd32a4`: seven native environment/security/input cases, affected Admin MCP interaction, full local regressions and all five [hosted Synthetic jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37729115112). The ledger owns exact counts and deliberate exclusions; the later real-model Journey L result is recorded in the full-cutover ledger.

## Bounded capability authorization and projection enhancement

This user-authorized follow-on composes with the completed Agent Workspace refinement. It does not reopen frozen milestones or start P10/P11. Its gates are: exact Selected/All authorization and removed count ceiling; separate always/context/Skill/loaded projection; fenced deterministic discovery with same-execution recovery; Admin catalog/access/projection authoring; safe projection telemetry; canonical docs and broad key-free regressions. The existing registry, Skill pipeline, Session mailbox and durable work checkpoints remain the implementation seams. Acceptance requires observed runtime and Admin journeys, not schema compilation alone. See the [capability enhancement verification report](reports/capability-authorization-projection-verification.md) for current gate evidence.


## Post-filesystem managed workspace refinement

The workspace refinement is superseded by the authorized full migration: one real Agent Instance owner per Session, durable `/home`, temporary `/working`, transient mailbox cwd and canonical copy/move contracts. Runtime built-ins include deliberately retained published versions for pinned owners; unsupported retired files remain in Git history. General Assistant v20 combines capability-aware native Browser projection with explicitly authorized `background.start`. Existing phase freezes remain historical; P10/P11 remain unopened.

Full migration acceptance requires mandatory ownership and empty Chat guidance; exact file/tree cross-root copy, the four-file c#→csharp rename, CAS and lifecycle conflicts, Artifact delivery, Session deletion and owner deletion recovery; legacy-data rejection; full backend/frontend tests/build, Synthetic Playwright, Compose recreation, synchronized normative docs and hosted CI. See the migration verification report for the current measured gate status. Cwd persistence, cross-store batches and home sandbox mounts remain deferred.


This is the ordered implementation handoff. Each milestone must satisfy its acceptance criteria before dependent work begins. Scope remains conversational presence, not a general autonomous-agent platform. [Roadmap](08-development-roadmap.md) is the short index; this document owns the detailed gates. Observability hooks, cancellation and tests begin with the first slice, although full instrumentation/tuning is Milestone 12.

The verified MVP generic UI is Ant Design v6 ([Technology Decisions](10-technology-decisions.md#decision-ant-design-v6-as-mvp-generic-ui-system)). That presentation change is not a new numbered milestone and does not reopen historical Milestone 1–12 status. Evidence: [antd-migration-handoff](reports/antd-migration-handoff.md).

## Milestone 0 — Documentation/contracts finalized

- **Goal:** Make implementation decisions explicit and coherent.
- **Scope:** README and docs 01–18; ports, state machine, transport/audio contracts, examples, persistence and deployment direction.
- **Required interfaces:** Review ILanguageModel, speech ports, IMemoryStore, IAgentDefinitionStore, IInterruptionClassifier, IAgentBrain, IEnvironmentEventIngress, IIdGenerator, ISessionOutput and ISessionAudioOutput for the composed pipeline.
- **Tests:** Markdown links/fences, JSON example parsing, cross-document naming and invariants audit (including canonical wire names `agent.response.started|interrupted|completed`, `agent.text.delta|completed`, `playback.stop|gain`); no application tests exist yet.
- **Acceptance criteria:** An implementation agent can determine project references, state ownership, event order, cancellation, audio format, reconnect, mode transitions and milestone sequence without choosing a new architecture. Changes contain Markdown and small repository agent/tooling configuration only.
- **Status:** Complete. Further architecture polishing is out of scope unless a later milestone discovers a contradiction. Implementation starts at Milestone 1 in a separate task.
- **Explicit non-goals:** No solution/project/application source, package manifests, migrations, Dockerfile, or GitHub Actions workflow files.

## Milestone 1 — Solution skeleton and synthetic text vertical slice

- **Goal:** Prove the boundaries with one offline text exchange using native .NET + Vite; Docker is not required.
- **Scope:** Create .NET project layout/tests and Vite strict TypeScript skeleton; DI, health, minimal lifecycle services, in-memory state, synthetic model and a test-host driver. Use a console/test harness invoking application services for streamed text until SignalR Milestone 5; do not add a temporary public text HTTP endpoint.
- **Required interfaces:** ILanguageModel, IIdGenerator, TimeProvider, ISessionOutput, initial IMemoryStore.
- **Tests:** Build references, xUnit stream ordering and cancellation, WebApplicationFactory /health and session creation, frontend build.
- **Acceptance criteria:** Synthetic boots without keys/network; an application integration test submits text and receives ordered normalized deltas and terminal output. Domain references no web/provider/persistence framework.
- **Explicit non-goals:** Real providers, voice, durable storage, complete browser realtime connection, Docker/Compose setup, MediatR or extra platform infrastructure.
- **Verification:** Local `dotnet build` / `dotnet test` and, once `web/` exists, frontend install/build/unit tests. Default verification never requires provider keys, internet inference, microphone, speaker or GPU.
- **Status:** Complete. Synthetic text runs through `SessionRuntime` + `ScriptedLanguageModel` (application tests, not a public chat HTTP endpoint). `/health` and session lifecycle HTTP APIs are covered by WebApplicationFactory. Commands: `dotnet test`; `cd web && pnpm install --frozen-lockfile && pnpm run test --run && pnpm run build`. See [Milestone 1 verification](reports/m01-verification.md).

## Milestone 2 — Agent Definition and Agent Runtime

- **Goal:** Run two reusable identities through the same runtime.
- **Scope:** JSON validation/loading, pinned versions, PromptContextBuilder, basic Agent Runtime/history/summary, IAgentBrain Speak/StaySilent, per-session mailbox ownership and supervised tasks.
- **Required interfaces:** IAgentDefinitionStore, IAgentBrain, ILanguageModel, IMemoryStore; definition/context records from docs 04/12/15.
- **Tests:** Complete example definitions deserialize/validate; missing aliases/version failures; exact prompt sections; current turn once; independent simultaneous sessions; deterministic snapshot revisions.
- **Acceptance criteria:** Examiner/support differ by definition only; no vendor DTO crosses Application; mutable state has a single owner and synthetic scripts work with both.
- **Explicit non-goals:** Agent editor, YAML, tools, native realtime implementation or model-assisted summarization.
- **Status:** Complete. Definitions load from `agents/examiner.json` and `agents/customer-support.json` (M2 gate). Shipped fixtures also include `agents/compliance.json` from post-MVP role environments and `agents/general-assistant.json` for open-ended harness checks. Speech/display remain runtime response capabilities, not definition instructions. `PromptContextBuilder` + `DefaultAgentBrain` drive the same `SessionRuntime` mailbox for both identities. See [Milestone 2 verification](reports/m02-verification.md).

## Milestone 3 — OpenAI-compatible streaming LLM and OpenRouter configuration

- **Goal:** Replace synthetic text capability with a real configurable HTTP adapter.
- **Scope:** OpenAICompatibleLanguageModel configured for OpenRouter hosted text. Opt-in adapter smoke may use `openrouter/free`; Real/demo configuration uses a **fixed** operator-selected model ID. Configurable model aliases per identity, application/harness streaming text (browser integration follows Milestone 5), IHttpClientFactory, private SSE parser, normalized failures, headers/model configuration, explicit timeouts, circuit breaker and no blind POST retries. Read `OPENROUTER_API_KEY` from environment or `dotnet user-secrets` only.
- **Required interfaces:** ILanguageModel, ModelRequest/ModelGenerationEvent, ModelCapabilities, ProviderFailure.
- **Tests:** Offline HTTP/SSE contract suite including arbitrary chunk boundaries, UTF-8, missing finish, cancellation, 429 and midstream failure. Default suite must pass without `OPENROUTER_API_KEY`. Explicit opt-in bounded OpenRouter smoke using `openrouter/free`; skip cleanly when the key is missing or opt-in is unset. Do not assert routed-model quality or structured-output correctness. Do not use `openrouter/free` as the demo DefaultModel.
- **Acceptance criteria:** Same runtime runs ScriptedLanguageModel or OpenAICompatibleLanguageModel via alias configuration; OpenRouter-specific IDs/options stay in Infrastructure and a local endpoint fixture requires no runtime changes; observed partial text is never replayed after a failure; credentials remain backend-only. Default tests never call OpenRouter or spend credits.
- **Explicit non-goals:** Vendor SDK coupling, tool calling, structured-output platform, speech implied by text compatibility.
- **Status:** Complete. `OpenAICompatibleLanguageModel` streams via IHttpClientFactory and a private SSE parser. Synthetic DI stays on `ScriptedLanguageModel` and rejects outbound HTTP. Opt-in smoke is `[LiveProviderFact]` (`AGENTCORE_LIVE_PROVIDER_TESTS=1` plus `OPENROUTER_API_KEY`) using `openrouter/free`; committed Real/demo `DefaultModel` remains operator-fixed. See [Milestone 3 verification](reports/m03-verification.md).

## Milestone 4 — Interaction Controller with synthetic speech/events

- **Goal:** Establish deterministic turn-taking before live media.
- **Scope:** Orthogonal state machine, bounded candidate/utterance logic, fake speech/environment inputs, heuristics, TimeProvider timers, response cancellation and supersession guards.
- **Required interfaces:** IInterruptionClassifier, IAgentBrain, recognition event records, IIdGenerator, TimeProvider.
- **Tests:** “mhm” Continue, “wait” Interrupt, noise Ignore, final-before-ended, stale classifier and late model results, RequestInterruptionClassification vs RequestAgentDecision separation, capability fallback, invalid timer generation.
- **Acceptance criteria:** Controlled tests reproduce R1→superseded→R2 and discard every stale R1 output; controller keeps processing while model is gated.
- **Explicit non-goals:** Real microphone, production semantic perfection, per-frame LLM classification, dedicated OS threads or actor framework.
- **Status:** Complete. Orthogonal controller fields, heuristic barge-in, FakeInterruptionClassifier, and mailbox-isolated brain/classifier workers cover mhm/wait/noise, final-before-ended, stale classifier, late R1 after R2, capability fallback, and invalid timer generation. See [Milestone 4 verification](reports/m04-verification.md).

## Milestone 5 — SignalR protocol and browser connection

- **Goal:** Deliver the synthetic text path through the real browser transport.
- **Scope:** /hubs/session, MessagePack camelCase DTOs, session attach/lease, one-session text/voice mode, frontend Zustand reducers, text UI, control sequencing, safe errors, binary method validation. Implement in-memory reconnect snapshot semantics now; durable resume comes in Milestone 11. Completing and verifying this synthetic/offline text-conversation path is prioritized over live OpenAI STT/TTS. Immediately after the synthetic browser text path works, add a **minimal** Synthetic production-like container and Compose file: one Agent Core app, built React SPA, in-memory or SQLite, no provider keys. Native `dotnet run` + Vite remains the fast loop.
- **Required interfaces:** ISessionOutput, concrete SessionManager entry points, Contracts command/event/audio DTOs.
- **Tests:** Kestrel + JavaScript MessagePack fixtures, protocol mismatch (including rejected client correlationId/causationId ownership), duplicates/gaps, second-connection rejection, browser synthetic text Playwright including pending voice start (Starting voice… / Cancel, no PCM until Mode=voice), pendingMode cleared on disconnect, connection-loss cleanup, SessionCapacityExceeded on attach, and a key-free Compose smoke (`docker compose up` + /health).
- **Acceptance criteria:** Text UI works offline end-to-end, late R1 deltas are rejected on both sides, hub has no runtime logic and browser cannot obtain provider secrets. `docker compose up` with Synthetic is a supported optional integration path.
- **Explicit non-goals:** WebRTC, authentication system, unbounded event replay, audio DSP, Kubernetes, Redis, brokers, reverse proxies, separate frontend/backend production containers.
- **CI:** `.github/workflows/synthetic.yml` runs key-free `dotnet test`, web unit tests/build, and synthetic Playwright.
- **Status:** Complete. Browser SignalR/MessagePack text path, leases, pending-voice timeout/disconnect, Kestrel JavaScript fixtures, Playwright text E2E, key-free Synthetic Compose, and GitHub Actions are implemented. See [Milestone 5 verification](reports/m05-verification.md).

## Milestone 6 — Microphone, AudioWorklet and STT

- **Goal:** Convert continuously streamed microphone input into speech activity and normalized partial/final evidence.
- **Scope:** getUserMedia, worklet resampling/PCM16 encoding, VAD boundaries, bounded separate audio ingress, synthetic STT selected through configuration/DI, partial/final transcript flow, capability discovery and batch-only degraded fallback. The OpenAI recognizer **boundary** was implemented in this milestone; later P1 left Recognition Adapter=`OpenAI` unselectable (`DeferredSession`). Do not block this milestone on `OPENAI_API_KEY`.
- **Required interfaces:** ISpeechRecognizer, ISpeechRecognitionSession, RecognitionCapabilities, AudioFrame; protocol InputAudioDto.
- **Tests:** Sample-rate conversion/sample offsets, byte ordering, overflow/discontinuity, boundary ordering, duplicate finals, batch capability degradation, permission/device errors, VAD activityScore hysteresis, VoiceUnavailable, voice preflight (PCM gated on applied Mode). Automated tests use Synthetic STT. Infrastructure contract tests for OpenAiSpeechRecognizer session payloads: omit optional vendor fields such as transcription `delay` by default; include them only on an explicit supported-API fixture. Those payload tests do not make Recognition Adapter=`OpenAI` selectable. Real OpenAI STT integration tests are explicit opt-in and skip when `OPENAI_API_KEY` is missing; they may be deferred without failing default build/test.
- **Acceptance criteria:** Voice input produces one final user turn per utterance; raw audio never enters the normal mailbox/event log or persistence. Synthetic mode uses script-driven recognition with no keys. Missing OpenAI credentials does not fail the default suite.
- **Explicit non-goals:** Real TTS output, assuming text-provider speech compatibility, perfect VAD/echo removal.
- **Status:** Complete as a historical MVP gate. Synthetic STT, bounded audio ingress, batch degraded adapter, AudioWorklet preflight/PCM gating, and fake-device Playwright coverage are implemented. `OpenAiSpeechRecognizer` session-payload fixtures exist, but the live realtime session is a no-op `DeferredSession` and `SpeechFactory` does not select Recognition Adapter=`OpenAI`. Selectable hosted STT after P1 is `OpenAICompatibleBatch`. Missing `OPENAI_API_KEY` does not fail the default suite. See [Milestone 6 verification](reports/m06-verification.md) and [P1 handoff](reports/p1-replaceable-speech-handoff.md).

## Milestone 7 — TTS streaming and playback

- **Goal:** Turn streamed model text into natural Speech Segments and start TTS playback before the full answer exists.
- **Scope:** ResponseTextAccumulator/SpeechSegmenter, synthetic and initial OpenAiSpeechSynthesizer selected through configuration/DI, canonical conversion, output queue/worklet, timing marks, playback acknowledgements, full-duplex capture. Implement the OpenAI synthesizer boundary as planned; do not block this milestone on `OPENAI_API_KEY`.
- **Required interfaces:** ISpeechSynthesizer, SpeechRequest/SpeechSynthesisEvent, OutputAudioDto and playback controls.
- **Tests:** Segment/sample continuity, empty final marker, underrun/overflow, cancellation, browser worklet acknowledgement, microphone remains active during output. Automated tests use Synthetic TTS. Real OpenAI TTS integration tests and manual voice verification are explicit opt-in and may wait for `OPENAI_API_KEY`; skip cleanly when it is missing.
- **Acceptance criteria:** Playback starts before full response completion when capabilities permit; sequence/duration counters are correct; no HTML audio element handles streamed PCM; response completion waits for playback. Default tests never require OpenAI credentials.
- **Explicit non-goals:** Perfect prosody/phoneme sync, native realtime providers, codecs in Agent Runtime.
- **Status:** Complete. ResponseTextAccumulator/SpeechSegmenter, SyntheticSpeechSynthesizer, OpenAiSpeechSynthesizer HTTP PCM boundary tests, output worklet playback acknowledgements, and capture-during-output coverage are implemented. Default suites do not require `OPENAI_API_KEY`. See [Milestone 7 verification](reports/m07-verification.md).

## Milestone 8 — Full-duplex voice and continuous listening

- **Goal:** Keep the composed input and output paths active concurrently during a natural call.
- **Scope:** Integrate microphone/streaming STT with model/segment/TTS playback, independent lifetimes, mute/unmute, audio queue limits and simultaneous state projections.
- **Required interfaces:** ISpeechRecognitionSession, ILanguageModel, ISpeechSynthesizer, ISessionAudioOutput and Playback Progress controls.
- **Tests:** PCM ingress and Partial/Final Transcripts continue while R1 audio plays; slow TTS does not block STT; mute affects input only; network loss stops both paths safely; synthetic simulation needs no hardware.
- **Acceptance criteria:** No automatic microphone stop during agent speech, no record-submit-playback cycle, no multimodal reasoning requirement. Existing response identity guards remain active; the next milestone validates full semantic interruption and Spoken Until.
- **Explicit non-goals:** Speech-to-speech models, half-duplex fallback UX, perfect echo suppression or provider-specific reasoning logic.
- **CI:** After synthetic voice exists, add synthetic Playwright voice scenarios to core CI (still no keys, mic, speaker or GPU).
- **Status:** Complete. Independent input/output lifetimes, mute/unmute, detach cancellation, and synthetic Playwright duplex/mute/disconnect coverage are implemented. Core CI remains key-free with Chromium fake media. See [Milestone 8 verification](reports/m08-verification.md).

## Milestone 9 — Semantic barge-in, ducking, supersession and spoken-until

- **Goal:** Make interruption reliable across provider, network and playback races.
- **Scope:** Local duck/restore, server interruption lifecycle, response tombstones, worklet flush, cancellation of LLM/TTS while STT continues, conservative heard context and history.
- **Required interfaces:** IInterruptionClassifier, model/speech cancellation contracts, playback.stopped/progress, context builder and memory snapshot fields.
- **Tests:** Interrupt at every pipeline boundary; intentionally non-cooperative late R1 text/audio/completion after R2; timing-mark path; no-timing-mark path that credits zero text from a partially played segment even when the first half of audio duration is less than half the text; late playback events ignored after supersession; semantic, speechAndFinal and speechActivity fallback policies.
- **Acceptance criteria:** “Wait” stops and replaces response; short backchannels usually continue in partial-capable mode; no stale R1 output plays after local supersession; next context excludes unplayed tail.
- **Explicit non-goals:** Perfect intent recognition, exact phoneme synchronization, muting microphone during output.
- **Status:** Complete. Playback.stop precedes interrupted; spoken-until credits timing marks or fully played segments only; late R1 playback is ignored; local duck/restore and worklet flush gate R2 audio. See [Milestone 9 verification](reports/m09-verification.md).

## Milestone 10 — Proactive interaction

- **Goal:** Offer useful initiative during active sessions with restraint.
- **Scope:** Idle/environment/unfinished triggers, policy usefulness rules, expiry/dedupe/cooldown, StaySilent, stale decision rechecks.
- **Required interfaces:** IAgentBrain, AgentTrigger, TimeProvider, IEnvironmentEventIngress, synthetic environment driver.
- **Tests:** Idle trigger, StaySilent cooldown, one hint per silence period, input invalidates pending initiative, environment event during output queued/expired, no initiative while detached.
- **Acceptance criteria:** Examiner may offer help after silence; support can mention a simulated update; timers do not automatically speak or create repeated nudges.
- **Explicit non-goals:** Push/SMS/email, background mobile services, arbitrary external-event ingestion or workflows.
- **Status:** Complete. Idle/environment/unfinished initiative uses `IAgentBrain` with StaySilent cooldown, one hint per silence period, queue/expiry/dedupe, and no speak while detached. In-process `IEnvironmentEventIngress` only. See [Milestone 10 verification](reports/m10-verification.md).

## Milestone 11 — SQLite persistence and reconnect/resume

- **Goal:** Restore conversation continuity after disconnect/restart.
- **Scope:** EF Core mappings/migrations, transactional revision writes, profile/summary/history, checkpointing, recovery of unfinished entries, runtime eviction/reconstruction and terminal end behavior.
- **Required interfaces:** IMemoryStore, SessionSnapshot, UserProfile, definition store and session lifecycle services.
- **Tests:** SQLite/in-memory contract parity, transaction rollback/conflict, reopen after simulated crash, old-entry heard offset refresh, streaming checkpoint does not rewrite unchanged completed rows, PendingMode=Voice cleared on pause/crash recovery, deduped uncertain text retry, clean end and failed end save.
- **Acceptance criteria:** A restarted process resumes a paused session with pinned identity/history and conservative heard offsets; no PCM/provider stream replay; known ended session cannot attach; WAL/backup behavior documented.
- **Explicit non-goals:** Vector memory, Redis, generic repository framework, PostgreSQL deployment or multi-user login.
- **Status:** Complete. EF Core 10/SQLite and InMemoryMemoryStore share revision/idempotency/conflict rules; crash recovery pauses attached sessions, interrupts streaming rows, and clears PendingMode. See [Milestone 11 verification](reports/m11-verification.md).

## Milestone 12 — Per-stage latency measurement, provider tuning and demo hardening

- **Goal:** Demonstrate presence with measured behavior and a simple deployable app.
- **Scope:** OpenTelemetry traces/metrics, structured safe logs, developer timeline, separate STT/controller/LLM/segmentation/TTS/transport/playback measurements, hosted/hybrid/on-prem configuration checks, same-origin static build, **container hardening** of the Compose environment introduced after Milestone 5 (SQLite volume/restart tests, Real-provider configuration, hybrid/local inference topology, backup/shutdown/operations polish), accessibility and connection/device errors.
- **Required interfaces:** Existing ports remain stable; ActivitySource/Meter, ILogger, TimeProvider and safe error mapping.
- **Tests:** Full offline backend/frontend/Playwright gates, redaction checks, bounded queue stress, 20-turn measured demo, optional manual real microphone/headset/speaker pass when `OPENAI_API_KEY` is available, restore/restart test, Compose SQLite volume survival across container recreation. Default gates remain key-free. Core GitHub Actions must still never require OpenAI/OpenRouter keys, internet inference, microphone, speaker or GPU. Real-provider smoke remains opt-in.
- **Acceptance criteria:** Both identities demonstrate text, full-duplex voice, meaningful interruption, restrained initiative and reconnect. Latency targets report actual measurements, not promised SLAs. Synthetic demo runs without secrets; production-like build is one application container plus persistent SQLite volume; docker compose up supports reproducible integration/demo. Native dotnet + Vite development remains usable without Docker. Missing hosted keys do not fail the default verification set.
- **Status:** Complete. Measurement/demo and Compose SQLite volume/restart plus the MVP handoff report are recorded. Headset/speaker pass stays optional and key-gated. See [Milestone 12 demo verification](reports/m12-demo-verification.md) and [MVP handoff](reports/m12-mvp-handoff.md).
- **Explicit non-goals:** Kubernetes, horizontal scaling, extra application services, generic autonomous-agent functionality, elaborate avatars or analytics dashboards.

## Post-MVP phases (planned until verified)

Milestones 0–12 above remain the historical MVP record and stay **Complete**. Phases A–H are mandatory follow-on gates and are **observed** in the table; Phase I is recorded not-applicable with a future trigger. The section heading is retained for stable fragment links. Apply acceptance only to the phase that supplies the capability. Workspace **ownership** is Phase A; physical provisioning is Phase F. Phase C may prove artifact-reference rendering with fixtures; complete generated-artifact workflow is F/G. Concrete numeric quotas live in [Technology Decisions](10-technology-decisions.md#planned-resource-limits).

| Phase | Production behavior | Evidence |
| --- | --- | --- |
| A — Sessions | Multi-chat catalog, picker, pinned AgentId+AgentVersion, deterministic titles, rename, archive/unarchive, versioned durable delete, new epoch on reopen, trusted-local capability, workspace-ownership record | **Observed:** list order/pagination; Ended rows stay terminal and open read-only HTTP history without attach; v1 DELETE unchanged; capability fail-closed; migrations backfill ownership |
| B — Attachments | HTTP streamed pending→bound attachments; immutable originals outside SQLite; authorized content. OCR/Office still out of scope. | **Observed:** store/upload/bind/cleanup, composer picker, processors/vision mapping |
| C — Rich responses | reply.text, optional reply.speech, Markdown, attachment/artifact refs, independent receipts | **Observed:** conservative heard; no unseen tails on reconnect; fixture artifact refs; sanitized Markdown/reference UI |
| D — Initiative | Repeated StaySilent/Speak/RequestDeactivate; definition-owned cap; finite silent bounds; deactivation ≠ archive/delete | **Observed:** FakeTimeProvider 1st/2nd/3rd Speak; at-cap and zero-cap RequestDeactivate; HTTP deactivate idempotent |
| E — Role environments | Versioned Support and Compliance plus preserved examiner; allowlists; initiative pins | **Observed:** distinct MaxConsecutiveProactiveTurns 1/2/0 (support `maxPerSilencePeriod=2`); pin stable across file version bump; knowledge citation retrieve; process/shell denied |
| F — Workspace | Lazy provision; /agent and /attachments read-only; /workspace 250 MiB; artifacts distinct | **Observed:** isolation/traversal/symlink/other-session/secret denial; lazy empty workspace; template without corpus copy; archive/reopen/deactivate preserve files; durable delete cleans up; 250 MiB concurrent workspace writes; artifact store 50 MiB each / 250 MiB per session; explicit materialize with hash/provenance |
| G — Bounded work | Typed tools, Support/Compliance workflows, step/time/output caps | **Observed:** OpenAI-compatible tool_calls mapping; Scripted Support/Compliance multi-step replies with Markdown and artifact refs; 24 / 30 s / 180 s / 8 MiB caps; host-path and Session-mutation denial; late tool results rejected; uploads never execute |
| H — Sandbox | Concrete container behind the execution boundary | **Observed:** Docker `sandbox.run` isolation/resource/cleanup/export tests on `busybox:1.36`; process/shell still denied; Support/Compliance allowlists unchanged |
| I — WorkItems | Conditional | **Observed not-applicable:** Support/Compliance tool steps and `sandbox.run` complete on the live Session Runtime (or are rejected after deactivation). Durable attachments/workspace/artifacts already survive via D/F. **Future trigger:** a later accepted Support, Compliance, or `sandbox.run` workflow that must continue or resume after `RequestDeactivate` (Paused + new epoch) without repeating the user turn. |

Rename and archive/unarchive are in scope for A (R6). Do not treat a sandbox interface-only as H. Integrated Support/Compliance durable multi-chat (attachments, bounded work, artifacts, rich blocks, deactivate/reopen, idempotent cleanup) is observed in `SupportComplianceWorkflowTests` plus catalog API dual-create. Requirement matrix, migrations, operating notes, and permitted omissions: [post-MVP handoff](reports/post-mvp-handoff.md). Original proposal files: [proposal retirement](reports/proposal-retirement.md).

## P0 — Conversation lifecycle (observed)

Historical Milestones 0–12 and post-MVP A–H stay as recorded above. P0 is a follow-on conversation-lifecycle gate, not a replacement architecture.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P0-A | Dynamic `nextWaitMs` clamp, null fallback, deactivate ignores wait | Application InitiativePlan/Initiative tests; meter `initiative.next_wait_ms` tags `source|clamp|mode` only |
| P0-B | Pause ≠ end; canonical pause reasons; explicit Resume; transport resume for disconnected/recovered | SessionPauseSemantics, catalog API, ChatApp pause/ended UI |
| P0-C | `user.text` queue/interrupt; trailing durable suffix; CancelResponse; persist-before-ACK; pending user over initiative | Domain TrailingUserSuffix; UserTextQueueTests; Kestrel JS interrupt/queue/cancel-active; SQLite Recover_keeps_trailing |
| P0-D | First-party client pending-send queue while live (idle Send immediate); Steer=`interrupt`; Stop targets rendered `responseId` | `realtime.queue.test.ts`, Composer/ChatApp; Playwright queue/Steer/Stop |
| P0-E | ResponseEnvelope DisplayText/blocks; SpeechText not visible; live-only thinking | Conversation/activityState tests; markdown reload Playwright |
| P0-F | Full section-17 cases and minimum commands, including named `dotnet test AgentCore.sln` | [P0 agent-lifecycle handoff](reports/p0-agent-lifecycle-handoff.md) |
| P0 UI baseline closure | After structured-error UI, rich envelope regressions, and one bounded Impeccable harden pass, the same key-free Synthetic Domain/Infrastructure/Application/API/web/Playwright gate is green on that checkout. Distinct from P0-A–F lifecycle slices. P1/hosted/Browser speech is not claimed by this row. | TODO.md P0 checkboxes; this row. Local evidence: `local/tdp-workspace/evidence/p0-p1-replaceable-speech/` (gitignored). |
| P0 stabilization closeout (this freeze) | Exact live `synthetic.yml` plus Compose SQLite volume survival; real Chrome 153 `general-assistant` Voice checklist; preferred-name seed `friend` removed at `EnsureLocalProfileAsync` / trusted prompt preferences. Browser voice and current conversation-lifecycle behavior are frozen. Follow-on P1 history/lifecycle/speech implementation in this run is observed and P1-frozen (Voice checklist re-run on HEAD `df0a12cecb5b60a12499488eed9c101cb01b45b2`; unedited `fr-FR` Browser STT/TTS smoke `p1-final-fr-smoke-r2` on HEAD `5764010d989da965b252f5389e07669594c8bc29`). | TODO.md P0 checkboxes; Domain/Application preferred-name regressions; run evidence `local/tdp-workspace/evidence/p0-p1-history-lifecycle-speech/run-20260918T180704-c332dd/` (gitignored). P0 freeze parent HEAD `215de23ecefe65560b0b7a666783122d6c67408c`. |

## P1 — Replaceable speech (observed)

P0 stays closed only because its gates passed. This table records observed P1 behavior, not intended realtime STT.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| Independent STT/TTS | Nested `Providers.Speech.Recognition` / `Synthesis`; `SpeechFactory` `EffectiveSpeechPlan`; no silent Synthetic fallback | Infrastructure SpeechFactory tests; API SpeechConfigurationHostTests |
| Transports vs providers | `serverAudio` / `clientTranscript` / `clientSpeech`; Browser is not a backend port | docs 03/04/10; SessionRuntime has no vendor adapter names |
| `voiceAvailable` | Voice.Enabled AND structurally resolvable STT AND TTS | Catalog + `session.ready`; missing hosted key → false |
| Client transcript | Additive `client.speech.evidence`; one durable final; no PCM STT | ClientTranscriptAdmissionTests; MessagePack Kestrel fixtures |
| Client speech | Shared SpeechSegmenter → `speech.output.segment`; playback ACK gates completion; Stop leaves unaccepted first-party drafts local, while already server-accepted queued turns retain durable ownership and dispatch in order | ClientSpeechSegmentRuntimeTests; ClientSpeechMessagePackTests |
| Fake Browser CI | Injected recognizer/synthesizer; no live Web Speech | Vitest speech/*; Playwright `browser-stt` + `browser-browser` |
| Native Web Speech contracts | Interim/final mapping, pending speechend closure, capped idle native restart, serialized Browser TTS | Vitest `browserSpeechRecognizer` / `BrowserSpeechSynthesizer`; Playwright Voice-first Browser/Browser |
| Hosted TTS | `OpenAiSpeechSynthesizer` when Adapter=OpenAI and key present | SpeechFactory; live HTTP skipped unless `AGENTCORE_LIVE_OPENAI_TTS=1` |
| Hosted STT | Selectable `OpenAICompatibleBatch` only; Adapter=OpenAI recognition unselectable | SpeechFactory; live HTTP skipped unless opt-in |
| Mixed plans | Synthetic/Synthetic, Browser/Browser, Browser/OpenAI, batch/Browser, batch/OpenAI | Host tests with dummy keys, zero outbound HTTP |
| Observability | `speech.partial.count`, final/segment/playback latencies, `speech.playback.complete`, `speech.cancel.reason`, `speech.error.code`; `LogConversationContent=false` | SpeechTelemetrySurfaceTests; docs 17 |
| Final key-free gate | Domain 22, Infrastructure 101+9 skip, Application 291, API 105, Vitest 242, Playwright 25 | [P1 handoff](reports/p1-replaceable-speech-handoff.md); run evidence gitignored |
| HOSTED-04 | Opt-in non-Synthetic real voice smoke | **Unverified** on this checkout (`AGENTCORE_LIVE_*=0`) |

## Follow-on P1 history, lifecycle and multilingual speech

This table does not reopen historical Milestones 0–12, post-MVP A–H, P0 conversation-lifecycle, or P1 replaceable-speech rows. Decisions: [Technology Decisions](10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence).

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P1A history | Evolve `IMemoryStore`; durable `LastEntrySequence` independent of the `Entries` window; retain older rows; messages newest/`before`/`after` with `hasOlder`; one public history projection; UI Load earlier messages with scroll-anchor preservation | **Observed** (P1A-1/P1A-2/P1A-3) |
| P1B lifecycle | Additive `lifecycleStatus` Active/Paused/Completed/Expired/Cancelled/Ended; protocol-v1 `status` compatible; `SessionPurpose` Ongoing\|Goal; one `TransitionLifecycle`; TimeProvider attached deadlines and detached atomic expiry; RequestComplete evaluator distinct from RequestDeactivate | **Observed** (P1B-1/P1B-2/P1B-3/P1B-4) |
| P1C speech locale | Effective locale session override > agent default > fallback; Application BCP-47 validation; Browser STT tag / TTS exact-then-base-then-compatible; hosted hints in adapters; Voice fails clearly, text remains; realtime OpenAI STT unselectable | **Observed** for Application, adapters, Speech locale Select, Playwright override/fallback, and unedited Chrome 153 `fr-FR` STT/TTS smoke (`p1-final-fr-smoke-r2`) on HEAD `5764010` |
| P1-Final | Exact live `synthetic.yml` plus Compose; catalog/archive/pause regressions remain; P0 Chrome Voice checklist re-run | **Frozen** on `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`, 2026-09-19). CI/Synthetic + Compose verification is green on that HEAD (Real V4.1 Flash development/demo default documented). Do not reopen P1. Prior verified repair `15930985f54e2e6bf4019dd0d8040796883021c7` (2026-09-19): Domain 40; Infra 112/9 skip; App 356 blame-hang; API 123; Vitest 328; Playwright 34 (`data/playwright/synthetic.db`); Compose-equivalent :5088. P2D followed this freeze; P2A and P2B are observed separately |

## P2D — Session model selection and inference controls (observed)

This table does not reopen P1. P2B structured-response work is observed separately and does not reopen P2D.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2D catalog | Trusted `IModelCatalog` / `ILanguageModelResolver`; Synthetic fake models; Real default `deepseek-v41-flash` / `deepseek/deepseek-v4.1-flash` / medium; Real also offers `gpt-4o-mini-2024-07-18`, `openrouter-free`, `gpt-4.1`, and `gpt-5.6-luna` | **Observed** |
| P2D persistence | Concrete `SessionModelSelection`; legacy pin before first post-upgrade generation; per-turn provenance | **Observed** |
| P2D runtime | Session-aware resolve for Conversation/Initiative/CompletionEvaluation; persist-before-use live switch; `SessionBusy` while generating | **Observed** |
| P2D API/UI | Safe catalog API; create/mutate catalog-level choices; Codex-like Default + effort controls; session isolation | **Observed** |

P2D key-free gate (2026-09-19): Domain 42; Infrastructure 120 passed / 10 skipped; Application 380 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 135; web 340 unit tests and production build; `CI=1` Playwright 35 including two-session Synthetic catalog isolation; `scripts/compose-sqlite-volume.sh` passed on :5080. Opt-in `OpenRouter_deepseek_v41_default_accepts_tools_request` was skipped (no process `OPENROUTER_API_KEY`). P1 lifecycle/history/speech tests were not weakened.

## P2A — First-class progress vs final assistant output (observed)

This table does not reopen P1 or P2D. P2B is **frozen** on `e0e8a55`. Do not reopen P2B without a reproducible regression.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2A progress contract | Additive `ResponseProgressOutput` / `agent.progress` with envelope `responseId`, optional runtime `operationId`, specified camelCase kinds/states, trusted bounded message | **Observed** (P2A-1) |
| P2A runtime | Attachment and tool progress start/complete/fail; no Preparing/Finalizing/WaitingExternal synthesis; finish on complete/interrupt/cancel/fail/detach; reasoning never becomes progress | **Observed** (P2A-2) |
| P2A UI | One replaceable `activeProgress`; clears on terminal/text/blocks/ready/disconnect/session switch; connection and pending Voice still win | **Observed** (P2A-3) |
| P2A verification | Playwright progress-to-final + reload/history + disconnect/reconnect + session-switch; Voice TTS exclusion; kind/state telemetry; key-free `synthetic.yml` plus Compose | **Observed** (P2A-4) |

P2A key-free gate (2026-09-20): git HEAD `5effbb5e0942b2176c970c3a6f1b79fbaa985f8d` plus the P2A working tree (new-run evidence; no retired-run artifact). `npm ci` in `tests/realtime-js`; Domain 64; Infrastructure 133 passed / 12 skipped; Application 436 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 155; web `pnpm install --frozen-lockfile`, 376 unit tests, production build; `CI=1 pnpm exec playwright test` 36 passed including `progress is visible, replaced, cleared on final, reload, disconnect, and session switch`; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). Optional Real-provider probes were not run.

## P2B — Validated model response envelope (observed)

This table does not reopen P1, P2D, or P2A. P2B is **frozen** on `e0e8a55`. **P2E** and **P2C** are observed/frozen. **P2 closed/frozen** on `47d6ff6` after mandatory whole-output review (see [P2-Final](#p2-final--reconcile-and-close-p2-production-evidence)). Do not reopen P2, P2B, P2E, or P2C without a reproducible regression.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2B speech semantics | Domain `same`/`custom`/`none`; legacy `speechText`; `speech.text` on `same` when playback coordinate differs from display; rejected custom normalizes to effective `same`/`none` | **Observed** |
| P2B semantic contract | `ModelResponseContract`, `ModelDisplayDelta` / `ModelSemanticResponseReady`; SessionRuntime cutover; public `speechText` custom-only; `agent.speech.projection` `mode` | **Observed** |
| P2B Infrastructure | Native JSON when catalog `StructuredOutput`; compatibility first-wins `[[speech:]]` including `[[speech:none]]`; `SemanticMatches` speech guard | **Observed** |
| P2B UI/e2e | Speech text vs Spoken; structured Finalizing at semantic validation only; Alpha without visible markers | **Observed** |
| P2B verification | Key-free `synthetic.yml` plus Compose; Real probes skipped without keys | **Observed** (P2B gate) |

**P2B freeze:** `e0e8a55b111b190b63dfe5a2a53d0c59d0a06a59` (`e0e8a55`, 2026-09-20) with CI/Synthetic + Compose green on that HEAD (workflow run `35496178496`). Prior descendant gate on `c6e0735` (run `35496153659`) remains corroborating evidence. Key-free gate counts on `e0e8a55`: Domain 75; Infrastructure 178 passed / 12 skipped; Application 457 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 155; web unit tests, production build, and `CI=1` Playwright per `.github/workflows/synthetic.yml`; `scripts/compose-sqlite-volume.sh` passed. Optional Real-provider probes were not run. Do not reopen P2B without a reproducible regression. Closure spans envelope persistence/review (`583f021`–`a8f41f7`), public custom-only `speechText` with live `agent.speech.projection` mode/telemetry (`2fc96e6`), superseded attachment idle/output production (`aedb32b`–`2561949`), and causation-synchronized gated attachment regression (`5bfa5a9`–`c6e0735`). Earlier gates on `5effbb5`, `2fc96e6`, and `aedb32b` remain historical evidence only.

## P2E — Multimodal image-input capability closure (observed)

This table does not reopen P1, P2D, P2A, or P2B, or **P2** overall (closed/frozen on `47d6ff6`; see [P2-Final](#p2-final--reconcile-and-close-p2-production-evidence)).

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2E representation | Sanitized `ModelImageContent.ContentType` matches output bytes; WebP/GIF static projection to PNG; processor cache `attachment-processors/2` | **Observed** (P2E-1) |
| P2E admission | `UserTurnCapabilityValidator` before durable user text; runtime defense after attachment processing; `ModelCapabilityUnsupported` (409, non-fatal) | **Observed** (P2E-2) |
| P2E UX | Catalog `vision` metadata; ModelPicker Vision indicator; composer compatibility guard; Synthetic `scripted-vision`; Playwright attach→block→switch→send | **Observed** (P2E-3) |
| P2E verification | Full proposal §11 regressions plus §23 key-free gate and Compose on one freeze HEAD | **Observed** (P2E-4) |

**P2E freeze HEAD:** `961fb277d913db1edd454b8f635c8017e63a3005` (`961fb27`, 2026-09-21; implementation `0eeb27c`–`a502266` plus freeze documentation on that commit). Local gate matching `.github/workflows/synthetic.yml`: `npm ci` in `tests/realtime-js`; Domain 75 passed; Infrastructure 181 passed / 12 skipped; Application 462 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 157 passed; web `pnpm install --frozen-lockfile`, 384 unit tests, production build; `CI=1 pnpm exec playwright test` 40 passed including `scripted-vision.spec.ts`; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). P2 overall closure gate on `47d6ff6` (workflow run `35552740853`) — see [P2-Final](#p2-final--reconcile-and-close-p2-production-evidence). Optional Real vision probe **SKIPPED — credentials unavailable**. Do not reopen P2E without a reproducible regression. Historical image re-inspection is owned by [P3A](#p3a--historical-multimodal-attachment-reread-observedfrozen), not P2E.

## P2C — Personalization boundary (observed)

This table does not reopen P1, P2D, P2A, P2B, P2E, or **P2** overall (closed/frozen on `47d6ff6`; see [P2-Final](#p2-final--reconcile-and-close-p2-production-evidence)).

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2C persistence | Typed `UserProfileValue`/`UserProfileValueSource`; expanded allowlist; legacy string JSON compatibility; InMemory/SQLite parity | **Observed** (P2C-1) |
| P2C mutation API | `ILocalUserProfileService`; owner `GET|PATCH /api/v2/profile`; optimistic revision; browser `UserSet` stamping | **Observed** (P2C-2) |
| P2C live refresh | `ProfileUpdatedReceived` mailbox apply; SessionHost fan-out; stale/mismatch ignored; session revision unchanged | **Observed** (P2C-3) |
| P2C verification | Proposal §20 profile regressions plus §23 key-free gate and Compose on one freeze HEAD | **Observed** (P2C-4) |

**P2C freeze HEAD:** `ce8c6ff443c6d7eb30789d0fdbe31e66e885a213` (`ce8c6ff`, 2026-09-21; implementation `4ae1095`–`c636b28`, then freeze documentation and §23 gate on this commit). Local gate matching `.github/workflows/synthetic.yml`: `npm ci` in `tests/realtime-js`; Domain 76 passed; Infrastructure 192 passed / 6 skipped; Application 470 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 161 passed; web `pnpm install --frozen-lockfile`, 384 unit tests, production build; `CI=1 pnpm exec playwright test` 40 passed; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). P2 overall closure gate on `47d6ff6` (workflow run `35552740853`) — see [P2-Final](#p2-final--reconcile-and-close-p2-production-evidence). Optional Real probes **SKIPPED — credentials unavailable**. Do not reopen P2C without a reproducible regression. Memory-derived personalization and inferred facts remain P4-owned.

## P2-Final — Reconcile and close P2 (production evidence)

P2A, P2B, P2D, P2E, and P2C remain observed/frozen on their recorded HEADs. Proposal §26 stop conditions trace to shipped artifacts plus mandatory whole-output review and the final gate on closure HEAD `47d6ff6`. **P2 closed/frozen** on `47d6ff65142d2d454c4aa3101b0f43a38f01389a` (`47d6ff6`, 2026-09-21). Do not reopen P2 without a reproducible regression.

**Mandatory whole-output review:** complete (2026-09-21; TDP production evidence). **Closure repair** on `47d6ff6`: Real-profile catalog fallback when base appsettings ships the full scripted catalog; best-effort live profile notification after durable save; trim `preferredName` before persist.

**Final gate (closure HEAD `47d6ff6`):** proposal §23 key-free Synthetic + Compose gate green on that exact HEAD (hosted workflow run `35552740853`). Observed pass/skip counts are authoritative in TDP production evidence for P2-Final (not duplicated here to avoid doc/SHA drift). Optional Real structured-response and vision probes **SKIPPED — credentials unavailable** (`OPENROUTER_API_KEY` / `OPENAI_API_KEY` absent); do not treat them as verified Real-provider behavior.

**Roadmap handoff:** **P3** freeze was reopened for a focused email/approval correction pass after review of `27efe17`; **P4** follows once this correction tree is frozen (`TODO.md`, proposal §26).

## P3A — Historical multimodal attachment reread (observed/frozen)

P3A introduces typed non-text tool results, safe historical image rehydration through `attachments.read`, locked OpenAI-compatible wire projection for image-bearing tool results, and Synthetic verification. **Implementation/gate HEAD:** `c0f8a85` (2026-09-21) with focused-output review closure for P3A-1–P3A-3 on that HEAD; freeze documentation in the P3A-4 production batch commit on `main`. See [P3A freeze report](reports/p3a-freeze.md) for gate counts and optional Real probe status.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P3A-0 baseline | Green key-free Synthetic gate on implementation-start HEAD | **Observed** |
| P3A-1 typed results | `ToolExecutionResult` with budgeted `Text` and ephemeral `Parts` | **Observed** |
| P3A-2 rehydration | `attachments.read` image path + `ToolResultAdmission` vision gate | **Observed** |
| P3A-3 projection | OpenAI `MapMessages` tool→multipart sequence; Scripted historical reread | **Observed** |
| P3A-4 verification | Playwright historical reread + non-vision refusal; docs; full §23 gate | **Observed** |

## P3B–P3F — Tools, web, approval, email, assistant capability closure

P3B–P3D deliver trusted registry/policy, workspace/artifact ergonomics, bounded public web, live-session approval, and provider-neutral email with draft-bound send approval. The freeze on `27efe17` was reopened after review found email/approval boundary defects. Key-free correction HEAD `e255916` is historical. P3F closes the assistant capability gap: working-directory path segments, `workspace.search` / `workspace.move`, approval-gated `http.request`, `general-assistant` v7, and the former 32-name allowlist bound (removed by the subsequent capability enhancement above). `demo.sensitive_action` remains on `approval-demo` for the harness. Sandbox networking, calendar, and GitHub mutations stay outside this closure. See the [P3 closure report](reports/p3-freeze-candidate.md).

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P3B-1 registry/policy | `ToolRegistry`, `ToolEffect`, Allow/Deny execution recheck | **Observed** (TDP) |
| P3B-2 workspace/artifacts | `workspace.list`/`patch`, `artifacts.create_from_workspace` | **Observed** (TDP) |
| P3C-1 public web foundation | SSRF-safe fetch, search ports, Synthetic/Brave | **Observed** (TDP) |
| P3C-2 web tools | `web.search`/`web.fetch`, `general-assistant` v2 | **Observed** (TDP) |
| P3D-1 approval | RequireApproval, protocol/UI, Synthetic sensitive action on `approval-demo`; human wait isolated from 30 s/120 s execution clocks | **Observed** (correction) |
| P3D-2 email | `email.*`, Gmail/Synthetic providers, exact-draft hash including Bcc, MimeKit MIME, Gmail `drafts.send` with approved `message.raw` | **Observed** |
| P3E reconcile | Docs/TODO/report alignment for the earlier correction | **Observed** |
| P3F capability closure | `workspace.search`/`workspace.move`, cwd segment normalization, `http.request` as `SensitiveWrite`, v7 allowlist, `Content-Type` canonicalization, shared text charset decode, provider error message off by default | **Observed / key-free frozen** on `4dbb920` (see [P3 closure report](reports/p3-freeze-candidate.md); implementation bulk `da93489`) |

General Assistant exit for this closure:

- naturally read/write/patch/list/search its own workspace
- use relative paths like a normal working directory
- inspect current and historical attachments in Synthetic and fake-provider paths; **Real GPT-4o mini historical reread remains red on the opt-in SessionRuntime probe (2026-09-22)**
- search the public web when Brave is configured
- fetch pages/data
- make bounded approved generic HTTP/API requests
- create/export artifacts
- use the offline sandbox
- search/read/draft/send email with the existing approval boundary
- surface actionable failures instead of an opaque provider body

**Roadmap handoff:** **P6** is **frozen** on **`30adaeb`** (workflow [`36085265506`](https://github.com/trannamtrung1st/agent-core/actions/runs/36085265506) green; last behavior **`bef77d1`**; core **`2067a44`**; runtime closure **`aeefffc`**). **P7 Harness Admin** is **frozen** on follow-up tree **`2acb1a8`** (last harness behavior **`1090535`**; gate repair **`9519a83`**–**`2acb1a8`**; prior canonical **`f4107d7`** / workflow [`36239630112`](https://github.com/trannamtrung1st/agent-core/actions/runs/36239630112) green; closure repair **`479b637`** → **`f4107d7`**). Post-freeze **`ConversationTurnExecution`** observer-durability appendix is **closed** on **`2c4d46f`** (last behavior **`93cb2ab`**; workflow [**`36336971087`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36336971087) green; separate from harness W01–W08; not autonomous execution). Scope, evidence map, and P8+ execution-control deferrals: [P7 closure report](reports/p7-freeze-candidate.md). **P7.5** architecture consolidation is frozen on `70a5720` ([workflow `36368766449`](https://github.com/trannamtrung1st/agent-core/actions/runs/36368766449) green). **P7.6** Admin usability closure is frozen on `17d89ae` ([workflow `36427670239`](https://github.com/trannamtrung1st/agent-core/actions/runs/36427670239) green). **P7.7** operational diagnosability is frozen on `40a1d92` ([workflow `36594702224`](https://github.com/trannamtrung1st/agent-core/actions/runs/36594702224) green). Post-freeze diagnostics, learned-memory admission, and memory-receipt follow-up is **closed** on **`1cadf46`** ([workflow **`36667172857`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green); see [p7.7-diagnostic-followup.md](reports/p7.7-diagnostic-followup.md). That follow-up does not move the P7.7 SHA. **P8** Agent Step, Chat action, and Definition Skills are **frozen** on `ca3eb23` ([workflow `36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green; [closure report](reports/p8-freeze-candidate.md)). Post-freeze provider-contract correction **`6fda4c5`**, CI stabilization **`c9aec29`**, and appendix closure workflow [**`36745126226`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36745126226) green do not move the freeze SHA; post-freeze P8 follow-up is **closed**. **P8.5** application messaging and dynamic Skill load are **closed** on `1461567` ([workflow `36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green; [closure report](reports/p8.5-freeze-candidate.md)). `app.message.send` and `skills.load` stay on the existing user-turn pump. The P8 freeze SHA is unchanged. **P9** visible browser is **closed** on `bba1de4` ([workflow `36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) green; [closure report](reports/p9-freeze-candidate.md)). **P9.5** is **closed** on `1012653` (hosted Synthetic [`37111979501`](https://github.com/trannamtrung1st/agent-core/actions/runs/37111979501) green). A later bounded follow-up projects that connection in Chat by display name and status. Admin names nopCommerce as the supported application type. It does not move the P9.5 closure SHA, add a second connection, or introduce a provider registry. A post-P9.5 bounded browser settle lets observations wait for a quiet visible page and lets `browser.wait_for` request `condition` `stable` inside 100–5000 ms. That settle does not move the P9 SHA `bba1de4` or the P9.5 SHA `1012653`. The post-P9.5 browser and store-review enhancement is recorded on `6f6ff42` (hosted Synthetic [`37128161642`](https://github.com/trannamtrung1st/agent-core/actions/runs/37128161642) green) and does not move `1012653`. **P9.6** is **closed** on `d033bc61` ([workflow `37187663286`](https://github.com/trannamtrung1st/agent-core/actions/runs/37187663286) green; [closure report](reports/p9.6-freeze-candidate.md)). Webhook credentials belong to an Event Source; agents subscribe with Trigger Registrations; admission snapshots `ExternalEventDelivery` rows and the scheduler resumes pending fan-out after restart. The nopCommerce plugin registers `OrderPlacedConsumer` through `INopStartup`; guest checkout **order 14** produced automatic `order-14-placed` admission without `emit-order-placed.sh`. Real-store delivery-ledger, dedupe, and unattended browser completion evidence are in that report. It does not move `bba1de4` or `1012653`. P10 and P11 remain requirement-triggered. Real browser workflows use the Core-owned budgets in [Technology Decisions](10-technology-decisions.md#planned-resource-limits); Local Journey A session `fcd46bb2-d1fb-4ea1-9560-4b39b0873281` passed inside the interactive browser profile. [p9.5-freeze-candidate.md](reports/p9.5-freeze-candidate.md) records that closure. Journey D is accepted there on the recovery result. **P5** (events and configurable triggers) is **frozen** on `4bbc0c1` (`4bbc0c17bc54746f87fd211174690659869e3e45`, 2026-09-24) with hosted Synthetic + Compose **green** (workflow `35954811544`). See [P5 closure report](reports/p5-freeze-candidate.md). **P4** is **frozen** on `822028f` (workflow `35806764609`). See [P4 closure report](reports/p4-freeze-candidate.md). **P3 key-free freeze** on `4dbb920` (workflow `35682808408`). See [P3 closure report](reports/p3-freeze-candidate.md).

**P4A local checkpoint** on this working tree (not a hosted workflow, not a P4 freeze): `npm ci` in `tests/realtime-js`; Domain 77 passed; Infrastructure 256 passed / 13 skipped; Application 580 passed / 1 skipped with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 167 passed; web `pnpm install --frozen-lockfile`, Vitest 50 files / 392 passed, production build; `CI=1 pnpm exec playwright test` 47 passed, including `e2e/long-session-compaction.spec.ts`; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). Those commands ran before this paragraph and the restore-window sentence in [Backend Implementation](12-backend-implementation-spec.md). That documentation does not change runtime behavior.

**P4B local checkpoint** on this working tree (not a hosted workflow, not a P4 freeze): `npm ci` in `tests/realtime-js`; Domain 77 passed; Infrastructure 267 passed / 13 skipped; Application 587 passed / 1 skipped with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 167 passed; web `pnpm install --frozen-lockfile`, Vitest 50 files / 392 passed, production build; `CI=1 pnpm exec playwright test` 47 passed; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). Chromium was already installed, so `playwright install chromium --with-deps` was not re-run. Those commands ran before this paragraph. The checkpoint test is `SessionMemoryCheckpointTests`. P4C had not started at that checkpoint.

**P4 local closure candidate** (historical, pre-repair): `npm ci` in `tests/realtime-js`; Domain 77 passed; Infrastructure 267 passed / 13 skipped; Application 594 passed / 1 skipped with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 167 passed; web `pnpm install --frozen-lockfile`, Vitest 50 files / 392 passed, production build; `pnpm exec playwright install chromium --with-deps` exited 0; `CI=1 pnpm exec playwright test` 47 passed; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). Those commands ran on the working tree that already contained `P4ClosureTests` before the post-review repairs on `2fbc7d5` and `822028f`.

**P4 freeze** on **`822028f`**: hosted workflow **`35806764609`** — Synthetic offline gates and Compose smoke **green**. Closure report: [p4-freeze-candidate.md](reports/p4-freeze-candidate.md). Do not reopen P4 without a reproducible regression.

**P5 freeze** on **`4bbc0c1`**: hosted workflow [**`35954811544`**](https://github.com/trannamtrung1st/agent-core/actions/runs/35954811544) — Synthetic offline gates and Compose smoke **green** on that exact SHA. Implementation includes durable owner-scoped schedules (OneShot, Daily, Weekly, FixedInterval), deterministic scheduler with O(1) fixed-interval coalescing, current-turn action-specific tools, bounded schedule referents and one-follow-up drafts, reminder-only scheduled-occurrence delivery, one allowlisted durable order event, live-or-`AwaitingDurableWork` routing (execution deferred to P6), `general-assistant` v10 fixed-interval policy without mutating v8/v9, and the Schedules drawer. Closure report: [p5-freeze-candidate.md](reports/p5-freeze-candidate.md). Do not reopen P5 without a reproducible regression. **P6 frozen** on **`30adaeb`**: workflow [**`36085265506`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36085265506) — Synthetic offline gates and Compose smoke **green** on that exact SHA (last behavior **`bef77d1`**). Closure report: [p6-freeze-candidate.md](reports/p6-freeze-candidate.md). Do not reopen P6 without a reproducible regression. **P7 Harness Admin frozen** on **`2acb1a8`** (last harness behavior **`1090535`**; workflow [`36253536025`](https://github.com/trannamtrung1st/agent-core/actions/runs/36253536025) on **`9519a83`** was 56/57 PW; fixed in **`2acb1a8`**). Session observer-durability evidence on **`93cb2ab`**–**`3e75934`** is documented in [P7 closure report](reports/p7-freeze-candidate.md) and does not extend P7 into run-control or Workflow orchestration. **P7.5** architecture consolidation is frozen on `70a5720` ([workflow `36368766449`](https://github.com/trannamtrung1st/agent-core/actions/runs/36368766449) green). **P7.6** Admin usability closure is frozen on `17d89ae` ([workflow `36427670239`](https://github.com/trannamtrung1st/agent-core/actions/runs/36427670239) green). **P7.7** operational diagnosability is frozen on `40a1d92` ([workflow `36594702224`](https://github.com/trannamtrung1st/agent-core/actions/runs/36594702224) green). Post-freeze diagnostics, learned-memory admission, and memory-receipt follow-up is **closed** on **`1cadf46`** ([workflow **`36667172857`**](https://github.com/trannamtrung1st/agent-core/actions/runs/36667172857) green); see [p7.7-diagnostic-followup.md](reports/p7.7-diagnostic-followup.md). That follow-up does not move the P7.7 SHA. **P8** Agent Step, Chat action, and Definition Skills are **frozen** on `ca3eb23` ([workflow `36696902928`](https://github.com/trannamtrung1st/agent-core/actions/runs/36696902928) green; [closure report](reports/p8-freeze-candidate.md)). **P8.5** is **closed** on `1461567` ([workflow `36770385588`](https://github.com/trannamtrung1st/agent-core/actions/runs/36770385588) green; [closure report](reports/p8.5-freeze-candidate.md)). **P9** visible browser is **closed** on `bba1de4` ([workflow `36890525463`](https://github.com/trannamtrung1st/agent-core/actions/runs/36890525463) green; [closure report](reports/p9-freeze-candidate.md)).

## Handoff rule

Implementation begins with Milestone 1 in a separate task. Native realtime is not an implementation milestone, prerequisite or runtime branch anywhere in this MVP plan. If a provider cannot meet a capability, implement the specified degraded policy and report its measured trade-off; do not quietly change the architecture. Each implementation milestone should update run/test instructions to actual commands as its artifacts are introduced. Apply the [hosted-provider verification policy](10-technology-decisions.md#decision-default-verification-is-offline-live-providers-are-explicit-opt-in): default tests stay Synthetic/offline; OpenRouter opt-in adapter smoke may use `openrouter/free`; the Real/demo catalog default is the shipped fixed model ID `deepseek/deepseek-v4.1-flash`, and the same catalog also offers `openai/gpt-4o-mini-2024-07-18`, `openrouter/free`, `openai/gpt-4.1`, and `openai/gpt-5.6-luna` as session choices; OpenAI speech live checks may wait for `OPENAI_API_KEY`. Intended repository CI is GitHub Actions (`.github/workflows/synthetic.yml`).

## P9.7 — Agent-assisted / self-managed harness authoring

P9.6 remains closed. **P9.7 is frozen on behavior SHA `8f5afa00`** ([hosted Synthetic green](https://github.com/trannamtrung1st/agent-core/actions/runs/37224218680)); [final verification](reports/p9.7-final-verification.md) covers subsequent Skill, Admin and provider corrections. The Admin-first closure on `11a3d25c` is historical and superseded as the current contract. Implementation uses the ordinary Chat model/tool loop, contextual instance-authorized semantic capabilities, ordinary-source receipts, exact approvals, revision-bound Core checks, immutable internal publication and future-Session adoption. Managed knowledge may auto-promote; Assisted changes, instructions and all tool proposals remain approval-bound. Admin governs policy, inspection and freeze. Current Session pins stay immutable.

[First-gate report](reports/p9.7-chat-first-freeze-candidate.md) owns the historical 20 correction acceptance criteria and journeys A–F. Final focused/full backend, frontend tests/build, P9.7 and existing browser/background/trigger regressions, runtime verification, docs consistency and exact-behavior-SHA hosted gates passed. The bounded Real DeepSeek Kubernetes create/use/update/use/repair sequence passed; GPT-4o mini wire compatibility was fixed but content fidelity was insufficient. Use DeepSeek for the harness-learning demo. Stop P9.7 architecture work and proceed to investor-demo stabilization. Existing P8/P8.5/P9/P9.5/P9.6 freezes stay closed; P10/P11 remain requirement-triggered.

## P9.8-P9.9 continuity implementation

A dedicated Experience-to-Memory promotion UI/capability is deferred. Ordinary explicit memory admission remains governed by existing memory policy; no automatic promotion occurs. Manual retrospective retry and a recent-Session source picker are deferred usability follow-ups. These do not expand thought authority or reopen P10/P11.

Historical P9.8/P9.9 execution record, superseded by unified Automation and AgentRun ownership. P9.7's frozen behavior remains `8f5afa00`. P9.8 implemented separate durable owned Experience, strict bounded retrospective generation, context/read-only inspection, existing stable lifecycle hooks, independent recoverable WorkItems, owner controls and Synthetic evidence. P9.9 builds on it through owner-only thought registrations in the existing scheduler/occurrence/work path, immutable admitted prompt/model snapshots, overlap coalescing, server-origin policy, exact approvals, structured quiet outcomes and instance Admin UX.

| Slice | Acceptance owner |
| --- | --- |
| P9.8A contract/persistence | Domain record, IExperienceStore, InMemory/SQLite, migration/reopen/visibility CAS/tombstone |
| P9.8B generation | stable visible projections, named native tool, strict validation, secrets/retry/source independence |
| P9.8C recall | same-instance 6000-character layer and bounded experience.recent lookup; no memory/policy promotion |
| P9.8D boundaries | explicit Session request, acknowledged pause/end, substantive terminal work, admission repair; no recursive retrospection |
| P9.8E UI | instance enable/review/provenance/suppress/delete/reset and shared background inspection |
| P9.8F evidence | owning docs, repeatable demo, full offline gates and direct runtime/visual review |
| P9.9A origin | Core-assigned ThoughtActivation in normal ToolExecutionAdmission/ToolPolicy; client/model forgery denied |
| P9.9B scheduling | owner revisions/interval/prompt/model; atomic occurrence snapshot/pin; Run now parity and coalescing |
| P9.9C execution | existing bounded no-chat loop, Experience/memory/harness context, NoAction/action/attention and normal work failures |
| P9.9D policy | P9.7 permitted/approval/deny/freeze; no authority or registration self-bootstrap |
| P9.9E UI/operations | cadence/model/prompt, next/last/outcome, disabled/running/approval/error states, shared drawer |
| P9.9F evidence | P9.8 → P9.9 → P9.7 Synthetic journey, restart/approvals/full regression and synchronized docs |

**P9.8 and P9.9 are closed/frozen on behavior SHA `0a3330db`.** All A-F slices and mandatory local/hosted Synthetic gates passed. The [implementation report](reports/p9.8-p9.9-freeze-candidate.md) maps all 36 criteria; [final verification](reports/p9.8-p9.9-final-verification.md) owns current results and closure status. [Hosted Synthetic and Compose](https://github.com/trannamtrung1st/agent-core/actions/runs/37260497161) passed on that exact SHA, including the explicit continuity project. Stop P9.8/P9.9 architecture expansion. Existing historical freezes are unchanged. P10/P11 and broader autonomous/multi-agent/tenant authority remain requirement-triggered.

## Continuity enhancements v2 follow-on

Accepted implementation scope after frozen P9.8/P9.9: (1) bounded unified search/get and scope/provenance; (2) relevance-based automatic context; (3) durable checkpoint maintenance for active Sessions; (4) Admin schedule configuration and normal Run now admission; (5) shared retrieval in Thought with quiet NoAction and unchanged authority. Phase 6 synthesis/classifier/vector/Admin-search improvements remain optional and deferred. Acceptance AC1–AC17 and restart/source-independence gates are tracked in [the follow-on report](reports/continuity-enhancements-v2.md); acceptance requires runnable backend journeys, frontend tests/build and Synthetic browser journeys. Existing freezes remain unchanged.

## P9.10 identity state consolidation follow-on

Historical P9.10 execution record, superseded by unified Automation and AgentRun ownership. The accepted bounded follow-on added atomic Memory consolidation with lineage/capacity relief, separate Experience generalization and supersession, exact-authority learned-memory forget, a default-off durable CAS permission and maintenance tools through existing Thought/WorkItem execution. P9.8/P9.9 remain frozen on `0a3330db`; this phase does not redefine their original acceptance. P10/P11 remain unopened.

Required slices: semantic contracts/persistence; Memory authority/admission/atomicity; Experience lineage/visibility/atomicity; current policy/tool/approval/recovery integration; shared Admin presentation; Synthetic/full/local/hosted regression and a bounded Real-provider semantic check. No generic CRUD, automatic Experience-to-Memory promotion, autonomous forgetting, new scheduler, continuous cognition or context-compaction redesign.

Acceptance maps AC1–AC20 in [the P9.10 report](reports/p9.10-final-verification.md). **P9.10 is closed/frozen on verified candidate `71a9e6fd`**, with bounded Real semantic judgement and [exact-commit hosted Synthetic/Compose `37452986171`](https://github.com/trannamtrung1st/agent-core/actions/runs/37452986171) green on 2026-10-06. All required acceptance gates are complete.

The requested configurable Continuity maintenance cadence is included in that closure: operator polling policy, separate revisioned per-instance intervals and persisted evaluation claims, with Admin/API controls. It retains one hosted loop and existing Experience/Thought/consolidation ownership. The final hosted gates include the cadence enhancement and test synchronization repairs. Stop P9.10 expansion; P10/P11 remain requirement-triggered.

## Bounded post-P9.10 Agent Instance workspace

Historical accepted scope (superseded by the full migration): one managed identity-owned `/home`, explicit retain/checkout, bounded tool-driven read/list/search, optimistic replacement, owner Admin inspection/download/delete, source Session deletion survival and local SQLite/Compose restart durability. Prior P9.8/P9.9/P9.10 freezes remain unchanged; this does not start P10/P11. No shared/application/task workspace, remote storage or Artifact ownership change is included.

Acceptance requires focused/full backend and frontend gates, production build, canonical cross-session Synthetic browser journey, existing Artifact/continuity/browser regression, SQLite/Compose survival, synchronized docs, exact published behavior SHA and green hosted Synthetic/Compose on that SHA. Status and AC1–AC20 evidence are recorded in [final verification](reports/agent-instance-workspace-final-verification.md); local passing checks alone do not freeze this enhancement.

## Bounded workspace filesystem enhancement

This user-requested follow-on extends the completed Agent Workspace slice. It does not move historical phase freezes or start P10/P11. Scope: shared logical tree preflight, first-class empty directories, native mkdir/copy/tree move/delete/ordered batch in authorized scratch/home, normal approval/replay policy, durable tree-token concurrency, folder-aware Admin inspection and canonical contracts. Non-goals remain shell expansion, Unix command wrappers, chmod/links/watchers/mounts, cross-owner sharing/synchronization, automatic organization/history/cloud-drive semantics and a transactional filesystem engine.

Acceptance also requires v12/v13 to keep scratch-file-only move authority while explicit structural capabilities enable tree/home moves; scratch approval fingerprints and dynamic batch effects are deferred. Acceptance requires exact-byte tree operations and empty folders in both adapters, rejection before mutation for invalid batches/quota/security, explicit destructive approval, structured unexpected-failure state, Session/identity isolation, SQLite/Compose survival, existing workspace/Artifact/sandbox regressions and the affected live Synthetic browser flow. Verification status is recorded in [the filesystem report](reports/workspace-filesystem-final-verification.md); the original [Agent Workspace freeze](reports/agent-instance-workspace-final-verification.md) remains historical evidence.

## System Credentials migration (authorized follow-up)

**Closed/frozen on verified behavior `55d31056b871e05c553f09aa2946815280add3e3` (`55d31056`), 2026-10-07.** [Hosted Synthetic/Compose workflow `37584505116`](https://github.com/trannamtrung1st/agent-core/actions/runs/37584505116) completed successfully on that exact SHA; all five jobs are green. This bookkeeping commit changes documentation only and does not move the behavior freeze.

Implement reusable protected resources, explicit instance bindings and context-only safe metadata, then the direct Browser Password sink and generic profile/occurrence policy. Retire Application Connection production code/API/table/UI. Preserve historical P9.5/P9.6 evidence and independently owned Event/provider credentials. Update Secretary procedures, current docs and operational key-ring backup. Acceptance requires shared Credential journey, offline sink/intervention/redaction, migration/reopen/isolation, owner API/UI and responsive navigation, full local suites, Synthetic browser/Compose, Real nopCommerce and exact-candidate hosted CI. Completed acceptance and closure evidence are recorded in [System Credentials verification](reports/system-credentials-verification.md). P10/P11 stay unopened; a hosted vault provider is a P11C future dependency only.

## Unified Automation cutover

The original unified Automation cutover passed all five hosted Synthetic/Compose jobs on **`60dda1df`** (2026-10-07; [historical evidence](https://github.com/trannamtrung1st/agent-core/actions/runs/37600240526)). Subsequent corrections are covered by the final Activation/AgentRun behavior `8cec78c5d47a43e0236a5c38f2e312f4e36ce283`, with all five jobs [green](https://github.com/trannamtrung1st/agent-core/actions/runs/37756244306). One Automation resource with Schedule/Event triggers, generic durable runs and semantic Experience tools supersedes historical behavioral Thoughts, separate Schedule/event-subscription resources, special retrospective execution and configurable semantic review cadence. Final evidence is recorded in [unified Automation verification](reports/unified-automation-model-verification.md). Historical reports/SHAs remain records of their original versions. P10/P11 remain unopened.


## Agent Instance Skills full migration gate

This approved post-MVP cutover supersedes only the Skill ownership/activation portions of P8/P8.5/P9.7. It adds first-class instance Skills, stable-id Definition enabled state, explicit Always/OnDemand, full live/durable execution catalog pinning, ordinary authorized self-management and the Admin Skills tab. Retained Harness knowledge/instruction/tool behavior keeps its existing approval/publication boundaries. No Role system, inheritance, name shadowing, synchronization, multi-agent runtime or compatibility adapter is introduced.

Acceptance requires atomic SQLite/InMemory transitions and customization, independent local persistence, no permission escalation, pinned procedure recovery, canonical-key load bounds, archived/foreign/stale rejection, and complete backend/frontend/Synthetic verification. All local gates and all five [hosted Synthetic/Compose jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37649033738) are green on exact behavior SHA `9c2d40e0d3a72268b72adbbecaf0654b5918b294`. This migration is closed/frozen on that candidate. Verification and closure evidence live in [migration verification](reports/instance-skills-migration-verification.md); earlier freeze SHAs are not moved.

## Automation destinations and reliable background report-back

This authorized bounded follow-up extends the frozen AgentRun/Browser v2 substrate without reopening their historical milestones or P10/P11. Phases are A domain/output contract, B persistence/atomic target admission, C routing/headless serialization, D unified reporting, E trusted authoring/API, F shared destination/delivery UI, G bounded Impeccable review/docs/full verification.

Acceptance requires J01–J16 in [Testing](16-testing-strategy.md#automation-destination-and-report-back-acceptance), InMemory/SQLite parity, no implicit fallback or second engine, Initiative-off immediate reporting and explicit-only Automation callbacks. Keep proposal history and prior frozen reports unchanged. Current implementation/evidence is in the [enhancement report](reports/automation-targets-background-reportback-verification.md); TODO owns final exact behavior SHA and five-job hosted closure. Implementation is not frozen until those final gates pass.


## Durable completion inbox, result handoff and wait successor

Authorized successor to the completed Automation destinations/report-back enhancement, starting at 513cbed0. Implementation adds canonical accounting, active handoff, deterministic unhandled reporting and typed same-Run suspension through existing owners. P10/P11 remain unopened and prior freeze records remain historical. Acceptance is tracked in TODO and docs/reports/durable-completion-inbox-result-handoff-wait-verification.md. Local implementation and verification are recorded separately from exact-SHA five-job hosted closure; do not infer closure from the preceding enhancement’s workflow.

Closure: all five required hosted Synthetic/Compose jobs [passed](https://github.com/trannamtrung1st/agent-core/actions/runs/37799412089) on final behavior `9edccab46782d616b8b8f7464be9495933e8bdfd`. The successor is closed; the verification report preserves local failures/rechecks and exact gate counts. Documentation-only closure publication does not move the behavior freeze.

## Admin shared Events and authoring enhancement

The requested enhancement converges global Connections resources on Credentials and Events, Instance grants on Credentials, and Schedule/Event behavior on Automation. Shared Events replace the retired Event Source + Event Type authoring model with stable Event-ID subscriptions and bounded generic webhook evidence. Capability Form authoring uses canonical Selected/All and grouped authorized always projection. The data-preserving upgrade and client URL/envelope transition are owned by [Persistence](15-persistence-and-configuration.md#shared-event-resource-upgrade). [Verification report](reports/admin-events-ux-verification.md) records this working-tree change separately from historical freeze evidence. It does not open P10/P11 or establish hosted milestone acceptance.

## Historical Browser v2 reliability follow-up

Authorized focused work on the existing Browser v2/AgentRun substrate: shared remaining-byte snapshot projection, bounded semantic discovery and scoped reads, precise opaque-reference errors, dependable authorized bootstrap with closure, bounded model-guided recovery and truthful observed completion. No migration, new execution architecture, selector/script authority, immutable Definition rewriting or P10/P11 work. Required acceptance is the independent dense Chromium fixture, full Synthetic model/tool-loop, full Application/Infrastructure regressions, synchronized owning documents and exact-SHA hosted Synthetic gates. [The verification ledger](reports/browser-v2-reliability-verification.md) distinguishes local implementation from final acceptance.

### Browser model compatibility enhancement

The user authorized all review recommendations after browser discovery recovery `13ba2f59`. This follow-up simplifies the model-facing find schema, bounds repeated invalid strategies across checkpoints, derives trusted current/prior Run facts, and adds an opt-in two-origin synthetic SSO comparison. It publishes immutable General Assistant v20 / Secretary v7. The prior native cutover freeze and earlier CI evidence remain historical; current gates are tracked in [browser model compatibility verification](reports/browser-model-compatibility-verification.md). P10/P11 remain unopened.

### Browser Run finalization follow-up

The user authorized all reply-failure recommendations and deferred waiting for hosted CI. This extends the existing Session/AgentRun path with a reply reserve inside the unchanged interactive deadline, bounded tool-free finalization, precise cancellation/timeout causes, sanitized prior failure facts and separate action/reply presentation. It does not introduce another browser engine or replay side effects. [Finalization verification](reports/browser-run-finalization-verification.md) records local execution and deferred acceptance; P10/P11 remain unopened.

### Browser cleanup lifecycle follow-up

The Pump 002 review authorizes actionable native dialog recovery, confirmed explicit closure, cleanup time before reply finalization, independent sign-out evidence and terminal progress fencing. This remains the native `IBrowser` and owned Session/AgentRun path. [Cleanup verification](reports/browser-cleanup-lifecycle-verification.md) records local gates, isolated setup correction and deferred CI/real-application acceptance. P10/P11 remain unopened.

## Capability discovery UX and reliability follow-up

Authorized focused enhancement of existing `capabilities.load`: registry-based concrete intents, compact actionable outcomes, bounded ineffective discovery and same-Run restoration. Definition authority, configuration/runtime eligibility, sensitive approvals, attached/detached restrictions, Skill procedures and fresh independent Run state remain authoritative. No new discovery subsystem, model-specific behavior, Browser provider, automatic grants or P10/P11 work. Local implementation and final acceptance are tracked separately in [discovery verification](reports/capability-discovery-reliability-verification.md). Hosted CI is deferred at the user's direction; this follow-up is not frozen until all required jobs pass on the implementation SHA.

## Final native browser blocked-state recovery

Authorized focused reliability review after `195d53df`, composed with capability discovery `bc0a7e94`. Fix only reproduced pending-modal loops and native dialog lifecycle defects using existing BrowserEvidenceProgress, tool receipts, finalization and native cancellation fences. No Browser redesign, additional tool, broader authority or P10/P11 work. Actual Chromium and owned Run acceptance, local suites and exact-behavior hosted gates are recorded separately in [verification](reports/browser-blocked-dialog-recovery-verification.md).
