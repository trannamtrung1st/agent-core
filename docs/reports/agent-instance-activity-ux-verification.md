# Agent Instance Activity UX verification

Verified locally on 2026-10-09. This is a scoped UX enhancement; it does not change a milestone freeze or claim hosted CI acceptance.

## Delivered behavior

Agent Instance → Activity replaces the top-level Runs tab. Sessions is the initial view; Runs remains independently available. Nested routes retain selection through refresh and browser history. Historical `/admin/instances/{id}/runs` links resolve to Activity → Runs.

Sessions lists all durable contexts owned by the selected Instance and local profile, including zero-run, background, continued-in-chat and archived contexts. One Session produces one row regardless of its Run count or surface flags. Rows use existing titles, explicit untitled fallbacks, origin, lifecycle, last activity and secondary copyable IDs. Cursor pagination sorts by last activity at persisted millisecond precision, then Session ID. Deleted and foreign contexts are excluded before pagination.

Exact `?session=` inspection works outside loaded pages. Ordinary contexts reuse Session Run history and Files, then open the existing conversation with a Back to Activity action retaining the source Instance and search. Background contexts reuse BackgroundWorkDrawer, preserving original results, files, source diagnostics and same-Session continuation. Archived contexts remain inspectable; attach/continuation follows existing lifecycle guards.

Runs uses the existing instance Run catalog and diagnostic drawer, independently of the Session list. Human activation labels replace primary UUIDs; copyable IDs, outcome, related Session, admission/creation time, execution status and update time remain available. `?run=` reloads the exact diagnostic. Automation and Experience source navigation remains intact. Current domain Activations/Runs require a durable Session ID, but that context need not be a user-facing conversation. The list neither joins nor filters through conversational metadata; missing Session metadata does not hide a Run.

Continuity retains Memory, Experience, identity maintenance and focused source selection. Its former Run composition was extracted into the Activity owner; there is no duplicate history browser.

## Changed owners

| Area | Files/components |
| --- | --- |
| Navigation | `web/src/app/appRoute.ts`, `AppRouter.tsx`, `features/admin/AdminApp.tsx` |
| Activity | New `InstanceActivitySection.tsx`, extracted `InstanceRunsSection.tsx`, shared `useActivitySearch.ts`; incumbent toolbar, table, cursor/footer, Run details and Session artifacts |
| Conversation return | `BackgroundWorkDrawer.tsx`, `ChatApp.tsx`, `web/src/services/api.ts`; Admin→Chat mounting avoids duplicate attach/reopen |
| Read API | `BackgroundSessionEndpoints.cs`, `AgentRunDtos.cs` |
| Storage | `IMemoryStore` in `Stores.cs`, InMemory/SQLite stores, `CatalogCursor.cs`; no schema migration |
| Canonical documentation | `docs/04-backend-interfaces.md`, `13-frontend-implementation-spec.md`, `14-api-and-realtime-protocol.md`, `16-testing-strategy.md` |
| Coverage | Route/router, Activity/navigation unit tests; store/API tests; new Activity browser journeys and migrated overflow/event selectors |

New owner-protected read routes:

- `GET /api/v2/agent-instances/{instanceId}/sessions?limit=20&cursor=…`
- `GET /api/v2/agent-instances/{instanceId}/sessions/{sessionId}`

The catalog performs one bounded metadata/snapshot query in SQLite, with no per-row Run or transcript query. Existing Run APIs and persisted relationships are unchanged.

## Local checks

