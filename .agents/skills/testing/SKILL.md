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

Run affected suites and every required gate for a claimed milestone. A small fix needs relevant verification, not every future suite. Plans inspect coverage and specify checks; behavior reviews also exercise existing scenarios when runnable, without creating implementation artifacts unless fixes are requested.

## Runtime verification

Apply this workflow to frontend/backend implementation, fixes and behavior reviews. Plans specify the checks to run later; docs-only work uses docs-consistency. [Testing Strategy](../../../docs/16-testing-strategy.md#runtime-evidence-for-development-and-review) owns the evidence policy. Both Codex and Cursor use this shared workflow.

1. Select a representative affected use case and define its expected observable outcome before execution. Include a relevant failure/recovery or boundary case when behavior changes (for example, rejected input, reconnect, cancellation or persistence reopen). Inspect existing fixtures and extend them for uncovered behavior changes; do not add tests that merely mirror implementation or create scaffolding for a review.
2. Inspect actual package/test-host configuration and [local run commands](../../../docs/17-observability-and-operations.md#running-after-implementation). Use Synthetic with no provider calls. Prefer existing integration fixtures and native .NET/Vite; Docker is needed only for affected Docker behavior. Check the profile before reusing a running host, use disposable test data, and do not reset user sessions/databases or terminate processes you did not start.
3. Execute the frontend/backend steps below. A configured tool is not proof it is available: discover callable tools and attempt the relevant check. Resolve routine local setup issues within scope. If blocked by missing tooling, dependencies, browser binaries, permissions or services, attempt the closest practical fallback; do not silently settle for static review.
4. Compare actual outcomes with the expectations. For implementation/fixes, address failures caused by the change and rerun affected checks. For reviews, record reproduction evidence and actionable findings without editing code. Stop only processes you started and clean up disposable resources.
5. Report commands/tools, scenario and expected versus observed result, pass/fail/blocked status, and useful evidence paths when captured. Separate setup blockers, product failures and unrelated existing failures. Name the fallback used and residual gaps; never mark an unexecuted scenario as passed or claim milestone acceptance with missing required gates.

### Frontend: interact with the running app

- Prefer Playwright MCP configured in [Codex](../../../.codex/config.toml) and [Cursor](../../../.cursor/mcp.json). Start or safely reuse the Synthetic backend and Vite app, navigate to the actual local app, and exercise the changed journey using browser interactions. MCP does not start the app merely because it is configured.
- Both editor configurations use isolated browser profiles and Playwright's default output-directory resolution. Avoid relative `--output-dir`/`--user-data-dir` overrides: a client launched from `/` can resolve them outside the repository and fail before navigation. After changing MCP configuration, reconnect/restart the server before verifying it; an existing connection may retain old arguments. If reload is unavailable, verify a fresh MCP process with the saved arguments and identify that limitation. Browser storage is discarded when the isolated context closes; retain evidence using the paths actually returned by the tool.
- Before CLI E2E, check both configured ports and the backend's `/health` profile. The current runner reuses servers outside CI, and its text scenario expects an empty session catalog. Reuse only a known disposable Synthetic instance with suitable state. With free ports, `CI=1 pnpm exec playwright test` prevents reuse. If another app occupies the ports (especially a Real-profile backend), leave it running and use a temporary test configuration with separate ports, baseURL and matching Vite HTTP/WebSocket proxy targets, or report the conflict. Never run Synthetic scenarios against an unknown or Real host, and do not clear a user's catalog to satisfy a test.
- Establish readiness, perform actions through visible controls, and verify the resulting state/content. For a conversation change, for example: start a conversation, wait for Ready, send text, and verify the completed Synthetic response. Exercise the affected attachment, reconnect, voice or other flow instead when relevant. Use current snapshots and accessible roles/labels to locate controls rather than guessing selectors.
- Inspect browser console errors and failed network requests related to the flow. Capture a screenshot/snapshot or trace when useful to explain a result; navigation success and visual appearance alone do not establish functionality. Verify interaction outcomes, not only element presence.
- Run applicable Vitest/build checks and existing Playwright regression tests as well. MCP exploration complements repeatable E2E coverage; it does not replace required gates. The [Playwright runner configuration](../../../web/playwright.config.ts) starts backend/Vite test servers and supplies fake-media flags; those settings do not automatically apply to MCP. Use the runner's fake-device AudioWorklet scenarios for capture/playback checks. Do not infer audible quality or actual microphone behavior from DOM or simulated audio evidence.
- If Playwright MCP is unavailable or cannot launch, run the affected Playwright E2E test via CLI or use another available browser automation tool. If no browser execution is possible, run applicable unit/build checks and explicitly report the unverified journey and exact blocker. Do not claim MCP verification when only the CLI ran.

### Backend: execute an integrated use case

- Prefer the existing fixture that crosses the boundaries affected by the change: WebApplicationFactory for HTTP lifecycle/validation; loopback Kestrel with the real JavaScript SignalR/MessagePack client for wire behavior; application integration with Synthetic adapters for runtime/controller behavior; temporary SQLite for durable writes and reopen/recovery. Keep focused unit tests for local invariants alongside these checks.
- Assert the workflow's outputs and state effects, not only a successful status. Examples: create/attach a session, submit text through the hub and observe ordered deltas plus terminal history; upload/bind an attachment and verify its session association; save/reopen a session and verify retained history; cancel a response and verify no stale output becomes visible. Choose the case relevant to the change rather than running all examples.
- An existing integration test is sufficient when it exercises the affected path; a separate manual host run is optional. When coverage is missing, add or extend a focused regression for behavior changes and run it. A local host smoke can provide additional evidence using existing HTTP/hub contracts, without introducing public test/debug endpoints. `/health` proves readiness only; follow it with the actual use case.
- If an integration fixture cannot run, try a local Synthetic host or a narrower runnable integration layer. Report what that fallback proves and which boundary remains unverified. Missing hosted keys do not block Synthetic verification and do not authorize live-provider calls.

## Deterministic mechanics

- Use ScriptedLanguageModel release gates, synthetic speech scripts/PCM/timing marks, FakeInterruptionClassifier, DeterministicIdGenerator and FakeTimeProvider. Advance logical time and await fixture publication acknowledgements plus mailbox-drained/observer barriers.
- A drained mailbox does not prove future callbacks arrived. Avoid Thread.Sleep, random jitter and wall-clock guesses. Keep hooks internal/test-visible, never public arbitrary-event endpoints.
- Select affected canonical cases: ordered text; superseded R1/late non-cooperative chunks after R2; backchannel/interrupt/noise; missing partial STT; stale classifier/timer; StaySilent/cooldown; midstream failure; reconnect; final-before-ended/duplicate final; segmentation; full-duplex input during output; backpressure; crash/revision conflict.
- Cancellation changes exercise relevant boundaries before first token, while TTS prepares, with queued PCM, after model completion before playback completion, and during terminal save. Check late playback cannot change superseded history/context and unplayed text is excluded from heard context.
- Adapter fixtures cover split UTF-8/SSE lines, CRLF/comments, multiple events/read, role/usage-only events, missing finish, malformed/oversized payloads, in-stream errors, 401/429/503, idle deadlines and disposal. Assert no repeated generation POST after partial output.
- Synthetic full-stack behavior requires no provider service, credentials, internet, microphone, speaker or GPU. Assert no outbound provider HTTP. Controlled browser samples and real playback DTOs provide hardware-free simulation; separate fake-device tests exercise real worklets and flush acknowledgements.
- Live-provider checks are bounded explicit opt-in. `OPENROUTER_API_KEY` / `OPENAI_API_KEY` in the environment must not spend credits during default `dotnet test`. Skip cleanly when opted in but a key is missing. Do not weaken synthetic contract tests because live OpenAI speech is deferred. Do not add extra external test-only inference services.
- Manual headset/speaker checks assess echo, audible latency and device behavior; DOM tests do not prove these. Those checks may wait for `OPENAI_API_KEY`.
- Default verification is local plus the existing [GitHub Actions workflow](../../../.github/workflows/synthetic.yml). Core CI stays key-free.

## Commands and results

Inspect actual solution/package/test-host configuration before executing. Current commands (dependency installation may need network; Synthetic execution makes no provider calls):

| Check | Working directory | Command |
| --- | --- | --- |
| Backend suites | Repository root | `dotnet test AgentCore.sln --nologo` |
| HTTP/session lifecycle example | Repository root | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~HealthAndSessionLifecycleTests --nologo` |
| Frontend dependencies, when needed | web/ | `pnpm install --frozen-lockfile` |
| Frontend unit tests | web/ | `pnpm run test --run` |
| Frontend build | web/ | `pnpm run build` |
| Synthetic browser suite | web/ | `pnpm exec playwright test` |
| Text conversation example | web/ | `pnpm exec playwright test e2e/text-conversation.spec.ts` |

Choose an existing test/filter matching the change; the examples are not universal acceptance gates. Inspect [CI](../../../.github/workflows/synthetic.yml) for prerequisites such as the real JavaScript client's dependencies and Chromium installation. Follow [Operations](../../../docs/17-observability-and-operations.md#running-after-implementation) to start native hosts for MCP. Do not create projects to run a docs-only check.

Report exact commands, results and omitted/blocked gates with reasons. Separate setup failures from test failures. An unrun check, future command or manual inspection is not a passing test.
