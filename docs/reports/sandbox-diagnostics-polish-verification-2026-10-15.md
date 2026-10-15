# Session sandbox diagnostics polish — 2026-10-15

The drawer rendered a bare diagnostic icon in its own row beneath command output. Command diagnostics now sits beside exit/duration/file status, before output; Initialization diagnostics uses the same metadata grouping. Contextual text and the info icon form one native Ant Design action. The shared DiagnosticDetails `label` names both action and popover, while existing error notices keep Error details. Missing IDs omit the control; safe Diagnostic ID and Session ID copying and the environment-ID fallback remain.

Heading/metadata groups own 8px gaps; the command-result stack owns 12px spacing, with command/output paragraph bottom margins cleared. Narrow drawer actions and shared diagnostic Copy buttons have 40px minimum height. Escape closes the popover and restores its trigger before a subsequent Escape closes the drawer and restores the header action. No runtime protocol or recovery authority changed.

## Runtime verification

Playwright MCP used disposable InMemory Synthetic API 5088 and Vite 5188. Health confirmed `Synthetic`, build `0f994232`. A fixture Instance was provisioned and selected through Chat; sending Hello created an actual Synthetic Session and response. Controlled owner-scoped Sandbox read responses then supplied completed/nonzero-exit command and initialization states. This verifies the real drawer controls and shared popup against deterministic data; it does not claim execution or initialization against a real sandbox provider. Existing user app/data were not used, and no Stop/Reset mutation was performed.

| Viewport | Drawer | Diagnostic action | Copy action |
| --- | --- | --- | --- |
| 1440×900 | 640px | 32px high | 32px high |
| 768×900 | 640px | 32px high | 32px high |
| 767×900 | Full width | 40px high | 40px high |
| 390×844 | Full width | 40px high | 40px high |
| 320×600 | Full width | 40px high | 40px high |

The labeled command action remained within its metadata group, above output, with an 8px gap and no horizontal section overflow. Settled popover bodies measured 360px on wide views, 358px at 390 and 288px at 320, all within the viewport. Actual Copy produced the expected command Diagnostic ID and Session ID at all five widths. First Escape closed only the popover and restored its action; second Escape closed the drawer and restored Session sandbox. Local captures: `.playwright-mcp/sandbox-diagnostics-1440.png` and `.playwright-mcp/sandbox-diagnostics-390.png` (not committed artifacts).

Nonzero exit 7 retained separate stderr after disclosure. Initialization diagnostics displayed its own ID; command diagnostics correctly used the environment ID when its execution ID was absent. Failed initialization retained output context, omitted the command section and kept explicit Reset available. Missing-ID omission and stale-selection recovery guards remain covered by the focused component suite.

A representative mobile Admin Save failure used a controlled API 400. The existing Error details title remained, Copy measured 40px high, and Escape restored the trigger without closing Add prebuilt image or losing the draft. The only related browser console error was the expected controlled 400 response.

## Focused checks

- `pnpm exec vitest run src/features/chat/SessionSandboxDrawer.test.tsx src/features/chat/diagnosticDetails.test.tsx src/features/admin/adminFailure.test.tsx --maxWorkers=1`: 36 passed. New assertions cover grouping/order, contextual titles, safe copying, missing/fallback IDs and Escape/focus ownership. An existing CopyDetailsButton act warning remains non-failing.
- `pnpm run build`: passed, with existing bundle-size and SignalR annotation notices.
- The active design hook reported no deterministic issues. Documentation checks validate guide/preview agreement, JSON/YAML, token/source references, links and candidate source hashes.

CI was not requested or run. This is focused local evidence, not milestone/provider acceptance. Physical touch, manual screen-reader behavior and native browser zoom remain unverified.
