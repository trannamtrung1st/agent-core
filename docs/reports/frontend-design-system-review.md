# Frontend design system review and refinement

Reviewed and updated 2026-10-12 against `787508c5` plus the current working tree. Method: focused source review, Playwright MCP interactions and existing regression suites. Scope: recent composer changes, shared focus presentation, and representative Chat/Admin consumers. This is local verification, not milestone or hosted acceptance.

## Result and corrected finding

The current composer follows the Ant Design v6 dark Operate baseline. Names and metadata remain readable within a compact row; category labels align at the trailing edge. Shared neutral chips reduce visual noise without losing the `/` invocation and `@` reference distinction. No unresolved findings remain in this reviewed scope.

**P2 — keyboard focus was indistinguishable from selection.** Open Add content → Use Skill with two choices, then Tab from search into the rows. The combobox's active selection remains on one row while another button receives focus; both formerly had the same fill and no outline. Enter correctly inserted the focused row, but the visual target was ambiguous. See the [before capture](assets/frontend-design-review/picker-keyboard-focus.png).

The fix includes native choice buttons in the existing shared product focus rule in [app.css](../../web/src/app.css). [AppShell](../../web/src/app/AppShell.tsx) publishes Ant Design `colorPrimaryTextHover` as `--ac-color-focus`; it resolves to `#3c89e8` in the current dark theme. The shared 2px inset outline distinguishes focus without shifting layout. Its calculated contrast against the composited selected elevated row is approximately 3.22:1. Model, header and session-row text controls use the same role. The editing well keeps its intended neutral focus treatment.

The existing [picker keyboard regression](../../web/e2e/structured-composer.spec.ts) now moves Tab focus away from the combobox selection, checks the visible outline, presses Enter and verifies that the focused Skill is inserted while the other Skill is absent. See the final [desktop](assets/frontend-design-review/picker-focus-fixed-desktop.png) and [mobile](assets/frontend-design-review/picker-focus-fixed-mobile.png) captures.

## Current system and ownership

| Relationship | Owner and current rule |
| --- | --- |
| Theme and portal roles | Ant Design `darkAlgorithm`; AppShell maps resolved tokens and publishes product roles on the document root. |
| Shell and sibling spacing | Existing 8/12/16px roles; picker shell and rows each own their 8px inset, and the row body owns its 8px gap. |
| Choice names | Natural width without flex shrink; maximum 50% of row body. Short names do not reserve a half-row. |
| Metadata and category | Metadata follows the actual title width and shrinks first. Logical auto margin keeps the category at the trailing edge. |
| Long labels | File paths preserve the final 12 Unicode code points; other labels use end ellipsis. Accessible names and titles retain full details. |
| Chips | One `.composer-chip` presentation across Lexical, queued drafts and history; neutral fill, readable text, 12px radius, 2px/8px padding and no border. |
| Hit areas and overflow | Choices retain 40px desktop/44px mobile minimum height. The choice list scrolls independently; containers stay within the viewport. |
| Keyboard focus | One shared inset outline for product text controls and choices, separate from selected/hover fill. |

The [frontend specification](../13-frontend-implementation-spec.md#unified-structured-composer), [design guide](../../.agents/context/DESIGN.md), [schema-v2 preview](../../.impeccable/design.json) and [Chat surface brief](../../.impeccable/surfaces/web-src-features-chat-chatapp-tsx.md) reflect the same final presentation. Preview Model/session controls and choice rows also expose the corrected focus state. Existing behavior and architecture ownership remain in `/docs`.

## Runtime evidence

Used an isolated InMemory Synthetic backend on 5097 and Vite on 5183. `/health` reported Synthetic and build `787508c5`. The host used the existing local build with `--no-build`; no backend source changed. Local server startup required loopback permission after the sandbox blocked Vite's bind. Only disposable Instance/Session fixtures were created.

- MCP selected an identity, sent Hello and observed the completed Synthetic reply. It created short and long Skill fixtures, selected through search/Enter, sent a Skill-only turn and restored its historical chip after reload. Exhausted search showed No matching choices; Escape restored Message focus without inserting content.
- Initial geometry checks covered 1440/768/767/390/320px. Short Review measured about 46px; the long title capped at 292px in a 584px desktop row body and 119px in a 238px body at 320px. Name-to-metadata gaps stayed 8px; categories stayed contained; row heights were 40/44px, with no page overflow.
- After the fix, MCP checked focus on a different row from selection at 1440/768/390/320px: the focused row had a 2px solid outline, the selected row retained its fill, and no row/page overflow appeared. Enter inserted Review and sending produced the expected third assistant reply.
- Representative Model, Open Admin and session-row controls resolved the same focus color and inset. Opening Model by keyboard showed its three catalog choices; Escape returned focus. Admin inventory at 1440/390px reused the same container and 8/12/16px roles and kept table overflow local.
- The static picker preview rendered at 1440/390/320px with the same outline, trailing category alignment and 40/44px heights, without horizontal overflow. Successful MCP journeys had no console errors; inspected application requests succeeded. Initial navigation before backend readiness and a rejected overlong test-fixture name were setup effects, corrected before verification.

## Checks and limits

- `pnpm run test --run src/features/chat/Composer.test.tsx src/features/chat/composerChoices.test.ts src/services/messageParts.test.ts src/features/chat/ChatApp.test.tsx src/features/chat/ModelPicker.test.tsx src/features/chat/SessionRail.test.tsx`: **95 passed**. Existing React `act` warnings remain.
- `pnpm run build`: **passed** after the focus change, including strict TypeScript. Existing SignalR annotation and bundle-size warnings remain.
- Focused Playwright keyboard regression: **1 passed** immediately after the fix.
- Final `pnpm exec playwright test --config=playwright.design-review.tmp.config.ts e2e/structured-composer.spec.ts e2e/builtin-composer.spec.ts`: **27 passed**. This includes structured send/history, built-in activation and revoked-selection recovery, clipboard/IME/queue behavior, responsive/short viewports and automatic paging/Retry. The temporary configuration reused only the isolated review servers and was removed afterward.
- Documentation validation covers schema-v2 references, token/source parity, narrative guidance, local links/fragments, balanced fences and `git diff --check`.

The design hook reported no deterministic issues in the changed AppShell. Functional browser checks establish the affected paths; they do not establish physical microphone/touch behavior, every Admin detail surface, a full accessibility audit or hosted acceptance. Backend suites and hosted CI were not repeated for this presentation change. Prior [composer implementation evidence](composer-single-row-verification.md) remains historical. Only review-owned servers and temporary configuration were cleaned up.
