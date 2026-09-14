# Testing Strategy

The architecture is acceptable only if conversational mechanics are testable without network or provider credentials. Synthetic is a first-class boot profile, not a stub added after real integrations. [Implementation Plan](18-implementation-plan.md) names milestone gates.

## Synthetic fixtures

| Implementation | Contract and behavior |
| --- | --- |
| ScriptedLanguageModel | ILanguageModel; exact text chunks, explicit release gates, controlled terminal failures, optional intentional late results after cancellation |
| SyntheticSpeechRecognizer | ISpeechRecognizer/session; accepts PCM but transcripts come from a scenario script, not real recognition; supports configurable capability flags |
| SyntheticSpeechSynthesizer | ISpeechSynthesizer; deterministic PCM tone/silence, sample counts and timing marks; finite duration and controlled cancellation |
| FakeInterruptionClassifier | IInterruptionClassifier; scripted decision/delay/failure with candidate identity |
| DeterministicIdGenerator | IIdGenerator; seeded exact UUID sequence for event/response and session IDs |
| FakeTimeProvider | Standard TimeProvider via Microsoft.Extensions.TimeProvider.Testing; explicit timer advancement |
| InMemoryMemoryStore | IMemoryStore; same revisions, dedupe and atomic semantics as SQLite |
| In-memory definition fixture | IAgentDefinitionStore; two validated definitions and invalid-version cases |

Tests advance logical time and release TaskCompletionSource gates, then await an explicit mailbox-drained/test-observer barrier. Do not use Thread.Sleep, random jitter or wall-clock timing assertions for core correctness. A barrier must wait for previously admitted messages, not assume all future worker callbacks have arrived; fixtures expose their publication acknowledgements. Keep production hooks small and internal/test-visible, not public debug endpoints.

Synthetic application profile includes a fixed default script (greeting, longer explanation, short response) and generated tone audio. For precise E2E scenarios, Api.Tests/Playwright host substitutes a scenario driver through DI selected at test-host startup. No arbitrary-event endpoint is exposed in normal deployment. Frontend development can trigger VAD and observe fixed scripted speech; it must label synthetic mode in the developer UI, not pretend to transcribe arbitrary content. Tests may script the source driver directly through their host harness. Real adapters are not even resolved in synthetic mode; assert no outbound HTTP was attempted.

## Synthetic full-stack mode

Synthetic Mode exercises React → SignalR → Session Runtime with separate audio ingress → SyntheticSpeechRecognizer → Interaction Controller → Agent Runtime / ScriptedLanguageModel → SpeechSegmenter → SyntheticSpeechSynthesizer → React playback simulation. Every production capability boundary is represented, including supersession and progress; no native realtime model is involved.

The hard acceptance goal is no OpenRouter, OpenAI, internet, microphone, speaker or GPU. Use scripted audio/boundary/transcript events and controlled TimeProvider delays for partial/final STT; deterministic PCM duration/timing marks and cancellation for TTS; FakeInterruptionClassifier and DeterministicIdGenerator for races. Browser simulation advances consumed samples and emits the real playback control DTOs without audio hardware. The Synthetic browser service drives silent PCM/boundaries and simulated playback using the same predefined scenario schedule; the matching in-process STT fixture emits transcript content when those boundaries arrive. Tests control both schedules explicitly. The synthetic driver is an in-process/test-host facility, not a production arbitrary-event endpoint.

Use this mode for CI, frontend development, deterministic conversation scenarios and interruption tests. Keep a separate integration suite with fake browser media devices to verify actual AudioWorklet capture/playback; hardware-free behavioral coverage and real worklet transport coverage are complementary. Synthetic requires zero API keys and rejects outbound provider HTTP by test assertion.

## Deterministic scenario matrix

