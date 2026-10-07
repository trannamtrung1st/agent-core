# Unified Automation model verification

Status: final follow-up closure pending hosted CI on its final behavior SHA. The original cutover passed all five hosted Synthetic/Compose jobs on `60dda1dff150e1f13134ec53881e9a31548b9d73` (2026-10-07); those results do not establish closure of later functional changes. P10/P11 remain unopened. Historical reports and freeze SHAs are unchanged.

Starting baseline: `fcd2090e65a4bbcdc08b4fdaf1f5cce8ef9d187c`. Original verified cutover behavior commit: `60dda1dff150e1f13134ec53881e9a31548b9d73`. Implementation was committed directly to `main` and pushed after explicit owner approval. This report is updated separately as documentation-only closure evidence.

## Implemented contract

One Automation owns Name, Instructions, Schedule/Event trigger, revisioned lifecycle, optional tool-capable model and immutable creation provenance. Event rows have no fake timing. Chat and Admin share the same authoring owner; current-turn authorization applies to future-behavior mutations. Owned inspection is read-only. Webhooks are ingress with per-Automation dedupe, configured instructions and untrusted normalized payload; paired root/depth correlation rejects depth above four.

Authored activations and Run now always use the existing durable occurrence/intake/tool loop, including while Chat is attached. Generic work.complete requires NoAction/ActionCompleted/AttentionRequested and a bounded summary with consistent attention flag. NoAction rejects accepted effects. Model/instructions are frozen at admission; edits affect future runs. Existing approval hashes, execution policy rechecks, lease/checkpoint/retry/recovery and uncertain-effect fences remain.

Experience source inspection and recording are ordinary semantic tools with owned stable cursors, bounded observable evidence, sensitivity checks and unchanged-source recording. Repeated recording is a no-change result. Manual review is a generic ManualInvocation Run; recurring review is ordinary Automation instructions. Automatic lifecycle synthesis, special retrospective execution and per-instance semantic review cadence were removed. Consolidation permission remains independent.

## Persistence cutover

Migration 20261007072939_UnifiedAutomation creates the final Automations table and resets disposable old runs/approvals/attention/captures/occurrences/events/deliveries/Experience. It drops TriggerRegistrations and ContinuityMaintenanceSettings, preserving independent owners. No behavioral compatibility readers, old DTO/endpoint aliases or dual-write projections remain. Historical migration files and schema-history stamping remain migration metadata only.

UnifiedAutomationMigrationTests seeds every retired table, migrates, verifies removal/empty final state, preserves instance/profile rows and reopens. API journeys create new Schedule/Event records and verify restart. The explicit reset command is `python3 scripts/reset-automation-demo.py --demo-data /absolute/path/disposable.sqlite`; it was exercised against a disposable sentinel SQLite database. It removes only that database and sidecars; data directories require separate explicit cleanup.

## Acceptance mapping and local evidence

| Requirement | Evidence |
| --- | --- |
| One Schedule/Event resource, validation, CAS, ownership and models | AutomationTests, TriggerStoreContractTests (both providers), Admin API and authoring tests; unified editor/navigation tests |
| Generic quiet/action/attention completion and contradictions | DurableReminderTests, WorkCompletionRequest/contract/capacity tests; Secretary and continuity browser journeys |
| Current authority, approvals, freezing and exact actions | HostedTriggerToolTests Event create/inspect/update/run/disable/delete journey; denial for foreign owners, occurrence authority and negated deletion; seven harness browser scenarios |
| Event configured instructions, untrusted payload, durable delivery and bounded recursion | OrderPlacedAdmission/Webhook tests, UnifiedAutomationJourneyTests; new Connections → Automation → actual webhook (202), duplicate (200), exactly one quiet Run, exact source focus and reload browser journey |
| Semantic source/record, stable checkpoints and no duplicate Experience | Ordinary Automation source inspection/record at cursor 4, ActionCompleted, no alert, repeated recording NoAction, one record and stale cursor conflict; three API integration journeys pass |
| Destructive reset and restart/recovery | UnifiedAutomationMigrationTests seeds every retired table, migrates, preserves independent owners and reopens; store/lease/checkpoint/fence regressions; disposable explicit reset script exercise |
| Admin/Chat shared sources and provenance | Secretary four-scenario journey, continuity authoring/source-focus journey, Chat Automation drawer and owner-protected APIs |
| Reminder stays durable across detach without transcript pollution | Faithful Manual-A: actual one-minute wall clock, durable completion and reload; no fake clock advancement |
| Six-tab navigation, Connections/Workspace placement and presentation | Navigation unit/browser tests and Playwright MCP confirmation at 1440/768/390px |

Final local backend evidence: Domain 150 passed; Infrastructure 810 passed, nine opt-in skipped; API 381 passed, three opt-in skipped; emitter four passed. Application 1307 passed, one opt-in skipped after a mailbox-barrier repair to a timing-sensitive receipt test; its focused receipt/authoring regression also passed 58 tests. Total: 2652 passed, 13 opt-in skipped. No hosted provider keys were used.

