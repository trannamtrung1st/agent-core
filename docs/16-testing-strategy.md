# Testing Strategy

The architecture is acceptable only if conversational mechanics are testable without network or provider credentials. Synthetic is a first-class boot profile, not a stub added after real integrations. [Implementation Plan](18-implementation-plan.md) names milestone gates. [Technology Decisions](10-technology-decisions.md#decision-default-verification-is-offline-live-providers-are-explicit-opt-in) owns the verification policy.

## Default versus live-provider verification

Default verification is fully offline and deterministic: Synthetic/scripted adapters, stub `HttpMessageHandler` or loopback SSE, and hardware-free Playwright. External-provider smoke tests are explicit opt-in only. Implement the planned OpenAI-compatible LLM and OpenAI STT/TTS adapters, but do not add extra hosted mock or third-party inference services solely for testing.

`OPENROUTER_API_KEY` and `OPENAI_API_KEY` are operator secrets for Real-profile runs and opt-in smokes. Their presence in the process environment must not cause default `dotnet test`, frontend unit tests or synthetic Playwright to call hosted APIs or spend credits. Live tests require an explicit opt-in (implementation-defined trait/filter or env such as `AGENTCORE_LIVE_PROVIDER_TESTS`) **and** the relevant key. If opted in and the key is missing, skip cleanly (not fail). If the key is present but opt-in is unset, skip. Do not weaken or skip synthetic HTTP/SSE, speech-contract or conversation-matrix tests because live-provider testing is deferred. Supply keys through environment or `dotnet user-secrets`; do not bind a named file under `local/`.

OpenRouter live smoke, when opted in, may use `openrouter/free`. Do not use that router as the Real/demo DefaultModel. Do not assert catalog quality, exact phrasing or structured-output correctness. OpenAI STT/TTS live integration and manual headset/speaker verification may wait until `OPENAI_API_KEY` is supplied; missing that key must not fail normal build/test. Prioritize verifying the text-conversation path with Synthetic and, optionally, OpenRouter.

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
| Noise/echo | Low activityScore brief activity | Ignore, no LLM/classifier call |
| Missing partial STT | 250 ms activityScore at/above threshold, PartialTranscripts false | Speech-activity interrupt without invented transcript |
| Ambiguous classifier race | Old IInterruptionClassifier decision after new revision/response | Old classification ignored; bounded deterministic fallback; AgentBrain is not invoked |
| Classifier vs brain | Ambiguous mic event vs idle trigger | Classifier path never calls IAgentBrain; initiative path never calls IInterruptionClassifier |
| Proactive timer | Advance TimeProvider through idle threshold | One eligible trigger with current timer generation |
| StaySilent | Brain declines idle intervention | No agent.response.started; cooldown prevents repeat storm |
| Provider midstream failure | Two deltas then Unavailable | Partial marked failed; no retry/replay; output cancelled |
| Reconnect | Detach R1, attach fresh lease | Snapshot restored, no old audio, old connection rejected |
| TTS cancelled | Cancel while pending segment | No next segment started; worker disposed; STT continues |
| Superseded audio | Queue PCM in worklet then stop/R2 | R1 never renders after flush acknowledgement |
| Spoken-until | Generated tail not played; no timing marks; first half of audio duration corresponds to less than half the text | Credits only fully played completed segments; zero text from the partial current segment; must not over-credit via duration proportion |
| Mode continuity | Text turns, then session.mode.set(voice), then back to text | Same sessionId and history; deliveryMode preserved; no second conversation |
| Pending voice start | session.mode.set(voice) while a text response is live | pendingMode=voice; mode stays text until the response ends; UI Starting voice…; no PCM until Mode=voice |
| Pending voice disconnect | Disconnect while PendingMode=Voice | Response interrupted; PendingMode=null; Mode=Text; reconnect ready does not start STT; user must press Voice again |
| Pending voice timeout | Advance TimeProvider past PendingVoiceTimeoutMs | PendingMode cleared; Mode=Text; client releases preflight resources |
| Session capacity | Attach when MaxActiveSessions in-memory runtimes are live | SessionCapacityExceeded, recoverable, retryAfterMs defaults to 5000; durable create still succeeds |
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

Domain.Tests: policy/value invariants and definition immutability. Infrastructure.Tests also validates JSON definition loading. Application.Tests: summary/context eligible text limits, controller tables, session ownership, IDs/time, prompt sections, initiative, the synthetic 20-turn measured demo, redaction, mailbox/audio backpressure and deterministic conversation matrix. Infrastructure.Tests: synthetic contract parity, SQLite transactions/revisions/reopen, HTTP adapter parsing and error mapping, OpenAI transcription session payloads with optional vendor fields omitted by default. Api.Tests: WebApplicationFactory REST examples, validation/statuses, lifecycle, concurrency and wire mappings.

Use a stub HttpMessageHandler or local loopback SSE server for adapter tests: split UTF-8 characters and data lines across reads, CRLF/comments, multiple SSE events per buffer, role-only/usage-only events, finish markers, missing terminator, malformed/oversize JSON, ignored provider reasoning fields, in-stream error payloads, hosted OpenRouter/local-endpoint fixture parity, 401/429/503, RetryAfter, stream-idle deadlines and disposal. Assert no duplicate generation POST after partial output and no vendor DTO leakage. These tests need no live AI service.

WebApplicationFactory covers HTTP and in-process integration; additionally start a loopback Kestrel test host for the real JavaScript SignalR MessagePack/WebSocket/audio contract because an HTTP-only TestServer check does not verify browser wire compatibility. Assert casing, GUID/timestamp strings, Uint8Array payloads, size limits, attachment leases, sequence gaps and protocol-version rejection. SQLite tests use temporary files for WAL/reopen behavior and clean them up; do not substitute EF's nonrelational in-memory provider for SQLite semantics.

## Frontend and end-to-end

Vitest: Zustand reducers, supersession guards, sample-offset accounting, segmentation/resampling math and queue bounds. React Testing Library: user interactions/statuses, keyboard access, safe errors, retained drafts. Playwright: synthetic text, voice fixture, stop/late audio, mute, disconnect/reconnect and history. Use deterministic scenario fixtures and fake media devices; test actual AudioWorklet execution where supported. Assertions observe sample counters and flush acknowledgements, not unreliable “did sound play” timing guesses.

Opt-in real-provider smoke tests require explicit operator credentials, an explicit opt-in flag, and small bounded requests. They are excluded from default local `dotnet test` / npm / Playwright loops. Manual headset/speaker demos measure subjective turn-taking, echo and provider latency and may be deferred without `OPENAI_API_KEY`. Unit gates remain deterministic and do not require real-model phrasing to match.

## Future implementation commands

Once projects exist: `dotnet test` from solution root; `npm ci`, `npm run test -- --run`, `npm run build` from web; `npx playwright test` with the synthetic test host; `scripts/compose-sqlite-volume.sh` for Compose volume survival. Intended repository CI is GitHub Actions (`.github/workflows/synthetic.yml`). Core CI must never require OpenAI/OpenRouter keys, internet inference, microphone, speaker or GPU. Real-provider credentials must not be required to build or pass core tests.

## CI evolution

```text
early milestones: dotnet build/test; frontend install/build/unit tests
after browser realtime exists: synthetic browser integration
after synthetic voice exists: synthetic Playwright voice scenarios
```