| Scenario | Stimulus | Required assertion |
| --- | --- | --- |
| Normal text | Release three model chunks then completion | One responseId, ordered text, one terminal durable entry |
| R1 superseded by R2 | Send second user text while R1 blocked | Mark R1 first, cancel token, start R2 only from new turn |
| Late R1 chunks | Release non-cooperative R1 text/audio/terminal after R2 | No client output or history extension for R1 |
| Backchannel | Speech start + partial/final “mhm” <=700 ms | Continue, gain restored, no new user turn during output |
| Explicit interrupt | Partial “wait” while R1 plays | Stop emitted, R1 superseded, STT remains alive |
| Noise/echo | Low confidence brief activity | Ignore, no LLM/classifier call |
| Missing partial STT | 250 ms confident speech, capabilities false | Speech-activity interrupt without invented transcript |
| Ambiguous classifier race | Old decision after new revision/response | Old decision ignored; bounded deterministic fallback |
| Proactive timer | Advance TimeProvider through idle threshold | One eligible trigger with current timer generation |
| StaySilent | Brain declines idle intervention | No response.started; cooldown prevents repeat storm |
| Provider midstream failure | Two deltas then Unavailable | Partial marked failed; no retry/replay; output cancelled |
| Reconnect | Detach R1, attach fresh lease | Snapshot restored, no old audio, old connection rejected |
| TTS cancelled | Cancel while pending segment | No next segment started; worker disposed; STT continues |
| Superseded audio | Queue PCM in worklet then stop/R2 | R1 never renders after flush acknowledgement |
| Spoken-until | Generated tail not played; partial progress | Future context includes only conservative heard prefix |
| Late playback after supersession | Deliver R1 started/progress/completed/stopped after R2 | No R1 state/history/context change; only diagnostic stop timing may be recorded |
| Speech segmentation | Split sentence across deltas; advance deadline; hit hard cap | Natural units, exact offsets, bounded buffer, no token-per-TTS jobs |
| Segment cancellation | Supersede R1 before timer/job release | No new R1 Speech Segment or TTS job |
| Full-duplex composed path | Keep feeding scripted PCM while TTS plays | STT events continue independently of model/TTS work |
| Final ordering | Final before Ended / duplicate final | Exactly one user history entry and response |
| Backpressure | Saturate audio or mailbox | Bounded memory, explicit error, control stop remains usable |
| Crash recovery | Streaming snapshot loaded after restart | Paused/Interrupted, no new output without user/eligible trigger |
| Persistence conflict | Stale expected revision | Conflict, no silent overwrite or two active runtimes |

Include interruption at each boundary: before first token, while TTS prepares, while PCM queued, after model complete but before playback complete, during terminal save. Also test empty transcripts, expired utterances, final marker with zero bytes, duplicate sample frames and response receipt offsets beyond emitted text.

## Backend suites

Domain.Tests: policy/value invariants and definition immutability. Infrastructure.Tests also validates JSON definition loading. Application.Tests: summary/context eligible text limits, controller tables, session ownership, IDs/time, prompt sections, initiative and deterministic conversation matrix. Infrastructure.Tests: synthetic contract parity, SQLite transactions/revisions/reopen, HTTP adapter parsing and error mapping. Api.Tests: WebApplicationFactory REST examples, validation/statuses, lifecycle, concurrency and wire mappings.

Use a stub HttpMessageHandler or local loopback SSE server for adapter tests: split UTF-8 characters and data lines across reads, CRLF/comments, multiple SSE events per buffer, role-only/usage-only events, finish markers, missing terminator, malformed/oversize JSON, ignored provider reasoning fields, in-stream error payloads, hosted OpenRouter/local-endpoint fixture parity, 401/429/503, RetryAfter, stream-idle deadlines and disposal. Assert no duplicate generation POST after partial output and no vendor DTO leakage. These tests need no live AI service.

WebApplicationFactory covers HTTP and in-process integration; additionally start a loopback Kestrel test host for the real JavaScript SignalR MessagePack/WebSocket/audio contract because an HTTP-only TestServer check does not verify browser wire compatibility. Assert casing, GUID/timestamp strings, Uint8Array payloads, size limits, attachment leases, sequence gaps and protocol-version rejection. SQLite tests use temporary files for WAL/reopen behavior and clean them up; do not substitute EF's nonrelational in-memory provider for SQLite semantics.

## Frontend and end-to-end

Vitest: Zustand reducers, supersession guards, sample-offset accounting, segmentation/resampling math and queue bounds. React Testing Library: user interactions/statuses, keyboard access, safe errors, retained drafts. Playwright: synthetic text, voice fixture, stop/late audio, mute, disconnect/reconnect and history. Use deterministic scenario fixtures and fake media devices; test actual AudioWorklet execution where supported. Assertions observe sample counters and flush acknowledgements, not unreliable “did sound play” timing guesses.

Opt-in real-provider smoke tests require explicit operator credentials and small bounded requests; excluded from default CI. Manual headset/speaker demos measure subjective turn-taking, echo and provider latency. Unit gates remain deterministic and do not require real-model phrasing to match.

## Future implementation commands

Once projects exist: `dotnet test` from solution root; `npm ci`, `npm run test -- --run`, `npm run build` from web; `npx playwright test` with the synthetic test host. The implementation must define these scripts and host startup in its test config; they are not runnable in this docs-only repository yet. CI gates are offline backend/frontend checks plus synthetic Playwright. Real-provider credentials must not be required to build or pass core tests.
