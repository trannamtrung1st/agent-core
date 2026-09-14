# Implementation Plan

This is the ordered implementation handoff. Each milestone must satisfy its acceptance criteria before dependent work begins. Scope remains conversational presence, not a general autonomous-agent platform. [Roadmap](08-development-roadmap.md) is the short index; this document owns the detailed gates. Observability hooks, cancellation and tests begin with the first slice, although full instrumentation/tuning is Milestone 12.

## Milestone 0 — Documentation/contracts finalized

- **Goal:** Make implementation decisions explicit and coherent.
- **Scope:** README and docs 01–18; ports, state machine, transport/audio contracts, examples, persistence and deployment direction.
- **Required interfaces:** Review ILanguageModel, speech ports, IMemoryStore, IAgentDefinitionStore, IInterruptionClassifier, IAgentBrain, IIdGenerator, ISessionOutput and ISessionAudioOutput for the composed pipeline.
- **Tests:** Markdown links/fences, JSON example parsing, cross-document naming and invariants audit; no application tests exist yet.
- **Acceptance criteria:** An implementation agent can determine project references, state ownership, event order, cancellation, audio format, reconnect and milestone sequence without choosing a new architecture. Changes contain Markdown only.
- **Explicit non-goals:** No solution/project/source/configuration/generated artifacts, package manifests, migrations or Dockerfile.

## Milestone 1 — Solution skeleton and synthetic text vertical slice

- **Goal:** Prove the boundaries with one offline text exchange.
- **Scope:** Create .NET project layout/tests and Vite strict TypeScript skeleton; DI, health, minimal lifecycle services, in-memory state, synthetic model and a test-host driver. Use a console/test harness invoking application services for streamed text until SignalR Milestone 5; do not add a temporary public text HTTP endpoint.
- **Required interfaces:** ILanguageModel, IIdGenerator, TimeProvider, ISessionOutput, initial IMemoryStore.
- **Tests:** Build references, xUnit stream ordering and cancellation, WebApplicationFactory /health and session creation, frontend build.
- **Acceptance criteria:** Synthetic boots without keys/network; an application integration test submits text and receives ordered normalized deltas and terminal output. Domain references no web/provider/persistence framework.
- **Explicit non-goals:** Real providers, voice, durable storage, complete browser realtime connection, MediatR or extra platform infrastructure.

## Milestone 2 — Agent Definition and Agent Runtime

- **Goal:** Run two reusable identities through the same runtime.
- **Scope:** JSON validation/loading, pinned versions, PromptContextBuilder, basic Agent Runtime/history/summary, IAgentBrain Speak/StaySilent, per-session mailbox ownership and supervised tasks.
- **Required interfaces:** IAgentDefinitionStore, IAgentBrain, ILanguageModel, IMemoryStore; definition/context records from docs 04/12/15.
- **Tests:** Complete example definitions deserialize/validate; missing aliases/version failures; exact prompt sections; current turn once; independent simultaneous sessions; deterministic snapshot revisions.
- **Acceptance criteria:** Examiner/support differ by definition only; no vendor DTO crosses Application; mutable state has a single owner and synthetic scripts work with both.
- **Explicit non-goals:** Agent editor, YAML, tools, native realtime implementation or model-assisted summarization.

## Milestone 3 — OpenAI-compatible streaming LLM and OpenRouter configuration

- **Goal:** Replace synthetic text capability with a real configurable HTTP adapter.
- **Scope:** OpenAICompatibleLanguageModel configured for OpenRouter hosted text, configurable model aliases per identity, application/harness streaming text (browser integration follows Milestone 5), IHttpClientFactory, private SSE parser, normalized failures, headers/model configuration, explicit timeouts, circuit breaker and no blind POST retries.
- **Required interfaces:** ILanguageModel, ModelRequest/ModelGenerationEvent, ModelCapabilities, ProviderFailure.
- **Tests:** Offline HTTP/SSE contract suite including arbitrary chunk boundaries, UTF-8, missing finish, cancellation, 429 and midstream failure; opt-in bounded real-provider smoke test.
- **Acceptance criteria:** Same runtime runs ScriptedLanguageModel or OpenAICompatibleLanguageModel via alias configuration; OpenRouter-specific IDs/options stay in Infrastructure and a local endpoint fixture requires no runtime changes; observed partial text is never replayed after a failure; credentials remain backend-only.
- **Explicit non-goals:** Vendor SDK coupling, tool calling, structured-output platform, speech implied by text compatibility.

