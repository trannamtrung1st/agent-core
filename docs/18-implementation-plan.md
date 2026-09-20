# Implementation Plan

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
| G — Bounded work | Typed tools, Support/Compliance workflows, step/time/output caps | **Observed:** OpenAI-compatible tool_calls mapping; Scripted Support/Compliance multi-step replies with Markdown and artifact refs; 12 / 30 s / 120 s / 8 MiB caps; host-path and Session-mutation denial; late tool results rejected; uploads never execute |
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
| Client speech | Shared SpeechSegmenter → `speech.output.segment`; playback ACK gates completion; Stop does not dequeue | ClientSpeechSegmentRuntimeTests; ClientSpeechMessagePackTests |
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
| P2D catalog | Trusted `IModelCatalog` / `ILanguageModelResolver`; Synthetic fake models; Real default `deepseek-v41-flash` / `deepseek/deepseek-v4.1-flash` / medium; Real also offers `gpt-4o-mini-2024-07-18` and `openrouter-free` | **Observed** |
| P2D persistence | Concrete `SessionModelSelection`; legacy pin before first post-upgrade generation; per-turn provenance | **Observed** |
| P2D runtime | Session-aware resolve for Conversation/Initiative/CompletionEvaluation; persist-before-use live switch; `SessionBusy` while generating | **Observed** |
| P2D API/UI | Safe catalog API; create/mutate catalog-level choices; Codex-like Default + effort controls; session isolation | **Observed** |

P2D key-free gate (2026-09-19): Domain 42; Infrastructure 120 passed / 10 skipped; Application 380 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 135; web 340 unit tests and production build; `CI=1` Playwright 35 including two-session Synthetic catalog isolation; `scripts/compose-sqlite-volume.sh` passed on :5080. Opt-in `OpenRouter_deepseek_v41_default_accepts_tools_request` was skipped (no process `OPENROUTER_API_KEY`). P1 lifecycle/history/speech tests were not weakened.

## P2A — First-class progress vs final assistant output (observed)

This table does not reopen P1 or P2D. P2B is tracked separately and is not frozen on `0906097`.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2A progress contract | Additive `ResponseProgressOutput` / `agent.progress` with envelope `responseId`, optional runtime `operationId`, specified camelCase kinds/states, trusted bounded message | **Observed** (P2A-1) |
| P2A runtime | Attachment and tool progress start/complete/fail; no Preparing/Finalizing/WaitingExternal synthesis; finish on complete/interrupt/cancel/fail/detach; reasoning never becomes progress | **Observed** (P2A-2) |
| P2A UI | One replaceable `activeProgress`; clears on terminal/text/blocks/ready/disconnect/session switch; connection and pending Voice still win | **Observed** (P2A-3) |
| P2A verification | Playwright progress-to-final + reload/history + disconnect/reconnect + session-switch; Voice TTS exclusion; kind/state telemetry; key-free `synthetic.yml` plus Compose | **Observed** (P2A-4) |

P2A key-free gate (2026-09-20): git HEAD `5effbb5e0942b2176c970c3a6f1b79fbaa985f8d` plus the P2A working tree (new-run evidence; no retired-run artifact). `npm ci` in `tests/realtime-js`; Domain 64; Infrastructure 133 passed / 12 skipped; Application 436 with `--blame-hang --blame-hang-timeout 5m` and no hang sequence; API 155; web `pnpm install --frozen-lockfile`, 376 unit tests, production build; `CI=1 pnpm exec playwright test` 36 passed including `progress is visible, replaced, cleared on final, reload, disconnect, and session switch`; `./scripts/compose-sqlite-volume.sh` passed (`compose sqlite volume check passed`). Optional Real-provider probes were not run.

## P2B — Validated model response envelope (pending closure)

This table does not reopen P1, P2D, or P2A. P2E/P2C/P6 have not started.

| Slice | Production behavior | Evidence |
| --- | --- | --- |
| P2B speech semantics | Domain `same`/`custom`/`none`; legacy `speechText`; `speech.text` on `same` when playback coordinate differs from display | **Implemented** (local); reopen gate pending |
| P2B semantic contract | `ModelResponseContract`, `ModelDisplayDelta` / `ModelSemanticResponseReady`; SessionRuntime cutover | **Implemented** (local) |
| P2B Infrastructure | Native JSON when catalog `StructuredOutput`; compatibility markers only in Infrastructure; `[[speech:none]]`; reject speech-only fallback | **Implemented** (local) |
| P2B UI/e2e | Speech text vs Spoken; structured Finalizing at semantic validation only; Alpha without visible markers | **Implemented** (local); full Playwright gate pending |
| P2B verification | Key-free `synthetic.yml` plus Compose; Real probes skipped without keys | **Pending** (post-`0906097` review fixes) |

P2B on commit `0906097` is **not frozen**. A follow-up working tree closes review blockers (same-mode derived speech persistence, fallback `displayText` validation, session attachment authorization, compatibility `none`, Finalizing progress timing). Re-run the full key-free Synthetic plus Compose gate before marking P2B observed again. Prior evidence on `5effbb5` plus the pre-review tree remains historical only.

## Handoff rule

Implementation begins with Milestone 1 in a separate task. Native realtime is not an implementation milestone, prerequisite or runtime branch anywhere in this MVP plan. If a provider cannot meet a capability, implement the specified degraded policy and report its measured trade-off; do not quietly change the architecture. Each implementation milestone should update run/test instructions to actual commands as its artifacts are introduced. Apply the [hosted-provider verification policy](10-technology-decisions.md#decision-default-verification-is-offline-live-providers-are-explicit-opt-in): default tests stay Synthetic/offline; OpenRouter opt-in adapter smoke may use `openrouter/free`; the Real/demo catalog default is the shipped fixed model ID `deepseek/deepseek-v4.1-flash`, and the same catalog also offers `openai/gpt-4o-mini-2024-07-18` and `openrouter/free` as session choices; OpenAI speech live checks may wait for `OPENAI_API_KEY`. Intended repository CI is GitHub Actions (`.github/workflows/synthetic.yml`).
