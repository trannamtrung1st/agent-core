# Browser entry cutover verification — 2026-10-21

The browser now mounts only canonical Account/System/organization administration and exact tenant Chat. Root enters Account organization selection; anonymous identity handling then offers sign-in with that destination. Retired `/admin/...`, unscoped `/c/...` and unknown paths show Page not found without mounting Chat or guessing a tenant. Root query/hash aliases do not restore the old composer. Remembered conversations accept only same-origin exact tenant Chat paths and use separate organization keys.

`AccessApp` passes an explicit permission-filtered context to the shared `AdminShell`. Its existing canonical layout, native rail/Drawer, breadcrumbs, headings, draft indicators, keyboard and focus behavior remain. Implicit global Admin navigation/search and the unscoped page tree are removed. The System Browser privacy unsaved guard now compares its actual canonical System route, retaining saved-draft restoration and departure protection.

## Retirement and preservation

The runtime import audit at baseline `90a351d0` found 41 modules exclusive to the old Admin entry. This slice removes 32 of them. Independent pure form/validation/presentation controls and shared types remain. Workspace and Skill type modules no longer contain their unused owner HTTP implementations. The obsolete Admin test workspace is removed.

`web/src/test/fixtures/retired-browser-admin.json` records removed production files and 181 retired test declarations across 24 source test files (parameterized declarations are counted once). They exercised removed owner-host pages/contracts; none is skipped or retained behind a compatibility host. Four inventory presentation cases move to `adminPresentation.test.ts`; controlled candidate/Skills and authoring-option cases remain in their mixed file; the independent complete-consolidation preview moves to `ApprovalModal.maintenance.test.tsx`.

This module does not certify feature parity. Coordinated state isolation, runtime-state inspection, Harness/Experience/maintenance and every remaining proposal family still require their explicit canonical audit/implementation where absent. Browser owner-token issuance/storage/retry, unscoped service/default transports, legacy URL builders, internal SessionHost/approval/workers and schema cleanup remain open. No browser authority is added by a Page not found view. The full proposal goal remains active.

## Observed flows

- Playwright CLI at 1440×900 and 390×900 seeds an obsolete local token, opens anonymous root and observes sign-in for organization selection, signs in through the native form, and opens authenticated root to My organizations. Four retired/unknown URL families show Page not found with no composer/New chat. Native desktop rail or narrow Drawer navigates Instances → Roles & permissions → Instances, then Chat admits text and displays `Hello from synthetic.`. Both final scenarios pass with zero owner/unscoped requests and zero page exceptions.
- Canonical organization switching/logout retires prior history/draft/transport; the new organization answers separately and logged-out private URLs reveal no retained content. Real WebSocket reconnect preserves the unsent draft and avoids duplicate admitted text. Both pass.
- Canonical lifecycle sends text, observes durable rendered receipt, reloads one retained transcript, renames, archives/unarchives and completes the same read-only conversation. An initial run stopped at the historical assertion that Add content was absent. Tenant Attachments already introduced that control; the corrected assertion requires it visible. The complete lifecycle rerun passes. No product behavior was weakened to satisfy that assertion.
- Playwright MCP successfully opened root and observed canonical Account/sign-in handling. A role locator timed out before sign-in; then MCP returned `Transport closed` for login and a fresh navigation retry. No completed MCP login/navigation/Chat flow is claimed. CLI is the practical fallback for both viewport journeys. No physical microphone/audio or hosted provider qualification was attempted.

## Checks and artifacts

All commands run from `web/` unless stated otherwise. Disposable runner configuration `/private/tmp/client-cutover/entry.config.mts` imports `playwright.tenant-chat.config.ts`, uses the existing built Synthetic API with `--no-build --no-restore`, and supplies separate ports and roots; no user hosts/database were changed.

| Check | Result | Evidence |
| --- | --- | --- |
| `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/app/AppRouter.test.tsx src/app/appRoute.test.ts src/features/access/accessRoute.test.ts src/features/admin/adminPresentation.test.ts src/features/admin/definitionCandidateEditor.test.tsx src/features/admin/BrowserPrivacySection.test.tsx` | 52 passed, six files, 27.99s | `/private/tmp/client-cutover/unit-final.log` |
| Preserved `ApprovalModal.maintenance.test.tsx` focused run | 1 passed | `/private/tmp/client-cutover/preview-preserved.log` |
| `pnpm run build` | Passed, existing bundle-size warning; Vite 6.85s | `/private/tmp/client-cutover/build-final.log` |
| CLI `--grep 'authenticated entry'`, final native navigation checks | 2 passed, 18.3s | `/private/tmp/client-cutover/entry-final.log` |
| CLI organization switch/logout and WebSocket reconnect | 2 passed; lifecycle in this initial group failed on obsolete Add content assertion | `/private/tmp/client-cutover/chat-regression.log` |
| CLI `--grep 'tenant text, rendered'`, corrected lifecycle | 1 passed, 14.7s | `/private/tmp/client-cutover/lifecycle-final.log` |

No C# sources, styles, fonts, assets, theme or visual token policy changed. The canonical shell projection is preserved; no new Impeccable review/detector result is claimed. `.impeccable/design.json` SHA-256 remains `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`. CI and full milestone/proposal acceptance were not requested or run. Canonical frontend/routing/testing documentation is updated; exhaustive older normative/historical documentation consolidation remains part of the final proposal audit.
