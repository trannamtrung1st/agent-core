# Development Roadmap

Build the canonical composed pipeline from deterministic conversation mechanics toward continuous full-duplex audio. Native realtime models are future-only and have no MVP milestone. [Implementation Plan](18-implementation-plan.md) is authoritative for each milestone's goal, scope, interfaces, tests, acceptance criteria and non-goals.

| Milestone | Deliverable | Acceptance gate |
| --- | --- | --- |
| 0 | Documentation/contracts finalized | Complete; see [Implementation Plan](18-implementation-plan.md) |
| 1 | Solution skeleton + synthetic text vertical slice | Complete: offline application-service text exchange and health (see [Implementation Plan](18-implementation-plan.md)) |
| 2 | Agent Definition + Agent Runtime + synthetic provider | Complete: two identities through one runtime and pinned JSON definitions (compliance added post-MVP; see [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified)) |
| 3 | OpenAICompatibleLanguageModel + OpenRouter configuration | Complete: offline SSE contracts; optional `openrouter/free` smoke (not demo DefaultModel; catalog option only); skips without opt-in + `OPENROUTER_API_KEY` |
| 4 | Interaction Controller with synthetic speech/events | Complete: deterministic backchannel, interruption, stale-result and classifier/brain isolation tests |
| 5 | SignalR realtime protocol + browser connection + minimal Synthetic Compose | Complete: MessagePack browser text slice, leases, identity guards, key-free Compose smoke, GitHub Actions (see [Implementation Plan](18-implementation-plan.md)) |
| 6 | Microphone + AudioWorklet + streaming STT | Complete: synthetic STT + AudioWorklet preflight/PCM gating (see [Implementation Plan](18-implementation-plan.md)) |
| 7 | Speech segmentation + streaming TTS + playback | Complete: synthetic TTS starts before model completion; output worklet acknowledgements; capture stays active (see [Implementation Plan](18-implementation-plan.md)) |
| 8 | Full-duplex voice | Complete: PCM/STT continue during playback; mute is input-only; disconnect releases both paths (see [Implementation Plan](18-implementation-plan.md)) |
| 9 | Semantic barge-in / ducking / supersession / spoken-until | Complete: wait replaces the live response; backchannels continue; conservative spoken-until; worklet flush before R2 (see [Implementation Plan](18-implementation-plan.md)) |
| 10 | Proactive interaction | Complete: examiner help-after-silence and support environment mention; StaySilent cooldown; no detached/repeated nudges (see [Implementation Plan](18-implementation-plan.md)) |
| 11 | SQLite persistence + reconnect/resume | Complete: temp-file SQLite/in-memory parity, crash reopen, checkpoint skip of unchanged completed rows (see [Implementation Plan](18-implementation-plan.md)) |
| 12 | Per-stage latency + provider tuning + demo/container hardening | Complete: measured 20-turn demo, Compose SQLite volume/restart, MVP handoff report (see [Implementation Plan](18-implementation-plan.md)) |

Cancellation/response IDs start in the synthetic slice; Milestone 8 proves full-duplex operation and Milestone 9 completes semantic interruption/audio races. Milestone 10 adds restrained idle and environment initiative. Milestone 11 restores pinned identity from SQLite or the in-memory store after pause/crash without replaying audio. Protocol attach/reconnect starts in memory at Milestone 5, which also introduces the first key-free Compose path; Milestone 11 adds durable recovery. Instrumentation hooks begin early; Milestone 12 completes measured tuning and container/operations hardening. Do not interpret dependency order as permission to defer fundamental invariants.

[Testing Strategy](16-testing-strategy.md) defines offline fixtures and suites. [Demo Scenarios](09-demo-scenarios.md) defines the product demonstration. [Operations](17-observability-and-operations.md) defines latency measurements and future run/deployment instructions.

## Follow-on observed slices

