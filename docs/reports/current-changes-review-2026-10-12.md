# Current-change review — 2026-10-12

Review of the uncommitted account-scoped Email corrections, native Chat/Admin navigation and their documentation/tests on main. This is a follow-up to implemented features; no new milestone or P10/P11 work is opened. Historical hosted acceptance remains evidence for its recorded source only.

## Corrections found during this review

- Shared Admin publication helpers and additional acceptance scenarios still located route controls as buttons. Updated inventory, Chat/Admin, Session, Run and Automation navigation consumers to locate links, including `admin-managed-helpers.ts`, P7.6/P9.7/continuity/Secretary journeys, terminal Session reopening and `BuiltinEventSubscribers.test.tsx`.
- `text-conversation.spec.ts` still used `button.session-row-open` in reconnect/reopen and Markdown-history checks. Updated those selectors to the actual anchors without weakening their history assertions.
- `admin-table-overflow.spec.ts` counted only buttons in action cells. Its geometry check now includes Ant Design anchor controls and retains alignment, containment and page-overflow assertions at 1440/768/390.
- Clarified the frontend specification: modifier activation issues no source navigation/tab-selection command, while a destination conversation still follows the existing single-attachment Session lease contract.

The original email corrections preserve mailbox ownership, immutable Run configuration, account/binding authorization and durable at-most-once send claims. The selected ID/alias is continuation context, not authority. Disabled SMTP rejects before claiming the draft; no schema migration or live provider call is introduced.

## Runtime observations

| Scenario | Expected and observed outcome |
| --- | --- |
| Owned email selection, including two already-bound accounts | One fresh Run uses the chosen actual alias, reads recent messages with an omitted query, and completes without duplicating the user entry. Cancellation, stale revision, expiry, foreign Session, detach and newer input block continuation. |
| Disabled SMTP with InMemory/SQLite reopen | Reads/drafts remain available; send discovery, approval and dispatch reject without claiming the draft. Enablement plus fresh exact approval sends the same draft once in Synthetic. |
| Missing IMAP UIDNEXT | Fake TLS servers with one/600 messages return a message through the bounded sequence fallback, including sparse UIDs and truthful truncation. |
| Native route links at 1440/390 | Middle/modifier activation opens the saved destination without selecting the source tab or discarding an unsaved persona edit. Ordinary clicks, keyboard arrows/Enter, Back and refresh retain the specified state. |
| Interactive Playwright MCP against disposable Synthetic | Created an Examiner Instance through Admin; edited its persona without saving; Cmd-clicked Skills & resources, inspected the destination and verified the unchanged source edit; switched normally and returned with the edit retained. Sent Hello, navigated Chat → Admin → last Chat at 390px, sent Hello again, and observed two completed Synthetic responses in the same Session with Ready status. Document width equaled the 390px viewport. No console errors were observed; inspected application requests succeeded. |
| Email editor/chooser at 1440/768/390 | Existing Synthetic account setup, authentication-test display, chooser and continuation scenarios passed. |

MCP used ports 5280/5283, InMemory and disposable `/tmp/current-review-mcp` roots. Its browser and both hosts were stopped. CLI checks use separate test ports and disposable SQLite databases; no existing user database/session catalog was reset.

## Verification

Backend commands ran with `--nologo -m:1 -nr:false`; the initial solution build also used `--no-restore -p:UseSharedCompilation=false`. Focused/recheck commands reused those binaries with `--no-build --no-restore`.