## Milestone 4 — Interaction Controller with synthetic speech/events

- **Goal:** Establish deterministic turn-taking before live media.
- **Scope:** Orthogonal state machine, bounded candidate/utterance logic, fake speech/environment inputs, heuristics, TimeProvider timers, response cancellation and supersession guards.
- **Required interfaces:** IInterruptionClassifier, IAgentBrain, recognition event records, IIdGenerator, TimeProvider.
- **Tests:** “mhm” Continue, “wait” Interrupt, noise Ignore, final-before-ended, stale classifier and late model results, capability fallback, invalid timer generation.
- **Acceptance criteria:** Controlled tests reproduce R1→superseded→R2 and discard every stale R1 output; controller keeps processing while model is gated.
- **Explicit non-goals:** Real microphone, production semantic perfection, per-frame LLM classification, dedicated OS threads or actor framework.

## Milestone 5 — SignalR protocol and browser connection

- **Goal:** Deliver the synthetic text path through the real browser transport.
- **Scope:** /hubs/session, MessagePack camelCase DTOs, session attach/lease, frontend Zustand reducers, text UI, control sequencing, safe errors, binary method validation. Implement in-memory reconnect snapshot semantics now; durable resume comes in Milestone 11.
- **Required interfaces:** ISessionOutput, concrete SessionManager entry points, Contracts command/event/audio DTOs.
- **Tests:** Kestrel + JavaScript MessagePack fixtures, protocol mismatch, duplicates/gaps, second-connection rejection, browser synthetic text Playwright and connection-loss cleanup.
- **Acceptance criteria:** Text UI works offline end-to-end, late R1 deltas are rejected on both sides, hub has no runtime logic and browser cannot obtain provider secrets.
- **Explicit non-goals:** WebRTC, authentication system, unbounded event replay, audio DSP.

## Milestone 6 — Microphone, AudioWorklet and STT

- **Goal:** Convert continuously streamed microphone input into speech activity and normalized partial/final evidence.
- **Scope:** getUserMedia, worklet resampling/PCM16 encoding, VAD boundaries, bounded separate audio ingress, synthetic and independently configured streaming STT adapter, partial/final transcript flow, capability discovery and batch-only degraded fallback.
- **Required interfaces:** ISpeechRecognizer, ISpeechRecognitionSession, RecognitionCapabilities, AudioFrame; protocol InputAudioDto.
- **Tests:** Sample-rate conversion/sample offsets, byte ordering, overflow/discontinuity, boundary ordering, duplicate finals, batch capability degradation, permission/device errors.
- **Acceptance criteria:** Voice input produces one final user turn per utterance; raw audio never enters the normal mailbox/event log or persistence. Synthetic mode uses script-driven recognition with no keys.
- **Explicit non-goals:** Real TTS output, assuming text-provider speech compatibility, perfect VAD/echo removal.

## Milestone 7 — TTS streaming and playback

- **Goal:** Turn streamed model text into natural Speech Segments and start TTS playback before the full answer exists.
- **Scope:** ResponseTextAccumulator/SpeechSegmenter, synthetic and independently configured streaming TTS adapter, canonical conversion, output queue/worklet, timing marks, playback acknowledgements, full-duplex capture.
- **Required interfaces:** ISpeechSynthesizer, SpeechRequest/SpeechSynthesisEvent, OutputAudioDto and playback controls.
- **Tests:** Segment/sample continuity, empty final marker, underrun/overflow, cancellation, browser worklet acknowledgement, microphone remains active during output.
- **Acceptance criteria:** Playback starts before full response completion when capabilities permit; sequence/duration counters are correct; no HTML audio element handles streamed PCM; response completion waits for playback.
- **Explicit non-goals:** Perfect prosody/phoneme sync, native realtime providers, codecs in Agent Runtime.

## Milestone 8 — Full-duplex voice and continuous listening

- **Goal:** Keep the composed input and output paths active concurrently during a natural call.
- **Scope:** Integrate microphone/streaming STT with model/segment/TTS playback, independent lifetimes, mute/unmute, audio queue limits and simultaneous state projections.
- **Required interfaces:** ISpeechRecognitionSession, ILanguageModel, ISpeechSynthesizer, ISessionAudioOutput and Playback Progress controls.
- **Tests:** PCM ingress and Partial/Final Transcripts continue while R1 audio plays; slow TTS does not block STT; mute affects input only; network loss stops both paths safely; synthetic simulation needs no hardware.
- **Acceptance criteria:** No automatic microphone stop during agent speech, no record-submit-playback cycle, no multimodal reasoning requirement. Existing response identity guards remain active; the next milestone validates full semantic interruption and Spoken Until.
- **Explicit non-goals:** Speech-to-speech models, half-duplex fallback UX, perfect echo suppression or provider-specific reasoning logic.