| Slice | Deliverable | Acceptance gate |
| --- | --- | --- |
| P0 | Conversation/UI baseline freeze | Complete: key-free Synthetic gate, Impeccable harden, structured failures, rich envelope regressions ([Implementation Plan](18-implementation-plan.md#p0--conversation-lifecycle-observed)) |
| P1 | Independently selectable STT/TTS, Browser client transports, hosted OpenAI TTS and batch STT | Deterministic coverage green; HOSTED-04 live non-Synthetic smoke **unverified** ([P1 handoff](reports/p1-replaceable-speech-handoff.md)) |

## Follow-on P1 history, lifecycle, and speech locale

History paging, additive semantic lifecycle (`TransitionLifecycle`, RequestComplete, terminal UI), and provider-neutral speech locale (Application + adapters + Speech locale Select) are observed. **P1 freeze:** `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`, 2026-09-19); CI/Synthetic + Compose verification is green on that HEAD. Do not reopen P1. Earlier unedited probe records stay as written: Chrome 153 `fr-FR` Browser STT/TTS smoke `p1-final-fr-smoke-r2` on HEAD `5764010`, and Voice checklist re-run `p1-final-chrome-probe-run.log` on `df0a12c`. Fake-browser Playwright is not that probe. Optional hosted multilingual checks are unverified. P2A, P2B, **P2E**, and **P2C** are observed/frozen (P2B on `e0e8a55`; P2E and P2C per [Implementation Plan](18-implementation-plan.md)). **P2 closed/frozen** on `47d6ff6` (2026-09-21) after mandatory whole-output review and closure repair; CI/Synthetic + Compose green (workflow run `35552740853`). **P3 freeze on `27efe17` was reopened** for a focused email/approval correction; implementation HEAD **`e255916`** passed the key-free gate, then P3 was **reopened narrowly** for the Real GPT-4o mini historical-image handoff (see [P3 closure report](reports/p3-freeze-candidate.md)). Hosted workflow `35627313751` was green on `06198a9`. Do not reopen P2 without a reproducible regression. Roadmap focus: **P4 waits** while P3 is reopened narrowly. Decisions: [Technology Decisions](10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence). Gates: [Implementation Plan](18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech). Do not treat this table row as replacing observed P1 replaceable speech.

## P2B validated model response envelope

Semantic `same`/`custom`/`none` envelope, SessionRuntime cutover, Infrastructure structured/compatibility parsing, public custom-only `speechText`, and live `agent.speech.projection` mode/telemetry are **observed**. **Freeze:** `e0e8a55` (2026-09-20) with CI/Synthetic + Compose green on that HEAD. Do not reopen P2B without a reproducible regression. Gate: [Implementation Plan](18-implementation-plan.md#p2b--validated-model-response-envelope-observed). Decision: [Technology Decisions](10-technology-decisions.md#decision-validated-provider-neutral-assistant-response).

## P2E multimodal image-input capability closure

Truthful sanitized image MIME (`attachment-processors/2`), layered Vision admission/defense, catalog-per-model `vision` authority, and Synthetic composer UX including `scripted-vision` are **observed**. **Freeze:** 2026-09-21 key-free Synthetic + Compose gate on the P2E freeze HEAD (implementation `0eeb27c`–`a502266`). Do not reopen P2E without a reproducible regression. Gate: [Implementation Plan](18-implementation-plan.md#p2e--multimodal-image-input-capability-closure-observed). Optional Real vision probe skipped (credentials unavailable).

## P2D session model selection

Trusted catalog, system default, persisted per-session resolved choice, session-aware resolver, reasoning effort, and Codex-like UI are **observed**. Existing sessions stay pinned when the operator default changes. The shipped Real catalog default is DeepSeek V4.1 Flash; GPT-4o mini 2024-07-18 and OpenRouter Free are additional allowed choices. Gate: [Implementation Plan](18-implementation-plan.md#p2d--session-model-selection-and-inference-controls-observed). Decision: [Technology Decisions](10-technology-decisions.md#decision-session-model-selection-and-inference-controls).

## Post-MVP planned until verified

Phases A–H are observed in [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified). Phase I is recorded not-applicable with a future trigger there and in [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified).
