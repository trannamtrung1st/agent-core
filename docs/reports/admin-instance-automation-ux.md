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

## Follow-up consistency review (2026-10-06)

The second review found and corrected three presentation/recovery gaps:

- Schedule and Thought navigation relied on the five-second poll rather than refreshing immediately. Two newly added component cases failed before the fix and pass afterward: a source created after the cached empty review becomes expanded and focused immediately. The browser journey additionally edits each source through the same owner API while Runs is open, then verifies its updated text and restored focus on return. Existing revision guards and draft preservation remain intact.
- A source read failure could be presented as a deleted/unavailable source. Schedule, Thought, Experience and Event subscription notices now require a successful read. Event subscriptions adds Retry subscriptions; a regression failed before that control existed and now verifies recovery/focus. Experience also verifies failure → Reload → recovery. MCP injected a 503, observed the retry control without a false missing-source notice, removed the fault and confirmed recovery at 390px.
- Schedule details/filters and retrying Thoughts exposed WaitingForApproval/WaitingToRetry rather than the labels used by Runs. Two Schedule regressions failed before the fix. Shared `runStatusLabel` now supplies Needs approval/Retrying across Schedule, Thought and the shared execution surface, retaining raw filter values and domain/wire states. A retrying Thought still overrides its earlier No action outcome and keeps Run now disabled.

The first browser rerun passed 12 cases but failed retrospection because its reused catalog contained several instances named Continuity reviewer. The source Session was confirmed to belong to a different instance (`01a10d96-126c-7a09-9029-d1594d5a7d9a`) than the newly created fixture (`01a10da0-7609-7dd0-8ac1-b4898194950d`). The fixture now uses a unique persona name and asserts Session ownership. The original automatic-retrospection/approval/no-action assertions remain intact; the final rerun passed all five cases.

Follow-up evidence, all under `local/admin-automation-ux/`:

| Check | Result / log |
| --- | --- |
| Schedule, Thought/Experience, instance navigation and drawer component run | 45 passed, `review-components.log` |
| Final shared labels, Schedule and drawer component run | 40 passed (Schedule 14, drawer 17, presentation 9), `review-labels.log` |
| Event subscriptions | 4 passed, `review-subscriptions.log` |
| Experience read recovery and retrying Thought | 2 passed, `review-extra-regressions.log` |
| Final Thought/Experience file | 16 passed, `review-continuity-final.log` |
| Final browser regression | 5 passed, `review-final-playwright.log`; Schedule/Thought journeys and Chat/Admin execution consumers |
| Admin collection regression before the fixture correction | 8 passed in `review-playwright.log`; delayed reads, unsaved persona, lifecycle, publication and focus retained |
| Owner run-read and Continuity/Thought API integration | 15 passed, `review-api-tests.log` |
| Frontend build | Passed, `review-build-final.log`; existing bundle-size warning |

The unique final component cases total 62 across six files (14 Schedule + 16 Thought/Experience + 2 navigation + 17 drawer + 9 presentation + 4 Event subscription). Counts above overlap between reruns and should not be added together. MCP separately observed exact Schedule/Thought return focus, disabled Thought Run now and no document overflow at 390px. A clean navigation interval had no console errors or failed HTTP responses; the intentional 503 recovery produced the expected failed request. Initial development-host startup refusal/delay was resolved with permitted local test hosts; no runtime verification remains blocked.

Repeat the follow-up browser command with the ready Synthetic hosts described above:

```sh
PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5084 PLAYWRIGHT_WEB_PORT=5184 PLAYWRIGHT_SQLITE_PATH=../local/secretary-e2e/review-mcp.db pnpm --dir web exec playwright test continuity-enhancements.spec.ts p9899-continuity.spec.ts background-work.spec.ts p96-admin-background-work.spec.ts --project=p9899-continuity --project=synthetic --output=../local/admin-automation-ux/review-final-playwright --trace=retain-on-failure
```

Verdict remains **UI/UX READY** for the requested local enhancement. Backend admission/execution/storage behavior and historical milestone acceptance are unchanged. No new hosted-provider verification was requested or performed. Intentional concurrent changes remain excluded from this follow-up.


## Event subscription concurrency review (2026-10-06)

A further consistency review found that Event subscriptions lacked the synchronous mutation guard used by the other owner controls. Two rapid clicks could submit overlapping requests before React rendered the disabled state. Returning from a Run could also refresh the selected subscription while a write was pending. Two focused regressions failed before the repair. Subscription writes now admit once, invalidate older reads, and queue source-navigation refreshes until the write completes. The selected registration is focused after reconciliation; a pending write does not show a false unavailable-source notice.

Verification under `local/admin-automation-ux/`:

| Check | Result / log |
| --- | --- |
| New regressions before repair | 2 failed, `event-write-before.log` |
| Event subscriptions and shared run presentation | 15 passed, `event-write-after.log` |
| Production frontend build | Passed, `event-write-build.log`; existing bundle-size warning |
| Synthetic event/background-work and store-connection browser regression | 3 passed, `event-review-playwright.log` |

Playwright MCP created a Secretary v2 instance through Admin, selected an active source, held the subscription POST and clicked Subscribe twice synchronously. It observed one POST, disabled Subscribe, and one saved registration after switching to Runs and back to Connections and releasing the request (HTTP 200). At 390px the document had no horizontal overflow. Earlier manual attempts with General Assistant v9 and Examiner v1 received the expected application-event capability rejection (HTTP 400); those fixture choices were corrected to Secretary v2. The component regression separately verifies queued source selection and exact registration focus.

This is a frontend ordering repair; the six-tab information architecture, wire contracts and backend admission policy remain unchanged. Local verification is green. The [hosted gate for 831a7c41](https://github.com/trannamtrung1st/agent-core/actions/runs/37394990476) was still running at the final review check: Compose smoke and backend gates passed, with frontend and browser gates outstanding. Hosted CI green remains the freeze condition.
