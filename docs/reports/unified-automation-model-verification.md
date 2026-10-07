# Unified Automation model verification

Status: complete and closed on behavior commit `60dda1dff150e1f13134ec53881e9a31548b9d73` (2026-10-07). All five hosted Synthetic/Compose jobs are green on that exact SHA. P10/P11 remain unopened. Historical reports and freeze SHAs are unchanged.

Starting baseline: `fcd2090e65a4bbcdc08b4fdaf1f5cce8ef9d187c`. Final verified behavior commit: `60dda1dff150e1f13134ec53881e9a31548b9d73`. Implementation was committed directly to `main` and pushed after explicit owner approval. This report is updated separately as documentation-only closure evidence.

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
