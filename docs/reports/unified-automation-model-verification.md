# Unified Automation model verification

Status: implementation and verification in progress. This report does not close/freeze the change; hosted CI on the final behavior commit is still required. P10/P11 remain unopened. Historical reports and freeze SHAs are unchanged.

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

Frontend regression previously passed 703 tests in 93 files. The final repeat is in progress; one timed-out continuity file is to be rechecked in isolation. TypeScript/Vite production build passes; the existing large-bundle warning remains. Focused final UI/navigation passed 23 tests.

The initial broad browser run passed 93 scenarios and exposed nine stale-contract fixture failures. Corrected targeted runs passed 16 scenarios plus seven isolated affected scenarios, including the new Event journey. The unknown messaging console error did not recur in its focused corrected run. Faithful Manual-A, Admin lifecycle, P7.6 and all seven P9.7 harness scenarios pass. Final continuity and Compose confirmation are pending; the hosted full core/acceptance runs must prove the final candidate.

Playwright MCP also created Schedule/Event Automations and quiet Runs, used a disposable one-time Event Source credential for actual ingress/dedupe, inspected the exact Run and returned to its source. Credentials were cleared and never included in this report.

## Bounded Impeccable finish

Loaded the project design context plus polish and craft-floor guidance. One batched rendered inspection covered 1440, 768 and 390px, populated collection, Event editor, long instructions and shared details. All three measured document width equal to viewport width. Inspected screenshots show shared Ant Design hierarchy, local table scrolling, wrapping actions, readable focused textarea and a 40px mobile Save target. Functional E2E covers empty, validation, stale/error/retry, running, approval and exact source focus. One coherent correction batch added save/delete focus restoration and “On event” for active Event sources; final confirmation verified disabled Save for an empty Name, read-failure reload recovery, empty search, focused editor at 768/390px and exact save/Run source focus. Screenshots were visually inspected; document width stayed equal to viewport width. Approval confirmation remains part of the continuity acceptance run. DESIGN.md now records the verified reusable collection, shared editor and navigation presentation; product behavior stays in canonical docs.

## Remaining gates

Finish the final frontend/continuity/Compose confirmations and exact final behavior SHA hosted Synthetic/Compose CI. Changed-doc hygiene passes: 20 Markdown files, 489 local links/anchors, 15 complete JSON examples; balanced fences and git diff --check pass. Record results and CI URL here before claiming closure. Synthetic demonstrates structural execution and authority boundaries; hosted-model judgement and real external business outcomes are not asserted.
