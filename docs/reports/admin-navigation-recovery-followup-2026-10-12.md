# Admin navigation and recovery follow-up

Baseline: main `a6212111`, merged PR #10. This focused correction follows the [continuity review](admin-chat-continuity-review-2026-10-12.md), preserving its text Run, attachment and conservative render/heard boundaries. No new milestone, backend API, provider or architecture is introduced.

## Reproduced findings

1. Playwright MCP reproduced Admin **Chat** opening `/` with one old assistant reply and an active hub. Bootstrap now treats `/` as New Chat, disables the old composer while detaching, and clears the prior local Session before metadata initialization. `/c/{id}` keeps the existing continuity path. Accepted durable work is retained and can be reopened.
2. A real Synthetic browser fault injection rejected the Admin mode-to-text command. Detaching alone was insufficient: an immediate return could reattach the Voice response with media disarmed and leave Stop visible beyond the test timeout. The client now retains this Session's shutdown intent, retries mode-to-text on reattach, and releases the intent only after acceptance. A repeated rejection keeps `/c/{id}`, a recoverable error and Retry with media off. Late cancellation results are fenced by Session, attachment and media epoch.
3. PR CI [38198942227](https://github.com/trannamtrung1st/agent-core/actions/runs/38198942227) passed all seven jobs, and PR #10 merged. The later main run [38200182781](https://github.com/trannamtrung1st/agent-core/actions/runs/38200182781) failed one native-link test: its initial middle click opened no tab and exhausted the entire 90-second test. The test now bounds popup waiting to five seconds with one repeat of the actual native gesture. Each attempt requires the source route to remain unchanged, and the opened tab must still restore the exact href and UI. No scripted `window.open`, suite-wide retry or weakened destination assertion is used. The artifact did not establish a production root cause.

## Runtime verification

- MCP used disposable Synthetic SQLite `/tmp/admin-followup-mcp.db`, API 5680 and Vite 5683. During `[test:durable-stream]`, **Return to last chat** retained the exact execution/response ids; reopening Admin and choosing **Chat** produced `/`, zero assistant entries and no active hub. The pre-fix browser showed one old reply and an active hub at `/`. The console contained only development and SignalR information.
- The CLI browser gate used disposable SQLite and fake-media AudioWorklet execution. It exercised active text return before/after completion and durable reload; both Admin destinations while a Run was active; a real WebSocket close in Admin with automatic recovery of the same text Run; normal Voice shutdown; rejected shutdown, detach, same-Session mode retry and a successful next text turn; email chooser at three widths; and modifier/middle native links.
- The combined focused gate passed **13 scenarios**. Voice recovery and both native-link widths then passed **9 repeated executions** (three each). The final failure-boundary check passed **2 scenarios**: one rejection with automatic mode retry, and two successive rejections followed by same-route Retry and a successful Text turn.

Representative browser command, from `web/` (each run uses a fresh SQLite file):

```sh
CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5580 PLAYWRIGHT_WEB_PORT=5583 PLAYWRIGHT_FIXTURE_PORT=5591 PLAYWRIGHT_SQLITE_PATH=/tmp/admin-followup-complete.db pnpm exec playwright test e2e/admin-shell.spec.ts e2e/navigation-links.spec.ts e2e/email-accounts.spec.ts --project=synthetic --output=/tmp/admin-followup-complete-results
```

Fault injection modifies the loaded SignalR client's invocation only within the disposable browser page, rejecting one or two mode-to-text calls. Recovery still crosses the real Synthetic backend, attachment and response lifecycle. WebSocket-close coverage uses Playwright's socket proxy and an actual close frame; it does not call the application's deliberate disconnect helper.

## Checks and scope

- Node 22 Vitest: **8 files / 142 tests passed** across realtime race/protocol/queue/overflow/client-transcript blocking, Session route navigation, AppRouter and ChatApp. Final realtime race recheck: **62 passed**.
- TypeScript project build and Vite production build passed. Existing dependency annotation and bundle-size warnings remain.
- Changed documentation links and `git diff --check` are checked before commit. Frontend and testing specifications own the new route/recovery behavior.
- Full exact-commit main CI is the final gate; its result is recorded on the pushed commit's GitHub Actions checks. The earlier main failure is preserved above rather than reported as a successful check.
- No backend implementation changed. Default Synthetic verification uses no hosted keys. Physical microphone/speaker quality and live-provider behavior were not assessed. P10/P11 remain unopened.
