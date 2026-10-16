# Automation approval modal polish — 2026-10-16

The owner requested a readable, consistent approval experience after the exact-action cutover exposed raw JSON and identifiers before the proposed task. This change refines the existing Ant Design approval modal in Operate mode. It changes presentation only; the owner decision, payload, action fingerprint and backend authorization/recovery remain unchanged.

## Result

`AutomationApprovalPreview` is a focused product composition inside the shared `ApprovalModal`. It presents full instructions, name when available, resolved schedule/timezone, execution destination, report-back policy, proposed state and supplied model/capability/settings fields. Weekly bounds and Schedule/Event collections remain readable. Update previews retain previous names, instructions and state; Event source/filter changes show the proposed values. Clearing an Event filter is “No filter”; explicit model reset is “Instance default”. Unknown supplied settings remain visible.

Each operation names its decision: Create automation, Save changes, Enable automation, Disable automation, Delete automation or Run now. Destructive deletion retains Ant Design danger styling and the existing future-run/history explanation. Other normal Automation decisions use the standard primary button.

The complete original `Exact action` string, identifiers and original failure/policy reason remain under a lazy Technical details disclosure. Raw data stays inert and unmodified. A malformed request exposes the full details immediately rather than inventing a summary. Missing exact action disables approval and keeps Reject available. A different approval identity resets the disclosure. Existing Drive, Memory, filesystem and configuration presentations remain in the shared regression set.

Spacing stays in existing owners: Ant Design modal insets and token gaps; Descriptions labels use the existing desktop 12rem detail role and stack on mobile. The Automation-specific CSS variant removes the nested details height limit so the body owns one scroll region while the footer remains visible. A small wrapping rule covers raw code and long content. No second modal, UI kit, token system, dependency, wire contract or authorization service was added.

## Verification

| Check | Result |
| --- | --- |
| `pnpm --dir web exec vitest run src/features/chat/ApprovalModal.test.tsx src/features/chat/AutomationDrawer.test.tsx` | 43 passed: 37 approval and 6 drawer tests |
| `web/`: `pnpm exec tsc --noEmit` | Passed |
| `pnpm --dir web run build` | Passed; existing large-chunk advisory remains |
| Playwright MCP Synthetic interaction | Disclosure, Reject, reload recovery, keyboard decision and saved Automation readback passed |
| Documentation/source audit | Relative links, balanced fences and `git diff --check` passed |

Component cases cover complete long/inert instructions, operation-specific labels, daily/weekly resolution and bounds, Event filters/dispatch/disabled triggers, proposed source replacement, filter clearing, explicit defaults, unknown settings, changed instructions/state, malformed/missing data and reset between approval identities. All existing shared approval tests remain included. Initial unit assertions used immediate CSS visibility during Ant Design's opening transition; tests now assert content/decision behavior, with rendered visibility checked in the actual browser. The raw JSON assertion compares `textContent` to preserve exact whitespace rather than Testing Library's normalized text matching.

### Rendered interaction and bounded polish

Used the existing disposable Synthetic API on `127.0.0.1:5680` with verifier-failure fixtures and Vite on `127.0.0.1:5683`, backed by `/private/tmp/agent-core-semantic-authorization-ui-20261016/verification.db`. No user host or production database was touched. `[test:automation-approval]` produces a complete daily 09:00 Asia/Ho_Chi_Minh background proposal with no report back.

The first batched inspection covered desktop and 390px mobile snapshots/screenshots. It found desktop label/value alignment to tighten; that correction was made in one batch. One confirmation round checked the final desktop/mobile rendering and recovered the pending request after reload. Navigation began at 768px; the restored modal was inspected after resizing to 390px, so no separate tablet-layout assertion is claimed. The final screenshots were captured and inspected at `/private/tmp/automation-modal-polish-20261016/automation-modal-polish-desktop-final.png` and `automation-modal-polish-mobile-final.png` as local evidence.

- At 1440px, the 720px modal shows the task and aligned readable fields before a collapsed Technical details row. IDs and verifier jargon do not dominate the initial view.
- At 390px, the modal is 374px wide, its body/details width is 326px and document width stays 390px. Labels stack, full strings wrap and controls remain reachable. Expanding the complete JSON produces one body scroller (688px viewport / 1516px content) while the footer stays within the 844px viewport (bottom 808px).
- Expanded Technical details and inspected the original `automation.create` JSON. Reject closed the proposal without adding a record.
- Submitted again, reloaded while waiting and recovered the same proposed task. Used native Tab navigation from Technical details to Reject and then Create automation; confirmed visible keyboard focus and pressed Enter. The drawer showed exactly one additional saved Automation, bringing this disposable catalog from one to two records. Task, timezone and background destination matched the proposal.
- Relevant model, Run and Automation HTTP reads returned 200. The unchanged drawer emits its existing Ant Design `List` deprecation warning; no approval-flow transport failures were observed.

Browser navigation was reset to `about:blank`. Only the API/Vite processes started for this verification were stopped. Long-instruction, malformed-data, Event and other approval-family stress cases were exercised in component tests; the real browser journey used the daily create flow plus long technical details. No claim is made for unexecuted browser variants, physical devices, live providers or a full E2E suite.

This is focused UI completion, not milestone acceptance. Backend/full solution tests, hosted CI and paid model calls were unnecessary for the presentation change and were not run. Concurrent unrelated backend/work-result changes in the shared checkout were preserved and excluded from this commit.

Canonical owners: [Frontend](../13-frontend-implementation-spec.md#live-approval-observed), [Testing](../16-testing-strategy.md#automation-approval-presentation). The [semantic authorization report](semantic-automation-authorization-verification-2026-10-16.md) owns the underlying behavior.
