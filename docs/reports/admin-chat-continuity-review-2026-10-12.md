# Admin / Chat realtime continuity review

Scope: [PR #10](https://github.com/trannamtrung1st/agent-core/pull/10), `fix/admin-chat-realtime-continuity`, integrated with main `53dca6b4`. This is a focused post-MVP correction; no new application milestone is claimed.

## Findings and corrections

- The shared SignalR connection and store now survive internal Chat → Admin navigation. A text Run keeps its attachment, execution and response identity, including output that completes while Chat is unmounted. Returning resumes receipts for committed assistant text and blocks rather than attaching or starting a second Run.
- Admin entry originally stopped local playback while retaining server Voice mode. Server voice completion depends on actual playback acknowledgements, so this could strand output until stall handling. Suspension now sends the existing mode-to-text command when Voice is active or pending. Active voice output follows `modeChange` interruption; pending Voice cancels without stopping active text. Hidden audio is never falsely credited as heard. Media requires an explicit Voice click after return.
- `resumeCapture` originally allowed a preflight completing after navigation to retain prepared microphone resources. A reproduced regression now verifies epoch/surface fencing and release. Hidden surfaces also reject a new Voice request.
- Reconciled native route links from main, idempotent router suspension, and the authoritative frontend/testing documentation.

## Runtime evidence

Playwright MCP used disposable Synthetic SQLite `/tmp/pr10-mcp.db`, API port 5680 and UI port 5683. Through visible controls, provisioned an Examiner instance, sent `[test:durable-stream]`, opened Admin during output and checked a connected hub, ready connection and stopped capture. Execution/response ids were unchanged. After completion in Admin, returned to the same Chat and reloaded: exactly one completed `Hello from synthetic.` entry remained. A second streamed turn returned before completion with the same execution/response ids and completed normally. The console contained only development and SignalR information, with no console errors.

MCP's browser did not have the CLI runner's fake-media flags: Voice preflight reported `getUserMedia timed out`. Voice runtime verification therefore used the existing CLI runner with fake media and real AudioWorklet execution. Physical microphone/speaker quality was not assessed. After editing realtime service code during the dev run, the MCP page was reloaded before repeating the live scenario.

Focused CLI browser gate, in `web/`:

```sh
CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5580 PLAYWRIGHT_WEB_PORT=5583 PLAYWRIGHT_FIXTURE_PORT=5591 PLAYWRIGHT_SQLITE_PATH=/tmp/pr10-after.db pnpm exec playwright test e2e/admin-shell.spec.ts e2e/email-accounts.spec.ts e2e/navigation-links.spec.ts --project=synthetic --output=/tmp/pr10-after-results
```

**10 passed**, including both active text return paths and durable reload, Voice mode exit with response terminalization and a successful subsequent text turn, effective configuration at desktop/narrow width, email allow/reload/cancel at 1440/768/390px and native route links. The original PR CI failure (`38194556247`) was email chooser cancellation at 1440px. All three email scenarios passed both before and after the corrections on the integrated candidate; no unsupported root cause or test weakening was introduced. Exact-candidate hosted CI remains the final gate, recorded by PR checks.

## Local checks

Commands use Node 22.18.0, matching CI, as documented in the [previous review](current-changes-review-2026-10-12.md).

- Vitest: `realtime.race`, `AppRouter`, `ChatApp`, `EmailAccountPicker`: **4 files / 94 tests passed**.
- Vitest: `realtime.protocol`, `realtime.queue`, `realtime.overflow`, `realtime.clientTranscriptBlocked`: **4 files / 41 tests passed**.
- Newly added mode-exit and late-preflight tests failed before the correction and passed afterward. Hidden completed-response render receipts also have direct regression coverage.
- TypeScript project build (`tsc -b web/tsconfig.json`) and Vite production build: passed. Existing dependency pure-annotation and bundle-size warnings remain.
- `git diff --check`: passed. Changed relative documentation references resolve to existing owners, tests and report.

No backend behavior or wire schema was changed. The existing key-free hosted workflow covers all backend suites, both full frontend shards/builds, both browser core shards, acceptance projects and Compose smoke. Live providers and physical-device tests are outside this focused Synthetic review.
