# Feedback spacing and visibility — 2026-10-15

This presentation successor replaces the blanket 96px offset in [compact feedback verification](compact-feedback-verification-2026-10-15.md). The shared Ant Design message configuration retains the 40rem maximum, wrapping and normal-flow stacking. Its default bottom gap is now 16px plus the device safe area. An opaque native elevated fill and 1px `colorBorder` outline distinguish the toast without an extra shadow.

AppShell owns one contextual placement hook. It moves feedback only when its default position would cover a visible action surface, leaving 8px above it. The frontmost Modal/Drawer owns the actions considered; covered page controls add no space. Resize, scrolling, overlay visibility changes and growing inputs update clearance. The message region scrolls locally within half the viewport or the smaller available space above actions.

[Frontend Implementation](../13-frontend-implementation-spec.md) owns behavior; [DESIGN.md](../../.agents/context/DESIGN.md#admin-forms-creation-dialogs-and-validation) owns the presentation guidance. Candidate source fingerprints are in the [preview metadata](../../.impeccable/design.json).

## Observed local behavior

Playwright MCP used disposable InMemory Synthetic API 5088 and Vite 5188. API health reported `Synthetic`, behavior build `aba8cd94`. The existing Real app/database was not used. A new profile was saved through the Modal with API 200, and actual failed provisioning verification reproduced the long error. These checks establish feedback behavior, not successful image provisioning.

| Viewport | Wrapped error size | Default bottom gap |
| --- | --- | --- |
| 1440×900 | 640×86 | 16px |
| 768×900 | 640×86 | 16px |
| 390×844 | 358×130 | 16px |
| 320×600 | 288×174 | 16px |
| 720×500 | 640×86 | 16px |

All five had no horizontal text overflow. Computed border was `1px solid rgb(66, 66, 66)`, fill `rgb(31, 31, 31)` and shadow `none`. Local screenshots: `.playwright-mcp/feedback-edge-1440.png` and `.playwright-mcp/feedback-edge-390.png` (not committed artifacts).

Blank Image submission focused the invalid field while keeping the error inline. The desktop warning left exactly 8px above Save, using 76px bottom clearance rather than a fixed gap. At 390×844, both the profile Modal and nested Registry credential validation retained an 8px action gap. Closing the nested dialog returned to the profile; successful Save restored focus to Add prebuilt image and moved success feedback back to the 16px default.

A non-destructive Chat catalog/rename response fixture exercised the actual Session rail Rename → Save flow. With the navigation Drawer open, feedback stayed 16px from the bottom instead of reserving space for the covered composer. Closing the Drawer moved it above the composer with an approximately 8px gap (178px measured bottom clearance). A ten-line draft grew the composer; feedback followed it while retaining the 8px gap (360px clearance). Fixtures were removed afterward. No chat deletion was used.

## Checks and limits

`pnpm exec vitest run src/app/appFeedbackPlacement.test.ts src/app/AppShell.test.tsx src/features/admin/adminFormFeedback.test.ts src/features/admin/adminFailure.test.tsx --maxWorkers=1` passed all 12 tests. They cover ordinary edges, non-overlapping controls, footer intersections, safe-area edges, growing composers, multiple action surfaces, theme/portal cleanup and existing validation/diagnostic helpers. `pnpm run build` passed with the existing bundle-size and SignalR annotation notices. Final browser console inspection reported no errors. The design hook reported no deterministic findings; documentation checks validate links, tokens, metadata and candidate hashes. CI was not requested or run.

Native zoom, physical touch, manual screen-reader behavior, nonzero device safe-area hardware and exhaustive overlay combinations remain unverified. Previous diagnostic/overflow checks retain their original scope; transient messages still supplement persistent local recovery.
