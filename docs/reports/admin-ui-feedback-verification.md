# Admin UI/UX feedback — October 9, 2026

This bounded follow-up implements all remaining UI/UX feedback from the October 9 implementation review. It preserves Ant Design v6, shared Admin layouts and the existing Event/Automation architecture. The only backend changes are safe read projections for active subscriber counts and already-persisted signal receipts. P10/P11 remain unopened; no milestone freeze or hosted acceptance is claimed.

## Delivered

- Revoked Events offer **Reactivate Event**, including the confirmation button. The confirmation describes resumed acceptance, a new credential and the absence of replay for previously skipped deliveries. Cancel preserves revoked state and restores focus.
- Collection and details distinguish **Active subscribers** from **Total subscriptions**. Disabled subscriptions stay in the total; cancelled subscriptions are excluded. Counts describe configuration rather than guaranteeing execution while an Event is revoked or an agent is ineligible.
- **Received signals** lists the latest 20 accepted unique receipts, including those without subscribers. **Automation deliveries** separately describes per-subscriber admission. Execution results stay in Runs; activity reads expose no payload, token or hash.
- **Send a signal** provides copyable JSON/cURL examples with illustrative values, a secret placeholder, unique delivery-ID guidance and inline clipboard recovery.
- Collection actions reserve 130px for **View** and a labeled overflow menu, replacing the 380px group. Rename, Rotate/Reactivate and Revoke stay discoverable. The table has a 1040px local scroll layout; name links also open details.
- Capability authoring shows an on-demand count and expandable names. Nested Instance labels read **Profile** and **Triggers**, preserving route keys and retained drafts.
- The Automation drawer retains its sections. A persistent footer summarizes trigger, recurrence/end bounds, execution destination, reporting and disabled state before Save. Missing selections are explicit.

Presentation reuses AdminCollectionToolbar, useAdminDetailLayout, admin-table-actions and the shared confirmation helper. Ant Design Flex owns 8/12/16px sibling gaps; drawers own their 16px inset and mobile sizing. WebhookRequestExample is a focused product composition using direct Ant Design controls. No new CSS, token system or UI kit was introduced.

## Functional browser evidence

Playwright MCP used disposable SQLite Synthetic at `127.0.0.1:5108` through Vite at `127.0.0.1:5198`. Existing hosts and user catalogs were preserved.

| Actions | Expected and observed |
| --- | --- |
| Create Event; send/replay a generic webhook before subscribing | 202 then 200; one Received signals row and explicit empty Automation deliveries |
| Inspect examples; switch JSON/cURL | Valid illustrative JSON, exact webhook URL and bearer placeholder |
| Revoke; cancel reactivation; confirm and test credentials | Cancel retained Revoked; confirmation issued a credential; old token 401 and new token 202 |
| Save a disabled Automation; add an Active subscription on another Instance | Active 1 / Total 2 with both lifecycle labels; no JavaScript errors or unexpected HTTP failures |
| Change trigger, report and enabled state in New automation | Exact Event key, missing report-destination guidance and disabled warning; valid disabled Automation saved |
| Fork General Assistant v17; expand capabilities | Count 20; collapsed became expanded; all 20 names in accessibility snapshot; grants unchanged |

Initial harness attempts used a relative request URL, an ephemeral cross-call variable and an owner header before owner access was ready. These were corrected before successful scenarios. The old-token 401 was intentional. Existing Ant Design deprecation warnings remain.

## Rendered verification

One batched pass covered Events collection/details, Automation and capability disclosure at 1440/768/390px, including long instructions and populated subscriber state. Document width matched each viewport. Drawer client/scroll widths were 640/640 at desktop/tablet and 390/390 at mobile, with 16px padding. Summary/Save remained visible; mobile actions were 40px tall. Tables scroll locally. No discretionary second polish cycle was needed.

Ignored evidence is under `local/verification/admin-ui-feedback/`: `events-{1440,768,390}.png`, `event-details-{1440,768,390}.png`, `automation-{1440,768,390}.png` and `capabilities-{1440,768,390}.png`. Logs are in `logs/`.

## Checks

