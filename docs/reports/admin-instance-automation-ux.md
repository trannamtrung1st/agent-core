# Agent Instance Automation and Runs UX

Verdict: **UI/UX READY**. Verified locally on 2026-10-06 with Synthetic providers and SQLite. This is a follow-on UI and read-projection change; it does not alter historical milestone freeze claims.

## Shipped information architecture

Active managed instances now have six top-level tabs:

| Tab | Contents |
| --- | --- |
| Identity & version | Existing identity, lifecycle and publication controls |
| Continuity | Memory and Experience |
| Automation | Schedules, Thoughts, Policies & models |
| Runs | Shared execution history, results, attention, approvals and cancellation |
| Connections | Application connection and event subscriptions |
| Effective configuration | Existing effective policy/configuration inspection |

Archived instances retain Memory inspection and show unarchive guidance for Experience and Runs. Automation is available for active instances. Compatibility instances retain their limited existing configuration/connection surfaces.

Schedule means a configured future obligation. Thought means an autonomous review opportunity where doing nothing is valid. Run means execution from a schedule, thought, event or retrospection. Chat retains its Background work drawer, using the same execution composition as Admin Runs. Ant Design v6 controls, collection toolbar, responsive detail descriptions, execution-model fields and confirmation dialogs are reused. Panel stacks own gaps; shared execution rows own padding and selected-state outlines.

Schedules show readable timing, enabled/lifecycle state, next run, last run, immutable original Chat/Admin provenance and effective model. Thoughts show prompt, interval, next review, last run, model and readable outcomes: No action, Action completed and Needs attention. Stored outcome/origin values remain unchanged. Empty states explain how records appear.

## Navigation and observed journeys

Schedule → View last run and Thought → View run select the exact owned run, highlight/focus it and work outside the first history page. View schedule/View thought returns to the exact expanded configuration, clears table search/filter/page and preserves unsaved editor fields. Event runs refresh Connections and focus their exact subscription. Retrospection runs refresh Experience and focus the generating checkpoint; View generation run returns to the same execution. Missing or bounded-out sources have explanatory notices.

The automated secretary journey authored a schedule through Chat, verified the same registration under Admin Automation, edited it at mobile width and confirmed its original Chat user request provenance remained intact. The Schedule journey admitted Run now, waited for completion, inspected its result and navigated back to its source. The Thought journey inspected an exact proposed action, approved it, observed Action completed, ran again to No action, navigated back and disabled/deleted the source. Existing freeze, stale approval, model defaults, lifecycle, memory, paging, diagnostics and connection journeys also passed.

Playwright MCP independently exercised the running Synthetic app at desktop and 390px: exact Schedule and quiet Thought run selection/return, disabled Thought controls, and Retrospection → Experience → generation run navigation. Expected selected source expansion/focus was observed. Document horizontal overflow was zero for Schedules, Thoughts and Runs at 390px; actions wrapped, detail labels stacked and timestamps remained readable. Clean final browser-console inspection returned no errors; the completed secretary journey also checks console/network failures.

Screenshots (local evidence, not required application assets):

- [Schedules desktop](../../local/admin-automation-ux/schedules-desktop.png) and [390px](../../local/admin-automation-ux/schedules-mobile.png)
- [Runs desktop](../../local/admin-automation-ux/runs-desktop.png) and [390px](../../local/admin-automation-ux/runs-mobile.png)
- [Thoughts desktop](../../local/admin-automation-ux/thoughts-desktop.png) and [390px](../../local/admin-automation-ux/thoughts-mobile.png)

## Bugs reproduced and fixed

| Problem | Correction and regression evidence |
| --- | --- |
| Inspect execution opened generic history instead of the referenced run | Owner-protected detail read and explicit selected-run state; component tests cover off-page selection, retry, stale responses and source callbacks; actual browser journeys verify exact identity |
| Last run vanished once more than 100 unrelated owner runs existed | Reproduced with two failing API theories before the fix; a registration-scoped latest read now filters by instance, profile and registration before ordering/limiting; both theories pass and both store implementations have contract coverage |
| Failed completed-result reads were silently omitted | Visible per-run error and Retry result; rejection/recovery component coverage |
| Cached source records could miss newly created Event/Experience sources | Refresh on navigation request, focus when the exact record is present, explain unavailable sources; component/MCP navigation evidence |
| Owner execution actions could overlap while an earlier mutation was pending | Synchronous guard and disabled action group; selected-read generations/revisions reject stale responses; component regression verifies another run's action remains locked until resolution |

The new `GET /api/v2/admin/agent-instances/{instanceId}/work-items/{workItemId}` reuses existing ownership checks: missing capability returns 401 and foreign/unknown work returns 404. Latest-run lookup returns one existing row or null, with no schema migration. TriggerRegistration → Occurrence → WorkItem, occurrence admission, execution, revisions, approval hashes and durable retry behavior remain unchanged. Run now does not call the model directly. Runs has no arbitrary execution authoring.

## Verification

Final relevant results:

| Check | Result | Local log |
| --- | --- | --- |
| Sequential Synthetic browser regression across six projects | 35 passed, 4.9 minutes | `local/admin-automation-ux/playwright-final.log` |
| Final drawer/Thought browser rerun after action-guard refinement | 4 passed, 44.1 seconds | `local/admin-automation-ux/drawer-e2e-final.log` |
| Focused component files, individually verified | 97 tests passed across eight files; counts below | Logs below |
| Continuity/Thought API integration | 15 passed | `local/admin-automation-ux/api-regressions-final.log` |
| WorkItem store contracts, InMemory and SQLite | 17 passed | `local/admin-automation-ux/work-store-contracts.log` |
| Production frontend build | Passed; existing large-chunk warning remains | `local/admin-automation-ux/build-final.log` |
| Diff whitespace, design JSON and changed Markdown references | Passed | Final repository checks |

Component counts: AdminApp 39, instanceMemoryAutomation 8, InstanceSchedulesSection 11, InstanceContinuitySection 13, AdminInstanceNavigation 2, EventSubscriptionsSection 3, BackgroundWorkDrawer 17, runPresentation 4. Evidence is in `all-affected-unit.log`, `continuity-unit-final.log`, `navigation-unit-final.log` and `drawer-unit-final.log` under the local evidence directory. Earlier aggregate runs contained failures while edits/test fixtures were being corrected; the affected files were subsequently rerun successfully. The 97 count is the final per-file aggregate, not a claim that an earlier failing invocation passed.

Reproduction commands from the repository root (browser command assumes a ready Synthetic API at 5084 and Vite at 5184, with restricted browser fixture port 5091):

```sh
pnpm --dir web exec vitest run src/features/admin/AdminApp.test.tsx src/features/admin/instanceMemoryAutomation.test.tsx src/features/admin/InstanceSchedulesSection.test.tsx src/features/admin/InstanceContinuitySection.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx src/features/admin/EventSubscriptionsSection.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx src/features/chat/runPresentation.test.ts --maxWorkers=1
dotnet test tests/AgentCore.Api.Tests --filter 'FullyQualifiedName~ContinuityReviewJourneyTests|FullyQualifiedName~ThoughtJourneyTests'
dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~WorkItemStoreContractTests
pnpm --dir web build
PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5084 PLAYWRIGHT_WEB_PORT=5184 PLAYWRIGHT_SQLITE_PATH=../local/secretary-e2e/review-mcp.db pnpm --dir web exec playwright test admin-collections.spec.ts background-work.spec.ts diagnostic-details.spec.ts operational-drawer-paging.spec.ts p96-admin-background-work.spec.ts store-connection.spec.ts z-admin-memory-automation-journey.spec.ts admin-lifecycle.spec.ts p97-harness-management.spec.ts p76-admin-journey.spec.ts continuity-enhancements.spec.ts p9899-continuity.spec.ts secretary-demo.spec.ts --project=synthetic --project=admin-lifecycle --project=p97-harness --project=p76-admin --project=p9899-continuity --project=secretary-demo --output=../local/admin-automation-ux/playwright-final --trace=retain-on-failure
git diff --check
```

An earlier parallel browser attempt collided in shared trace output; verification was rerun sequentially with isolated output. The 35-test log also contains a redundant managed-host bind failure during manual-host startup; the manually owned host became ready and all 35 tests passed against it. The final four-test rerun used already-ready hosts. No unresolved product test failure remains.

## Changed files

- UI: `web/src/features/admin/AdminApp.tsx`, `InstanceContinuitySection.tsx`, `InstanceSchedulesSection.tsx`, `EventSubscriptionsSection.tsx`, `instanceMemoryAutomation.tsx`, new `useAutomationSelection.ts`; `web/src/features/chat/BackgroundWorkDrawer.tsx`, new `runPresentation.ts`; `web/src/app.css`.
- Backend: `src/AgentCore.Api/AdminScheduleEndpoints.cs`, `ContinuityEndpoints.cs`; `src/AgentCore.Application/Ports/Work.cs`; InMemory/SQLite WorkItem stores.
- Regression coverage: new AdminInstanceNavigation/runPresentation tests; Schedule, Thought/Experience, event-subscription, drawer and memory-panel component tests; ContinuityReviewJourney/Thought API tests and WorkItem store contracts. Existing affected Admin, continuity, secretary, connection and operational browser scenarios use the new navigation and display labels.
- Documentation: README; product vision, backend interfaces, demo, frontend, protocol and persistence specifications; `.agents/context/DESIGN.md`; Impeccable design metadata/Admin surface notes; this report.

Intentional concurrent workflow/test changes and the unrelated proposal deletion were preserved and excluded from this change's commit.

## Limits and deferred work

No requested UI workflow is deferred. A historical started-at timestamp is not recorded, so Runs accurately shows created/updated time and result completion time instead of inventing a start time. Experience source lookup remains the existing bounded review; deleted or older checkpoints receive clear guidance. Optional unified owner Continuity search and bundle splitting remain outside this request. Verification used Synthetic providers; this pass makes no new claim about hosted-model judgment or physical touch-device behavior.