| Check | Command/result |
| --- | --- |
| Frontend type/build | `pnpm --dir web run build` — passed (`tsc --noEmit` and Vite). Existing SignalR annotation and chunk-size warnings remain. No lint script is configured. |
| Focused frontend | `pnpm --dir web run test --run src/app/AppRouter.test.tsx src/app/appRoute.test.ts src/features/admin/InstanceActivitySection.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx src/features/admin/InstanceContinuitySection.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx src/features/chat/ChatApp.test.tsx src/services/sessionRouteNavigation.test.ts --maxWorkers=2` — 8 files, 89 tests passed. |
| Store regression | `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo --disable-build-servers -m:1 --filter 'FullyQualifiedName~AgentRunAdmissionStoreTests|FullyQualifiedName~MemoryStoreTests'` — 108 passed, including SQLite/InMemory isolation, all surfaces and same-millisecond cursor ties. |
| Catalog regression | `dotnet test tests/AgentCore.Application.Tests --no-restore --nologo --disable-build-servers -m:1 --filter FullyQualifiedName~SessionCatalogTests` — 14 passed. |
| HTTP journeys | `dotnet test tests/AgentCore.Api.Tests --no-restore --nologo --disable-build-servers -m:1 --filter 'FullyQualifiedName~BackgroundSessionJourneyTests|FullyQualifiedName~SessionCatalogApiTests'` — 18 passed. |
| Backend build | `dotnet build AgentCore.sln --no-restore --disable-build-servers -m:1` — passed, zero errors/warnings. |
| Synthetic browser | Temporary Playwright config against disposable Synthetic/InMemory hosts at API 5188 / Vite 5278, one worker: `pnpm exec playwright test --config playwright.activity.local.config.ts instance-activity.spec.ts`, then `admin-table-overflow.spec.ts agent-run-background-session.spec.ts unified-event-automation.spec.ts` — all 8 distinct journeys passed after correcting the obsolete event-row selector. Temporary config removed after verification. |
| Diff whitespace | `git diff --check` — passed. |

The existing Real hosts on 5080/5173 were left untouched. Browser fixtures used disposable owners and sessions; no provider calls were needed. New browser coverage exercises 22 owned contexts and cursor paging, foreign isolation, exact detail/reload, two Chat turns producing separate Runs in one Session, return search, run diagnostics/reload, back/forward, legacy links, Continuity, original background result/files, same-Session continuation, failure recovery and long-title containment.

Playwright MCP also exercised the running Synthetic app: Sessions defaulted correctly; two messages returned `Hello from synthetic.` and completed separate Runs; Activity kept one Session row. Return-to-Activity retained its filter; exact Run reload and close worked. Escape returned keyboard focus to the initiating Session control. At 1440px, 768px and 390px there was no page-level horizontal overflow; tables scrolled locally, drawer width capped at 640px and filled the 390px viewport, and established 8px row gaps/16px drawer insets were retained. Final inspected screenshots are `/tmp/activity-ux-confirm-sessions-1440.png`, `/tmp/activity-ux-confirm-run-1440.png`, and `/tmp/activity-ux-confirm-run-390.png` (temporary local evidence).

## Validation boundaries and limitations

- Search explicitly covers loaded cursor pages, with no-results copy directing users to clear search or load older rows. It is retained in the URL.
- Session Run totals and execution duration are omitted: the bounded read models do not supply authoritative totals or first-attempt timing. Created is admission time and is labelled accordingly; no execution start time is fabricated.
- A broader optional Admin/Chat Vitest run encountered timeouts and was interrupted. The two timed-out Automation cases were rerun unchanged in isolation with `pnpm --dir web run test --run src/features/admin/InstanceAutomationsSection.test.tsx -t 'resets pagination when searching provenance|releases a rejected run' --maxWorkers=1`: 2 passed, 20 skipped. The final affected suite above passed. This is not evidence that the entire frontend suite passed.
- Full solution regression, Compose and hosted GitHub Actions were not run for this working-tree change. No milestone or production deployment acceptance is claimed.

## Follow-up polish and documentation sync

The 2026-10-09 follow-up preserved the incumbent dark Ant Design presentation. Activity continues to reuse AdminCollectionToolbar, compact collection tables, DrawerListFooter, SessionRunHistory, SessionArtifacts, AgentRunDetails and BackgroundWorkDrawer. The parent Flex owns section gaps, each row owns its 8px sibling gap, AppShell bridges existing Ant Design tokens, and the shared drawer CSS owns the single 16px body inset. No extra stylesheet, token system, UI wrapper or dependency was added.

