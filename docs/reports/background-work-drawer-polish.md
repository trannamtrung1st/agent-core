# Background work drawer polish

Verified 2026-10-08 in the isolated, key-free Synthetic app on API port 5128 and Vite port 5208. Existing Real processes and working-tree changes were preserved.

The existing drawer now uses up to 640px at widths of 768px and above, and the full viewport below 768px. Its body owns 16px insets. Compact timestamps and aligned Model/Outcome/Run metadata improve scanning; identifiers wrap, title/status rows can wrap, and legacy one-shot ISO summaries show the viewer's local date/time and zone.

Instructions, approval previews and results reuse one `RunTextSection` in the existing BackgroundWorkDrawer renderer, including its Admin Run details consumer. Each section retains its label above a focusable reading region. Only long text scrolls: the region has `max-height: clamp(8rem, 24dvh, 16rem)`, with thin scrollbars, visible keyboard focus and contained overscroll. Short content has its natural height. The drawer body continues to scroll between records and actions.

## Screenshots

| Viewport | Normal content | Long-content boundary |
| --- | --- | --- |
| 1440 × 900 | [Desktop](assets/background-work-polish-1440.png) | [Long instructions/result](assets/background-work-polish-1440-long.png) |
| 768 × 900 | [Tablet](assets/background-work-polish-768.png) | [Long instructions/result](assets/background-work-polish-768-long.png) |
| 390 × 844 | [Mobile](assets/background-work-polish-390.png) | [Long instructions/result](assets/background-work-polish-390-long.png) |

Browser assertions verify the drawer's final width/position, no document overflow, independently scrolling instructions/results, and naturally sized short content. Screenshot capture waits for the normal-content drawer resize transition.

## Verification

- Playwright MCP: send a Synthetic turn, observe the assistant response, open Background work, verify the real empty state and 640px drawer, then close. Console inspection returned zero errors/warnings; affected HTTP requests succeeded.
- `pnpm exec playwright test --config /private/tmp/agent-design-system/playwright.config.mjs background-work-layout.spec.ts`: passed. An explicitly provisioned Examiner fixture exercises result 503 → Retry, long-region focus/End scrolling without moving the outer body, responsive geometry, revision refresh to short text, Mark as read without losing the result, and Escape dismissal. Controlled HTTP fixtures contain no provider calls. No JavaScript page errors or unexpected failed requests occurred.
- The same isolated configuration ran `operational-drawer-paging.spec.ts`: both existing scenarios passed, covering list paging, failure/retry, read-state persistence, and Admin pages across resize/refresh.
- `pnpm run test --run src/features/chat/BackgroundWorkDrawer.test.tsx src/features/chat/runPresentation.test.ts`: 28 tests passed, including approval/cancellation, selected run/result recovery, and trigger-format fallbacks.
- `pnpm run build`: passed. Existing bundle-size/SignalR annotation warnings remain; unit output includes the runtime's localStorage warning.
- `git diff --check`: passed. The canonical frontend specification and DESIGN.md were updated with the shared layout/scrolling rules.

The long-content browser journey covers Chat. Admin Run details uses the same renderer and has unit coverage; a separate browser capture of that Admin detail consumer was not taken. This scoped polish does not claim whole-application or milestone acceptance. The design hook reported an older Impeccable sidecar; no tooling drift repair was performed.
