# Drive approval review — 2026-10-14

Reviewed the compact approval presentation on `a04e6a76` against the frontend specification and existing server preparation/argument validation. Two defects were corrected:

- The client accepted empty IDs/versions, invalid ID characters, control characters in names/versions, duplicate targets and multiple targets for single-file operations. Nine regression cases reproduced the gap before the fix, including a duplicate React key warning. Invalid projections now disable confirmation and retain fresh-details guidance and rejection. Valid single targets and all twenty distinct targets at the 160/255/64-character metadata bounds remain supported. These checks never grant server authority.
- A native version Tag retained `nowrap`; a valid 64-character version made the 320px review region scroll to 552px despite a 256px available width. The Tag now wraps within its row. Desktop, tablet and narrow review regions retain their exact available width.

The Drive browser regression now waits for native dialog focus before Escape, avoiding a reproduced race between initial visibility and entrance focus transfer. It also checks the long-version layout using temporary rendered text, restored before deciding the unchanged exact request.

## Runtime and local checks

- Playwright MCP used the task-owned disposable Synthetic host at API 5086 / web 5186, with its profile verified through `/health`. A real Synthetic single-file approval displayed the complete trusted target and remained enabled. Checks at 1440×900, 768×360, 390×400 and 320×400 showed reachable footer controls and no horizontal overflow.
- A client projection fixture with an empty version disabled confirmation; Reject dismissed the actual pending request without changing the remaining file count. A supported 64-character version fixture wrapped with review widths/scroll widths of 472/472, 472/472, 326/326 and 256/256. Both projection fixtures were rejected, never approved.
- A fresh request with actual Synthetic metadata was approved using Enter, returned `Succeeded`, moved only the remaining listed fixture to Trash, changed its available count from one to zero and restored focus to Message. No failed requests were recorded in that flow; browser console errors were zero.
- `PATH=/Users/trungtran/.nvm/versions/node/v22.18.0/bin:$PATH NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/chat/ApprovalModal.test.tsx src/services/realtime.approval.test.ts --maxWorkers=1`: **28 passed** (22 component, six decision recovery).
- `pnpm build`: **passed**, including in the isolated frontend described below; existing SignalR annotation and bundle-size warnings remain.
- `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-approval-review/e2e/synthetic.db pnpm exec playwright test --config=/tmp/agent-core-approval-review/review.config.ts e2e/drive-office.spec.ts e2e/approval-flow.spec.ts --project=synthetic --output=/tmp/agent-core-approval-review/e2e/isolated-results`: **seven passed**. These cover Drive connection, previews, recovery, exact batch rejection/confirmation and shared approval refresh/disconnect/keyboard behavior.
- Changed Markdown targets/fences and the staged patch whitespace checks passed. The canonical frontend specification now states target validation and full version wrapping.

## Isolation and limits

Concurrent backend and preview work was present and changed during review. The default browser runner's fresh API build failed at startup. Reusing the already-verified Synthetic host then exposed a preview UI dependency on `/api/v2/file-preview/options`, absent from that running API, before the Drive scenario reached its approval checks. Those unrelated source edits were preserved.

Final browser, component and build verification used `/tmp/agent-core-approval-review/isolated/web`: the committed `a04e6a76` frontend plus only the four affected approval/CSS/test files, with a temporary configuration reusing the known disposable Synthetic host. After adding native focus readiness, all seven browser scenarios passed. This verifies the approval changes against the committed frontend; it does not verify the concurrently edited backend/preview features. A broader documentation scan also found four temporary links to the not-yet-created concurrent download report; the changed approval documentation passed its own link checks.

Hosted CI was not awaited. Real Google effects, assistive-device behavior and broader milestone acceptance remain outside this focused local review.