Frontend regression passed 703 tests in 93 files before the final focus/navigation correction. The final local repeat passed 701 and timed out on two continuity tests while sharing CPU with a build; the same continuity file then passed all eight tests in isolation with its existing timeout. No product defect was reproduced and no timeout was increased. The final hosted full frontend gate passed all 703 tests in 93 files and the production build, confirming the final behavior commit. TypeScript/Vite production build passes; the existing large-bundle warning remains. Focused final UI/navigation passed 23 tests.

The initial broad browser run passed 93 scenarios and had nine failures plus one unrun scenario: stale-contract fixtures and one transient messaging console error. Corrected targeted runs passed 16 scenarios plus seven isolated affected scenarios, including the new Event journey. The unknown messaging console error did not recur in its focused corrected run. Faithful Manual-A, Admin lifecycle, P7.6 and all seven P9.7 harness scenarios pass. Final sequential acceptance also passed both P9.8/P9.9 continuity scenarios and the P9.10 maintenance journey. These exercise exact protected-skill approval, ActionCompleted followed by NoAction, semantic Experience, consolidation authority, separate lineage, default-off behavior, forgetting and reload. The final local Compose smoke passed owner access, approval/result, ordinary recurring Automation, workspace, credential protection, SQLite volume survival and host recreation. Its disposable containers/network/volume and the local verification hosts were cleaned up. The final hosted core and acceptance runs passed on the exact final behavior commit.

Playwright MCP also created Schedule/Event Automations and quiet Runs, used a disposable one-time Event Source credential for actual ingress/dedupe, inspected the exact Run and returned to its source. Credentials were cleared and never included in this report.

## Verification commands

The local runs used Synthetic providers without credentials. The aggregate backend result includes the final full Application rerun after the receipt-test mailbox barrier; the preceding full solution run had that single timing failure.

```sh
dotnet test AgentCore.sln --no-restore --nologo -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false
NODE_OPTIONS=--no-experimental-webstorage pnpm --dir web exec vitest run --maxWorkers=1
pnpm --dir web build
pnpm --dir web exec playwright test --project=synthetic --project=browser-stt --project=browser-browser
```

Acceptance projects were run separately with isolated disposable SQLite paths: faithful-manual (real wall clock), admin-lifecycle, p76-admin, p97-harness, p9899-continuity, p910-continuity-maintenance and secretary-demo. The Event webhook journey also passed in isolation. Compose used `./scripts/compose-sqlite-volume.sh` with a dedicated project/port. Local Node 25 required disabling experimental Web Storage for jsdom; hosted CI uses Node 22.

## Bounded Impeccable finish

Loaded the project design context plus polish and craft-floor guidance. One batched rendered inspection covered 1440, 768 and 390px, populated collection, Event editor, long instructions and shared details. All three measured document width equal to viewport width. Inspected screenshots show shared Ant Design hierarchy, local table scrolling, wrapping actions, readable focused textarea and a 40px mobile Save target. Functional E2E covers empty, validation, stale/error/retry, running, approval and exact source focus. One coherent correction batch added save/delete focus restoration and “On event” for active Event sources; final confirmation verified disabled Save for an empty Name, read-failure reload recovery, empty search, focused editor at 768/390px and exact save/Run source focus. Screenshots were visually inspected; document width stayed equal to viewport width. Final approval confirmation passed both continuity scenarios. The settled 640px approval drawer was visually inspected with animation disabled in an ignored screenshot harness: instructions and approval actions are readable and reachable. The temporary harness was removed; screenshots remain under local/verification/unified-automation/. DESIGN.md now records the verified reusable collection, shared editor and navigation presentation; product behavior stays in canonical docs.

## Hosted verification