| Check | Exact invocation and result |
| --- | --- |
| Event HTTP integration | From root: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~OrderPlacedWebhookApiTests --nologo` — 7 passed |
| Event UI | From web: Node 22 `node_modules/vitest/vitest.mjs run src/features/admin/EventsSection.test.tsx --maxWorkers=1 --reporter=verbose` — 6 passed |
| Automation, capabilities, navigation | From web: Node 22 `node_modules/vitest/vitest.mjs run src/features/admin/InstanceAutomationsSection.test.tsx src/features/admin/definitionCandidateEditor.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx --maxWorkers=1 --reporter=verbose` — 37 passed |
| Production build | From web: Node 22 `node_modules/typescript/bin/tsc --noEmit` then `node_modules/vite/bin/vite.js build` — passed; existing large-chunk warning remains |
| Synthetic browser regression | From web with environment/config below: Node 22 `node_modules/@playwright/test/cli.js test --config=../local/verification/admin-ui-feedback/playwright.config.mts e2e/shared-events.spec.ts e2e/unified-event-automation.spec.ts e2e/automation-editor.spec.ts e2e/admin-tab-navigation.spec.ts e2e/admin-table-overflow.spec.ts e2e/admin-polish.spec.ts e2e/p96-admin-background-work.spec.ts` — 17 passed |
| Source/docs consistency | `git diff --check`, changed-document relative-link checks and source comparison — passed |

Final logs: `api-tests.log`, `events-unit-final2.log`, `admin-unit.log`, `build.log` and `e2e-final.log`. Frontend total is 43. Browser regressions also verify draft retention/recovery, navigation/history, table containment, both trigger types, original Run inspection and exact Automation source return.

Temporary config `local/verification/admin-ui-feedback/playwright.config.mts` imports the repository config, using its Synthetic project, absolute test/output paths and first two servers. ES-module loading required `.mts`. Environment: `PLAYWRIGHT_API_PORT=5108 PLAYWRIGHT_WEB_PORT=5198 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-core-ui-feedback.db`. Node executable: `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node`.

Earlier exploratory runs were superseded: jsdom modal assertions were corrected while actual browser confirmations passed. The first browser batch encountered a browser closure during edits and an overly broad Automations-label replacement in test locators. Both affected browser cases passed in the final batch. Only tab locators retain Triggers. Final checks use settled source.

## Limits

This is local verification of affected UI/UX, not the whole enhancement or an exact-commit hosted freeze. Full backend/frontend/Compose, unrelated acceptance projects and Real providers were not required or rerun for this slice. No hosted workflow was dispatched. Original enhancement evidence remains historical; future publication requires its own exact-commit CI result.

## Pre-publication review and design synchronization

The October 9 follow-up review checked the accumulated implementation and design documentation before commit/push. DESIGN.md, PRODUCT.md, the Admin surface brief and the Impeccable preview sidecar now agree with current navigation, Event activity/actions, Automation summaries and capability disclosure. Ant Design token primitives are unchanged. The guide retains the canonical eight sections; the sidecar narrative is copied verbatim from the guide. YAML/JSON, component/token references, color/typography metadata, local links and source targets pass consistency checks.

The review reproduced one additional keyboard issue: Rename selected from the Event overflow menu could retain a disappearing menu item as its return-focus target. EventsSection now preserves the More actions trigger. The new `cancelling Event rename returns focus to its overflow trigger` browser test opens the menu by keyboard, cancels Rename and asserts focus restoration. Playwright MCP also exercised cancellation twice and observed the correct focus. At 1440/768/390px, document width matched the viewport, the table scrolled locally on narrow screens and the mobile trigger remained 40px high.

Playwright MCP created an Event, inspected the request example, denied clipboard access, and observed the inline manual-copy instruction with valid selectable JSON. Switching to cURL cleared the error and retained the bearer placeholder. Initial navigation had a stale local owner capability from the earlier disposable database: normal local bootstrap recovered it; subsequent review navigation had zero console errors. A first Rename probe used the wrong dialog title; the corrected probe reproduced the focus issue and verified the fix. These were harness corrections, not passing assertions.

Review logs remain ignored under `local/verification/admin-ui-feedback/logs/`. API integration (`review-api-final.log`) passed all 7 OrderPlacedWebhookApiTests with the earlier invocation plus `-m:1`. The 17 affected Synthetic browser cases passed again (`review-e2e-final.log`); the new focused keyboard case passed separately (`review-focus.log`). TypeScript and production build passed (`review-build.log`), retaining the existing large-chunk warning. Initial restricted .NET server/test startup failed on named-pipe/socket permissions; reruns with local process permissions succeeded. No Real-provider calls or hosted milestone acceptance are implied.

The remaining changed navigation cases also passed: `review-navigation.log` records 7 checks across shared Events, the Admin memory/Automation journey, Admin lifecycle, continuity maintenance and Experience/Skill Automation. This used `review.playwright.config.mts` (same isolated API/Vite ports and database `/private/tmp/agent-core-ui-review.db`, only the first two web servers, projects Synthetic/Admin lifecycle/P9.10/P9.8–9.9) and the following files: `shared-events.spec.ts`, `admin-lifecycle.spec.ts`, `p910-continuity-maintenance.spec.ts`, `p9899-continuity.spec.ts`, `z-admin-memory-automation-journey.spec.ts`. Together with the earlier 17-case batch, these cover 22 distinct browser scenarios; repeated Event checks are not additional unique coverage. The new focus regression passed on the settled source.

The final focused frontend batch passed all 43 tests in 4 files (`review-unit-final.log`, 302.02s), using the earlier Node 22 invocation with all four changed test files together and `--maxWorkers=1`. Superseded restricted workers were stopped after the final run was established. The settled-source TypeScript check, production build, staged whitespace check and canonical-document link checks passed. The jsdom textarea measurement warning is fixture-only; live browser layout showed no overflow.

## Background Work UI selector follow-up

The next supplied review identified two remaining browser selectors that treated Background Work task titles as buttons. This UI-only follow-up updates `credential-bindings-work.spec.ts` to inspect Morning review through its row’s **View original result** action, assert the Needs attention result, and return to the catalog before checking the quiet row. `durable-journeys.spec.ts` uses that same action for the exact seeded retry Session. Its fixture helper reads the created Run’s Session ID because the preserved original-task title differs from the later Chat title; the test no longer assumes Retry fixture is the original title. Cancellation and reload assertions remain intact. No product UI, runtime, projection, scheduling or persistence behavior changed.

From `web/`, the final command was:

```sh
PATH=/Users/trungtran/.nvm/versions/node/v22.18.0/bin:$PATH \
PLAYWRIGHT_API_PORT=5108 PLAYWRIGHT_WEB_PORT=5198 \
PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-core-background-selector-final.db \
/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node node_modules/@playwright/test/cli.js test \
  --config=../local/verification/admin-ui-feedback/playwright.config.mts \
  e2e/credential-bindings-work.spec.ts e2e/durable-journeys.spec.ts e2e/background-work.spec.ts
```

All 5 scenarios passed (45.0s); ignored log: `local/verification/admin-ui-feedback/logs/background-selectors-final.log`. Coverage includes attention/quiet labels, exact approvals, source navigation, bulk-read recovery and detached reminder/retry cancellation surviving reload. The earlier attempt passed credential bindings but failed the retry selector because it still assumed the Chat title matched the original-task heading; the final run supersedes it.

Playwright MCP separately exercised original-result inspection with public DTO fixtures in the running Synthetic app: keyboard activation of View original result, the expected Needs attention summary, Back with restored action focus, and reopening at 390px without page overflow. Console errors and unexpected HTTP failures were absent. Backend behavior for cancellation/reload was exercised by the durable CLI scenario, not the mocked MCP result.

`git diff --check` passed. No application code changed, so production build and frontend component suites were not rerun for this selector-only slice. Changes were reviewed on top of the concurrently published `6e3395a7` CI-fixture commit and preserve its work. The supplied review reports failed hosted CI on `3947e51a`; this local UI verification does not establish exact-commit integrated acceptance or a new freeze.
