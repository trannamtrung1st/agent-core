# Design system extraction and polish

Verified 2026-10-08 against an isolated Synthetic API on port 5128 and Vite on port 5208, with in-memory persistence and disposable storage. The existing Real app and concurrent working-tree changes were preserved.

## Shared ownership

| Relationship | Owner |
| --- | --- |
| Colors, 8/12/16 spacing, 4/6/8 radii, typography | `AppShell` consumes resolved Ant Design tokens and exposes product CSS aliases. Document-root publication also covers body portals; cleanup restores prior values. |
| Chip/composer/bubble radii, reading measure, breakpoints | One declaration per product shape in `app.css`; existing geometry is preserved. |
| Inventory and configuration panel surface/header/title | Shared CSS selectors, with 16px panel insets and no duplicate rules. |
| Collection search and pagination | Existing `AdminCollectionToolbar`; contained Versions search adds no second inset. |
| Composer sibling/action spacing | Ant Design `paddingXS`, including queued-message actions. Mobile Model occupies the first row; Attach/Voice and Send share the bottom row. |
| Keyboard state | Shared primary focus outline on product text/icon controls and a focused composer border. Ant Design retains control states. |

[DESIGN.md](../../.agents/context/DESIGN.md) records these rules and the resolved dark palette. The theme seed remains `#1677ff`; product CSS now follows Ant Design's resolved primary `#1668dc`, text `rgba(255,255,255,0.85)`, success `#49aa19`, and existing neutral surfaces. Undefined `--ant-*` references were replaced by the shared aliases.

## Screenshot evaluation

Reviewed Chat, the model overlay, Admin inventory, immutable version inspection, and inline error recovery at desktop/mobile sizes, with inventory also checked at the 768px breakpoint. [Computed measurements](assets/design-system-measurements.json) confirm 8px composer padding/gaps, 8px header block padding, 16px panel insets, and no document overflow on the checked surfaces.

The first inspection found a three-row mobile toolbar. The final confirmation shows a 90px two-row toolbar: Attach, Voice and Send share the same vertical position, with 44px icon hits. Inventory headings/search/table edges remain aligned; immutable details stack on mobile, and wide tables scroll locally. The model overlay and version drawer retain their theme and insets outside the app shell.

| Evidence | Capture |
| --- | --- |
| Desktop Chat and Model overlay | [1440px](assets/design-system-chat-model-desktop.png) |
| Mobile Chat with wrapped draft | [390px](assets/design-system-chat-mobile.png) |
| Admin inventory | [1440px](assets/design-system-admin-desktop.png), [768px](assets/design-system-admin-tablet.png), [390px](assets/design-system-admin-mobile.png) |
| Immutable version drawer | [Desktop](assets/design-system-version-desktop.png), [mobile](assets/design-system-version-mobile.png) |
| Inline failure and Retry | [Mobile](assets/design-system-admin-error-mobile.png) |
| New-chat state | [Desktop](assets/design-system-chat-empty-desktop.png) |
| Incumbent inventory | [Desktop before](assets/design-system-before-admin-desktop.png), [mobile before](assets/design-system-before-admin-mobile.png) |

## Functional verification

Playwright MCP was attempted for initial inspection, but its shared page repeatedly reset to `about:blank`. An isolated Playwright CLI browser provided the interaction and screenshot evidence.

The local verification script (`/private/tmp/agent-design-system/verify.mjs`) passed these actions and assertions:

- Select an Examiner instance, send text, and observe the completed `Hello from synthetic.` response.
- Open Model, verify a visible 2px keyboard focus outline, and dismiss it.
- Cancel End and retain the live composer.
- Retain a long mobile draft and assert the aligned Attach/Voice/Send row.
- Search to no matches and clear the search to restore records.
- Open immutable version details, inspect the form, and dismiss with Escape.
- Inject an Event Sources 503, keep the error on its panel without a false empty state, then Retry successfully.

No JavaScript page errors or unexpected failed requests occurred. A separate console inspection of the Admin version journey recorded no console errors. The intentionally injected 503 is the recovery fixture.

Repeatable checks passed:

- `pnpm run build` — strict TypeScript and production build.
- `pnpm run test --run src/app/antdTheme.test.ts src/features/chat/Composer.test.tsx src/features/chat/ModelPicker.test.tsx src/features/chat/SessionRail.test.tsx src/features/admin/DefinitionVersionsTable.test.tsx` — 49 tests.
- `pnpm run test --run src/app/AppShell.test.tsx` — 2 tests for theme changes/body portals and restoration on unmount. AppShell and Composer were rerun after the final change: 20 tests passed.
- `pnpm exec playwright test --config /private/tmp/agent-design-system/playwright.config.mjs approval-layout.spec.ts operational-drawer-paging.spec.ts` — 3 existing regressions passed: short-viewport approval layout/replacement scroll, drawer paging/retry/read status, and Admin paging across resize/refresh. The temporary configuration points only to the disposable host.
- `git diff --check` — passed.

Build output still reports bundle-size and SignalR annotation warnings; unit output includes jsdom height and React `act` warnings. These did not fail the checks. This is a scoped presentation pass, not whole-application or milestone acceptance. Actual microphone/audio behavior, hosted-provider behavior, all Admin form combinations, and a full accessibility/contrast audit were not exercised.
