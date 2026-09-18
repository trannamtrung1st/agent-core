# Development Roadmap

Build the canonical composed pipeline from deterministic conversation mechanics toward continuous full-duplex audio. Native realtime models are future-only and have no MVP milestone. [Implementation Plan](18-implementation-plan.md) is authoritative for each milestone's goal, scope, interfaces, tests, acceptance criteria and non-goals.

| Milestone | Deliverable | Acceptance gate |
| --- | --- | --- |
| 0 | Documentation/contracts finalized | Complete; see [Implementation Plan](18-implementation-plan.md) |
| 1 | Solution skeleton + synthetic text vertical slice | Complete: offline application-service text exchange and health (see [Implementation Plan](18-implementation-plan.md)) |
| 2 | Agent Definition + Agent Runtime + synthetic provider | Complete: two identities through one runtime and pinned JSON definitions (compliance added post-MVP; see [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified)) |
| 3 | OpenAICompatibleLanguageModel + OpenRouter configuration | Complete: offline SSE contracts; optional `openrouter/free` smoke (not demo DefaultModel); skips without opt-in + `OPENROUTER_API_KEY` |
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

History paging, additive semantic lifecycle (`TransitionLifecycle`, RequestComplete, terminal UI), and provider-neutral speech locale (Application + adapters + Speech locale Select) are observed. P1 is **frozen** after the real Chrome 153 `fr-FR` Browser STT/TTS smoke and Voice checklist re-run on HEAD `df0a12c`; fake-browser Playwright is not that probe. Optional hosted multilingual checks are unverified. P2 has not started. Decisions: [Technology Decisions](10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence). Gates: [Implementation Plan](18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech). Do not treat this table row as replacing observed P1 replaceable speech.

## Post-MVP planned until verified

Phases A–H are observed in [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified). Phase I is recorded not-applicable with a future trigger there and in [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified).