Exact Session details now gate actions on their successful current read; a cached list row cannot hide a read error. Loading uses the same Ant Design Spin as other operational details. Background exact-read failure shows one actionable Alert without an ongoing spinner or duplicate errors. Retry and post-mutation refresh target the selected background Session, and overlapping/previous-selection responses cannot replace newer detail. Ordinary Chat background catalog consumers retain their existing behavior.

The canonical frontend specification and testing strategy were updated together with `.agents/context/DESIGN.md`, the existing Admin surface brief and its design sidecar description. Activity owns Sessions/independent Runs; Continuity owns Memory/Experience. Existing visual tokens and historical milestone claims were preserved.

Follow-up checks:

- `pnpm --dir web run test --run src/features/admin/InstanceActivitySection.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx src/app/AppRouter.test.tsx --maxWorkers=2`: **41 passed across 4 files**. The initial two new background tests had fixtures without the exact owner metadata; those fixtures were corrected to preserve the real owner guard, then the whole focused set passed.
- `pnpm --dir web run build`: **passed** (strict TypeScript and Vite); incumbent bundle/SignalR warnings remain.
- `pnpm exec playwright test --config playwright.activity.local.config.ts instance-activity.spec.ts agent-run-background-session.spec.ts admin-table-overflow.spec.ts`: **6 passed**, against disposable Synthetic ports 5188/5278. The first invocation could not launch Chromium inside the filesystem sandbox (MachPortRendezvous permission); the permitted rerun executed and passed. Temporary config removed afterward.
- Playwright MCP exercised exact Session failure → retry → Open conversation → Back to Activity, and Escape focus restoration. Cached actions were absent during failure; recovery succeeded. The only observed console/network error was the intentionally injected 503, followed by a successful 200 retry; no page exceptions occurred.
- At 1440/768/390px, long titles remained contained, page overflow was zero, row gap was 8px, and search width was 448/448/366px. Drawer body padding was 16px; width was 640px on desktop/tablet and 390px on mobile after the resize transition settled. Final local detail evidence: `/tmp/activity-polish-confirm-detail-1440.png`, `/tmp/activity-polish-confirm-detail-768.png`, `/tmp/activity-polish-confirm-detail-390.png`.

This follow-up changes frontend detail states and documentation only. Backend verification above remains the prior scoped result; it was not rerun. Full CI, Compose and full-repository regression remain unrun.

## Background lifecycle review fixes

The 2026-10-09 review identified two remaining lifecycle defects. A failed later exact-read poll now clears the selected background snapshot, removing its action/detail controls until retry or polling successfully reloads it. The operation epoch also resets when `initialSessionId` changes, invalidating pending continuation, metadata refresh and handling-run results for the previous selection. Backend work already admitted before navigation is not undone; the obsolete frontend response cannot navigate or overwrite the new selection.

Three regressions cover later-poll failure/retry and selection changes while continuation or subsequent metadata reads are pending. The two pending-response regressions failed against the prior implementation. The poll test uses controlled interval advancement; its initial timer-spy harness was corrected because nested history polling shares the same interval. The Synthetic background journey now also fails a poll after a successful inspection and verifies actions stay absent until recovery.

- `pnpm --dir web run test --run src/features/chat/BackgroundWorkDrawer.test.tsx src/features/admin/InstanceActivitySection.test.tsx src/app/AppRouter.test.tsx --maxWorkers=1`: **42 passed** across 3 files.
- `pnpm --dir web run build`: **passed**, including strict TypeScript and Vite. Existing chunk-size and SignalR annotation warnings remain.
- `pnpm exec playwright test --config playwright.activity.lifecycle.local.config.ts instance-activity.spec.ts agent-run-background-session.spec.ts`: **4 passed** against disposable Synthetic/InMemory API 5188 and Vite 5278. Temporary configuration is removed after verification.
- Playwright MCP created two actual Synthetic background Sessions. A later 503 removed continuation controls; retry restored them. Holding A's continuation response, switching to B through browser history, and releasing A retained B's inspection; continuing B then opened its exact conversation with Ready state. The expected injected 503 was the only console error; no unrelated failed request or page exception was observed.

The existing architecture and visual presentation are preserved. Hosted CI status will be reported for the exact pushed lifecycle fix commit; earlier local results do not imply hosted acceptance.