Workflow [37600240526](https://github.com/trannamtrung1st/agent-core/actions/runs/37600240526) tests exact behavior commit `60dda1dff150e1f13134ec53881e9a31548b9d73` on `main`.

Completed backend logs report Domain 150 passed, Infrastructure 804 passed/15 skipped, Application 1307 passed/one skipped and API 381 passed/three skipped: 2642 passed, 19 skipped on the hosted Linux runner. The local aggregate additionally includes the emitter and platform-specific Infrastructure coverage. Hosted acceptance passed all 17 scenarios across its seven isolated projects. Hosted Compose passed. Frontend passed all 703 tests in 93 files and the TypeScript/Vite production build. Core browser passed all 106 scenarios without retries reported as flaky.

All five jobs completed successfully: Synthetic backend, Synthetic frontend, Synthetic Playwright core, Synthetic Playwright acceptance and Synthetic Compose smoke. The later evidence commit changes documentation only; the workflow above verifies the final behavior SHA.

## Closure and limits

The proposal's architecture, continuity, UI and quality gates are complete. Changed-doc hygiene passes: 20 Markdown files, 489 local links/anchors, 15 complete JSON examples; balanced fences and git diff --check pass. The final evidence update preserves these counts. Synthetic demonstrates structural execution and authority boundaries; hosted-model judgement and real external business outcomes are not asserted. The existing frontend bundle-size warning remains. This convergence does not open P10/P11 or move historical milestone freeze SHAs.


## Post-closure consistency and runtime review — 2026-10-07

Reviewed from `ca911d9660af6b1e803c220d140367b3a36e9529` on `main`. The original cutover SHA and hosted evidence above remain historical closure evidence. This bounded follow-up fixes two reproduced UI defects and synchronizes contract/design references; it does not introduce a new runtime or milestone.

- Completed/Expired Automation rows incorrectly disabled deletion even though current-revision backend deletion preserves accepted Runs. Delete now remains available for these terminal sources; edit/enable/run remain unavailable. Two component regressions reproduce the previous failure and pass after the correction.
- Immediate focus restoration raced the confirmation dialog's closing animation and left keyboard focus on the page body. The existing shared `confirmAction` composition now accepts Ant Design's `afterClose` callback; confirmed Automation deletion restores New automation focus after the dialog closes. Cancellation retains the default opener restoration.
- Corrected the backend port's exact latest-run method name, per-Automation Event dedupe key and executable `experience.source`/`experience.record` description. Testing guidance and the shared design context no longer recommend retired Events/Schedules/Thought/Retrospection navigation/labels. Refreshed the design sidecar narrative from the existing context, preserving tokens and component metadata.

Final local checks passed: 17 Automation component tests, three UnifiedAutomationJourney API scenarios, 11 migration/store/tool-authority Infrastructure tests, both browser journeys in unified-event-automation.spec.ts, and TypeScript/Vite production build. The existing bundle-size warning remains. The first new browser test used an incorrect exact outcome-text selector; it was corrected to match the actual model/outcome line, then both scenarios passed. The initial build found an unused test parameter, corrected before the successful build.

Playwright MCP against an isolated native Synthetic/SQLite host exercised a real one-shot activation through quiet NoAction completion, confirmed deletion, empty configuration and retained immutable Run instructions/result after navigation/reload. A final repeated deletion confirmed New automation focus after dialog closure. Console errors: zero; affected API reads/mutations succeeded. The repeatable browser regression verifies the same lifecycle and reload, alongside actual Event ingress/deduplication and exact source navigation. Local verification hosts were stopped; no user database was reset.

Changed-document hygiene passed for five Markdown files and 66 local links/anchors, with balanced fences and no complete JSON examples in these changed files. `git diff --check` and design-sidecar JSON/unchanged token-component checks passed.

Hosted verification for this follow-up is pending the committed candidate. The original five-job green workflow does not prove this later behavior change.


## Chat scheduling semantics and terminology follow-up — 2026-10-07

Reviewed from `fa7bbe4c20ec7bc7c6eeceb31ece1dd928c0b317`. The conversational scheduling prompt incorrectly described every recurring Automation as reminder-only, contradicting the generic durable Run. It now preserves explicitly requested recurring work, distinguishes reminder requests, and states that creation does not authorize future actions: current eligible capabilities, policy and exact-action approvals govern each Run. Immediate execution does not establish a recurring Automation.

`ChatAutomationJourneyTests` submits the requested hourly Experience-review action through SignalR Chat and the real `automation.create` authorization/authoring path. SQLite retains the action Instructions and current-user provenance. A logical hour later the scheduler, occurrence router and durable intake execute the generic Run. The authorized case inspects and consolidates three Experience records with retained lineage and finishes quietly via `work.complete`; the permission-revoked case offers no consolidation tool, changes no Experience and completes quietly with NoAction. Model decisions use a deterministic test adapter; no provider credentials, live inference or external business effects are claimed.

Renamed the remaining current-behavior Thought test files, classes, methods, clocks, variables and fixture provenance to Automation/WorkCompletionRequest. Preserved the destructive-migration historical comment and reasoning-channel “secret-thought” sentinel strings, which do not describe a behavioral resource. Simplified a redundant review-test path while retaining both interval/calendar cases.

Local focused regression: both Chat-authored hourly-action cases pass (authorized and permission revoked). The full Domain suite passes 150 tests and Application passes 1307 with one explicit live-provider skip, including the scheduling-prompt regression. The full local Infrastructure/API suites are still running at candidate publication. Changed-document hygiene passes for seven Markdown files, 240 local links/anchors and eight complete JSON examples; `git diff --check` passes. Full local counts and final hosted exact-SHA evidence will be recorded after completion. Final closure remains pending; the original green workflow and intermediate `fa7bbe4c` workflow are historical evidence only.
