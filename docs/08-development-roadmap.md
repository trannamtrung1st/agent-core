# Development Roadmap

Build the canonical composed pipeline from deterministic conversation mechanics toward continuous full-duplex audio. Native realtime models are future-only and have no MVP milestone. [Implementation Plan](18-implementation-plan.md) is authoritative for each milestone's goal, scope, interfaces, tests, acceptance criteria and non-goals.

| Milestone | Deliverable | Acceptance gate |
| --- | --- | --- |
| 0 | Documentation/contracts finalized | Coherent specification; Markdown only |
| 1 | Solution skeleton + synthetic text vertical slice | Offline application-service text exchange and health |
| 2 | Agent Definition + Agent Runtime + synthetic provider | Two identities through one runtime and pinned JSON definitions |
| 3 | OpenAICompatibleLanguageModel + OpenRouter configuration | Streaming text with configurable hosted model; no runtime gateway dependency |
| 4 | Interaction Controller with synthetic speech/events | Deterministic backchannel, interruption and stale-result tests |
| 5 | SignalR realtime protocol + browser connection | MessagePack browser text slice, leases and identity guards |
| 6 | Microphone + AudioWorklet + streaming STT | Continuous PCM and partial/final transcript pipeline; explicit capability fallback |
| 7 | Speech segmentation + streaming TTS + playback | Natural segments and first audio before full response completion |
| 8 | Full-duplex voice | Microphone/STT remain active while agent audio plays |
| 9 | Semantic barge-in / ducking / supersession / spoken-until | Cancel segments/TTS/LLM; reject late output/playback feedback |
| 10 | Proactive interaction | Useful trigger or StaySilent with cooldown |
| 11 | SQLite persistence + reconnect/resume | Recover history/identity without replaying old audio |
| 12 | Per-stage latency + provider tuning + demo hardening | Measured composed pipeline, offline tests and one-app deployment |

Cancellation/response IDs start in the synthetic slice; Milestone 8 proves full-duplex operation and Milestone 9 completes semantic interruption/audio races. Protocol attach/reconnect starts in memory at Milestone 5; Milestone 11 adds durable recovery. Instrumentation hooks begin early; Milestone 12 completes the measured tuning pass. Do not interpret dependency order as permission to defer fundamental invariants.

[Testing Strategy](16-testing-strategy.md) defines offline fixtures and suites. [Demo Scenarios](09-demo-scenarios.md) defines the product demonstration. [Operations](17-observability-and-operations.md) defines latency measurements and future run/deployment instructions.