| Check | Result |
| --- | --- |
| `dotnet test AgentCore.sln` — Domain/Application/Infrastructure portions | 195 / 1,746 / 1,198 passed; 0 / 8 / 10 opt-in tests skipped. Initial API portion had one recovery failure and was stopped before the separate API recheck. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` — full recheck | 549 passed, 0 failed, 3 opt-in tests skipped. |
| API filters `EmailSelectionRuntimeTests\|EmailAccountApiTests\|SelfManagementContinuationTests` | 20 passed. |
| Infrastructure filters `ImapSmtpEmailProviderTests\|EmailAccountIntegrationTests` | 24 passed. |
| API filter `Failed_user_persist_invalidates_attachment` | 1 passed in isolation before the successful full API recheck. |
| `pnpm run test --run --maxWorkers=1` — initial local Node 26 invocation | 859 passed, 74 failed: 70 storage-environment failures, two stale subscriber link roles and two Admin timeouts. |
| Node 22 `node node_modules/vitest/vitest.mjs run --maxWorkers=1` from `web/` | All 116 files and 933 tests passed, 0 failed. Node was explicitly `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node`. |
| Node 22 TypeScript check + Vite production build | Passed; existing SignalR annotation and large-bundle warnings remain. |
| `playwright test e2e/navigation-links.spec.ts --project=synthetic` | 2 passed. |
| Initial affected Synthetic batch | 50 completed cases passed; five stale/intermittent failures were rechecked below. The interrupted conversation case and remaining cases ran in the final-journeys batch. |
| Supplemental Synthetic batch: table overflow, diagnostic details, Instance Skills, self-management, unified Event Automation | 20 passed. |
| Synthetic lifecycle batch: Email accounts, P9.6 background work, terminal lifecycle | 7 passed. |
| Synthetic final-journeys batch: text conversation and four `z-admin-*` publication/management/resource journeys | 19 passed. |
| Separate acceptance projects: `admin-lifecycle`, `p76-admin`, `p97-harness`, `p9899-continuity`, `p910-continuity-maintenance`, `secretary-demo`, `faithful-manual` | 1 / 1 / 6 / 2 / 1 / 4 / 1 passed. |
| `git diff --check`, changed-document relative targets, design JSON parsing | Passed. |

CLI commands used `CI=1` and separate API/web/fixture ports. The supplemental/lifecycle/final-journey and acceptance invocations used `PLAYWRIGHT_FAITHFUL_MANUAL=1` to start only their required Synthetic host/Vite pair; each invocation had a fresh `/tmp/current-review-*.db`. These are passing component/functional checks across the affected scope, not a claim that the interrupted original command passed. Final backend project results total 3,688 passed and 21 opt-in skips; the final frontend suite is entirely green. Final loopback-port inspection found no remaining review servers.

The initial sandboxed .NET runner was blocked before test execution by its loopback socket permission. The focused tests were rerun with local execution permission and passed. The initial broader browser pass exposed outdated link selectors and was interrupted to reload corrected helpers; corrected paths and remaining scenarios passed separately. Interrupted CLI hosts left loopback ports occupied; only the four confirmed hosts started by this review were stopped before restarting. Those setup/interrupted runs are not counted as passing suites.

The initial full frontend invocation selected Node 26.7.0 in the `web/` shell. A temporary diagnostic confirmed Node's undefined native `localStorage` accessor masked jsdom Storage, causing 70 service-test failures; explicit Node 22.18.0 used jsdom Storage and passed the 59 affected realtime/catalog tests. The temporary diagnostic was removed. Two large Admin tests also hit wall-clock timeouts under the concurrent initial workload and passed in isolation without assertion/timeout changes. Two subscriber tests exposed missing link-role updates and were corrected. Operations now identifies Node 22, matching CI, for frontend verification; no user runtime configuration was changed.

`Failed_user_persist_invalidates_attachment` initially rejected reattachment after the fixture's reopen step. It passed in isolation and in the full API recheck without a production or fixture change. This is a recorded transient failure, not a demonstrated root-cause repair. Likewise, the initial P9.6 mocked background-list check passed on its isolated full-file recheck without a behavior change.

## Boundaries

Live Gmail authentication, public IMAP reading, SMTP delivery and recipient receipt remain deferred/unverified. Default tests make no paid provider calls. This review does not renew hosted milestone acceptance or require Compose changes; packaging was not changed. A push to main triggers the existing workflow, whose final hosted outcome is separate from these local results.
