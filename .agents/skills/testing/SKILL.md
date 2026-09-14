---
name: testing
description: "Select and run Agent Core milestone checks using deterministic synthetic scenarios, backend/frontend suites, wire tests and persistence recovery."
---

# Testing

[Testing Strategy](../../../docs/16-testing-strategy.md) owns the full fixture/scenario matrix; [Implementation Plan](../../../docs/18-implementation-plan.md) owns milestone gates. Read affected scenarios and acceptance criteria before selecting checks.

## Select checks

| Changed behavior | Verification |
| --- | --- |
| Documentation/instructions | [docs-consistency](../docs-consistency/SKILL.md); no application scaffolding |
| Domain/policy/definitions | xUnit invariants, immutability, definition validation |
| Runtime/controller/cancellation | Application xUnit deterministic matrix and affected interruption boundaries |
| HTTP/SSE/speech adapters | Infrastructure offline parsing, capabilities, failures, cancellation, synthetic parity |
| HTTP endpoints/lifecycle | WebApplicationFactory statuses, validation, lifecycle, concurrency |
| SignalR/MessagePack/audio wire | Loopback Kestrel + real JavaScript client fixtures; TestServer alone is insufficient |
| SQLite/store | Temporary SQLite transactions/revisions/dedupe/rollback/reopen and in-memory parity |
| Browser state/UI/audio math | Vitest + React Testing Library behavior and frontend build |
| Browser transport/conversation/audio | Synthetic Playwright and separate fake-device AudioWorklet checks |
| Packaging/operations | Applicable redaction, shutdown, restore and Synthetic Compose/volume survival |

Run affected suites and every required gate for a claimed milestone. A small fix needs relevant verification, not every future suite. Plans/reviews may inspect coverage and report gaps without creating implementation artifacts.

## Deterministic mechanics

- Use ScriptedLanguageModel release gates, synthetic speech scripts/PCM/timing marks, FakeInterruptionClassifier, DeterministicIdGenerator and FakeTimeProvider. Advance logical time and await fixture publication acknowledgements plus mailbox-drained/observer barriers.
- A drained mailbox does not prove future callbacks arrived. Avoid Thread.Sleep, random jitter and wall-clock guesses. Keep hooks internal/test-visible, never public arbitrary-event endpoints.
- Select affected canonical cases: ordered text; superseded R1/late non-cooperative chunks after R2; backchannel/interrupt/noise; missing partial STT; stale classifier/timer; StaySilent/cooldown; midstream failure; reconnect; final-before-ended/duplicate final; segmentation; full-duplex input during output; backpressure; crash/revision conflict.
- Cancellation changes exercise relevant boundaries before first token, while TTS prepares, with queued PCM, after model completion before playback completion, and during terminal save. Check late playback cannot change superseded history/context and unplayed text is excluded from heard context.
- Adapter fixtures cover split UTF-8/SSE lines, CRLF/comments, multiple events/read, role/usage-only events, missing finish, malformed/oversized payloads, in-stream errors, 401/429/503, idle deadlines and disposal. Assert no repeated generation POST after partial output.
- Synthetic full-stack behavior requires no provider service, credentials, internet, microphone, speaker or GPU. Assert no outbound provider HTTP. Controlled browser samples and real playback DTOs provide hardware-free simulation; separate fake-device tests exercise real worklets and flush acknowledgements.
- Live-provider checks are bounded explicit opt-in. `OPENROUTER_API_KEY` / `OPENAI_API_KEY` in the environment must not spend credits during default `dotnet test`. Skip cleanly when opted in but a key is missing. Do not weaken synthetic contract tests because live OpenAI speech is deferred. Do not add extra external test-only inference services.
- Manual headset/speaker checks assess echo, audible latency and device behavior; DOM tests do not prove these. Those checks may wait for `OPENAI_API_KEY`.
- Default verification is local plus, once scripts exist, GitHub Actions. Core CI stays key-free. Do not create workflow files until projects/scripts exist.

## Commands and results

Inspect actual solution/package/test-host configuration before executing. Planned commands: `dotnet test` from solution root; `npm ci`, `npm run test -- --run`, `npm run build` from web/; `npx playwright test` with the synthetic host configured. They are runnable only after their artifacts/scripts exist. Do not create projects to run a docs-only check.

Report exact commands, results and omitted/blocked gates with reasons. Separate setup failures from test failures. An unrun check, future command or manual inspection is not a passing test.