## Milestone 9 — Semantic barge-in, ducking, supersession and spoken-until

- **Goal:** Make interruption reliable across provider, network and playback races.
- **Scope:** Local duck/restore, server interruption lifecycle, response tombstones, worklet flush, cancellation of LLM/TTS while STT continues, conservative heard context and history.
- **Required interfaces:** IInterruptionClassifier, model/speech cancellation contracts, playback.stopped/progress, context builder and memory snapshot fields.
- **Tests:** Interrupt at every pipeline boundary; intentionally non-cooperative late R1 text/audio/completion after R2; timing/no-timing estimates; late playback events ignored after supersession; semantic, speechAndFinal and speechActivity fallback policies.
- **Acceptance criteria:** “Wait” stops and replaces response; short backchannels usually continue in partial-capable mode; no stale R1 output plays after local supersession; next context excludes unplayed tail.
- **Explicit non-goals:** Perfect intent recognition, exact phoneme synchronization, muting microphone during output.

## Milestone 10 — Proactive interaction

- **Goal:** Offer useful initiative during active sessions with restraint.
- **Scope:** Idle/environment/unfinished triggers, policy usefulness rules, expiry/dedupe/cooldown, StaySilent, stale decision rechecks.
- **Required interfaces:** IAgentBrain, AgentTrigger, TimeProvider, synthetic environment driver.
- **Tests:** Idle trigger, StaySilent cooldown, one hint per silence period, input invalidates pending initiative, environment event during output queued/expired, no initiative while detached.
- **Acceptance criteria:** Examiner may offer help after silence; support can mention a simulated update; timers do not automatically speak or create repeated nudges.
- **Explicit non-goals:** Push/SMS/email, background mobile services, arbitrary external-event ingestion or workflows.

## Milestone 11 — SQLite persistence and reconnect/resume

- **Goal:** Restore conversation continuity after disconnect/restart.
- **Scope:** EF Core mappings/migrations, transactional revision writes, profile/summary/history, checkpointing, recovery of unfinished entries, runtime eviction/reconstruction and terminal end behavior.
- **Required interfaces:** IMemoryStore, SessionSnapshot, UserProfile, definition store and session lifecycle services.
- **Tests:** SQLite/in-memory contract parity, transaction rollback/conflict, reopen after simulated crash, old-entry heard offset refresh, deduped uncertain text retry, clean end and failed end save.
- **Acceptance criteria:** A restarted process resumes a paused session with pinned identity/history and conservative heard offsets; no PCM/provider stream replay; known ended session cannot attach; WAL/backup behavior documented.
- **Explicit non-goals:** Vector memory, Redis, generic repository framework, PostgreSQL deployment or multi-user login.

## Milestone 12 — Per-stage latency measurement, provider tuning and demo hardening

- **Goal:** Demonstrate presence with measured behavior and a simple deployable app.
- **Scope:** OpenTelemetry traces/metrics, structured safe logs, developer timeline, separate STT/controller/LLM/segmentation/TTS/transport/playback measurements, hosted/hybrid/on-prem configuration checks, same-origin static build, shutdown/backup checks, accessibility and connection/device errors.
- **Required interfaces:** Existing ports remain stable; ActivitySource/Meter, ILogger, TimeProvider and safe error mapping.
- **Tests:** Full offline backend/frontend/Playwright gates, redaction checks, bounded queue stress, 20-turn measured demo, manual real microphone/headset/speaker pass, restore/restart test.
- **Acceptance criteria:** Both identities demonstrate text, full-duplex voice, meaningful interruption, restrained initiative and reconnect. Latency targets report actual measurements, not promised SLAs. Synthetic demo runs without secrets; production-like build is one logical application plus SQLite file.
- **Explicit non-goals:** Kubernetes, horizontal scaling, services, generic autonomous-agent functionality, elaborate avatars or analytics dashboards.

## Handoff rule

Implementation begins with Milestone 1 in a separate task. Native realtime is not an implementation milestone, prerequisite or runtime branch anywhere in this MVP plan. If a provider cannot meet a capability, implement the specified degraded policy and report its measured trade-off; do not quietly change the architecture. Each implementation milestone should update run/test instructions to actual commands as its artifacts are introduced.
