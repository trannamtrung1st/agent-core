# Response-bound approval protocol retirement — 2026-10-23

The canonical tenant consent path was qualified, but the shared Runtime still contained a separate response-bound approval mailbox and grant-resume loop. Its event mapper, ready projection and browser state also retained the old modal protocol. This slice removes that live chain; it does not introduce a replacement execution owner.

## Implemented boundary

- Removed `SessionRuntime.Approval.cs`, the old decision/control facade, `ApprovalResponseReceived`, `ApprovalRequestedOutput`, `PublicPendingApproval` and their response-decision enums. The Runtime no longer constructs a `ToolApprovalGrant`, restores an embedded approval into a live grant, publishes the old event or pauses its tool clock for an inline decision.
- An unowned `RequireApproval` returns `forbidden`/`tenant_context_required`. The representative Runtime case reaches that result, finishes without a waiting event and performs no provider draft read. Canonical tenant review still uses its saved request, exact HTTP decision, current authority and incumbent Run continuation.
- `session.ready` no longer carries `pendingApproval`. Approval-pending progress uses the existing `agent.approval.pending` request/Run reference pair; detail and decision remain separately authorized HTTP operations.
- Removed browser pending-modal state/parser/handler, its composer tooltip flag, the unused `AutomationApprovalPreview` and the old injected-state layout fixture. Unknown old events/ready members cannot reconstruct approval state. Existing canonical saved-action components and Ant Design presentation are reused.

No permission catalog, approval default, grant fence, schema, migration, store mapping or provider adapter changes are made. Retained primitive grant APIs and embedded Run approval records remain separate cleanup work; this is not complete legacy/schema retirement or proposal-wide acceptance.

## Retired fixtures and retained behavior

| Retired assumption | Current evidence |
| --- | --- |
| Response-local approve/reject, modal replay, callback ACK and inline human-wait clocks | `ToolApprovalTests` now exercises fail-closed missing tenant context; canonical original-conversation recovery, exact consent outcomes and machine-budget suites exercise the actual owners. |
| Unowned Automation approval/resume and fresh configuration callback | Canonical model Automation and configuration-continuation cases qualify exact decisions, current pins, independent work and SQLite recreation. Pure timezone/classifier assertions are retained. |
| Automatic fake rejection output and routing through the removed global occurrence router | Fixtures capture outputs without issuing decisions. The classifier fixture stops at semantic/persisted-plan readback; actual execution is qualified through the tenant worker. No retired router registration was restored. |
| Unowned optional `SelfManagementLiveTests` | Removed with its retired decision API; no current live-provider acceptance is claimed. |
| Pending-modal compaction branch and injected browser layout | Short-history compaction and unrelated Composer cases are retained. Canonical saved-action browser journeys and real pending/rejected reload replace the obsolete presentation assumption. |

## Executed local checks

Backend selections use `dotnet test <project> --no-build --no-restore --nologo --filter <selection>`. The selections below identify the exact classes/method prefixes; multiple entries use `FullyQualifiedName~…` joined by `|`.

| Group | Final result and evidence |
| --- | --- |
| Application `ToolApprovalTests`, `CompactionLifecycleTests` | **12 passed**; `/private/tmp/live-approval-retirement-runtime-20261023-r1.log`. |
| API `SessionEventMapperApprovalTests`, `Original_conversation_receives_saved_wait_and_recovers_decision_after_independent_work` | **6 passed**; `/private/tmp/live-approval-retirement-conversation-20261023-r1.log`. |
| API real JavaScript JSON/MessagePack, model manual invocation/recreation, model mutation and `Canonical_configuration_` | **27 passed**; `/private/tmp/live-approval-retirement-canonical-regressions-20261023.log`. Real wire filters were `Tenant_Hub_real_JavaScript` and `Tenant_live_MessagePack_JavaScript`; model filters selected saved-conversation invocation, pending-consent recreation and create/inspect/edit/enable/disable completion. |
| API retained semantic/timezone and projection fixtures | **32 qualified passes**: 28 initially passed; four reached a retired router. The adjusted nine-case calendar method passed, including the four corrections. `/private/tmp/live-approval-retirement-remaining-fixtures-20261023.log`, `/private/tmp/live-approval-retirement-calendar-recheck-20261023.log`. |
| Application `ToolExecutionBudgetTests`, `ExecutionBudgetRuntimeTests`, `RunBudgetDiagnosticsTests`, `CheckpointBudgetMeasurementTests` | **24 passed**; `/private/tmp/live-approval-retirement-budget-regressions-20261023.log`. |
| API independent authoring, exact consent outcomes and event recovery | **30 qualified passes**: 29 initially passed; one retry fixture raced its consumed fault flag. Its scenario now controls clock handling independently. All **20 event cases passed** in the recheck; the other ten passes are retained. `/private/tmp/live-approval-retirement-independent-regressions-20261023.log`, `/private/tmp/live-approval-retirement-event-recheck-20261023.log`. |
| Frontend state, Composer, history and canonical inspector | **224 qualified passes**. The original four-file run had 199 passes and three failures caused by an overly broad test edit and an incorrect orphan-response expectation. Unrelated Composer helpers/cases were restored; only the obsolete modal test was removed. The two affected files passed all 83 cases; unchanged history/inspector retain 11/130 passes. `/private/tmp/live-approval-retirement-web-focused-20261023-r1.log`, `/private/tmp/live-approval-retirement-web-rechecks-20261023-r2.log`. |
| Solution build | **Passed**, zero warnings/errors, 11.65 s; `/private/tmp/live-approval-retirement-build-20261023-r5.log`. |
| Frontend build | **Passed**; `/private/tmp/live-approval-retirement-web-build-20261023.log`. Vite reports large chunks; Vitest also emitted jsdom height warnings. Neither is presented as a full performance/accessibility qualification. |

The backend rows qualify **131 distinct current cases**; correction rows overlap their original batches. Passing unrelated cases were retained instead of rerunning whole suites after fixture-only fixes. The last build/event recheck changes only the event fixture, so the browser run below uses the same production source. The new fault-flag race does not establish the cause of the older unexplained event stall recorded in the delivery ledger.

## Browser and rendered checks

**32 passed** in 2.3 minutes: 24 compound Harness/configuration/Skill/Resource inspections, four exact Automation creation decisions and four manual-occurrence admission decisions, with approve/reject at 1440/390 px. Visible Chat, exact saved-action review, confirmation, factual outcomes and canonical readback remained functional with no old pending state. Log: `/private/tmp/live-approval-retirement-browser-20261023.log`; captures/traces: `/private/tmp/live-approval-retirement-ui-20261023-aS0euY/results`.

The command from `web/` was `CI=1 NODE_OPTIONS=--no-experimental-webstorage TENANT_CHAT_API_PORT=5114 TENANT_CHAT_WEB_PORT=5194 TENANT_CHAT_TEST_RUN_ROOT=/private/tmp/live-approval-retirement-ui-20261023-aS0euY pnpm exec playwright test --config playwright.live-approval-retirement-temporary.config.ts`. The removed disposable override imported `playwright.tenant-chat.config.ts`, selected the three current management-inspection/Automation files and disabled runner-owned web servers. Both explicitly configured hosts were checked free before startup, used the Synthetic profile and private roots, and were stopped after verification.

Playwright MCP independently sent a private Skill metadata request, observed pending consent with an enabled composer and no `pendingApproval` state member, then reloaded at phone width. The same saved request reappeared. The drawer rendered literal markup as text, withheld the private procedure and contained no script node. Escape closed it and returned focus to **Review details**. Reopening and confirming rejection with Enter produced **Rejected** and the factual refusal; both survived another reload, with no live response or old pending state. Console errors were zero.

One bounded rendered round checked 320/390/768/1024/1440 px: no horizontal document overflow and both decision controls were visible at every width. Current desktop/phone captures were inspected. No visual corrections were needed. The targeted Impeccable detector reported no findings for `Composer.tsx`/`ChatApp.tsx`; tokens, design context, surface briefs and sidecar are unchanged. The sidecar SHA-256 remains `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`.

CI, live providers, physical devices, a full WCAG/performance audit and complete proposal acceptance were not run or claimed. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) owns remaining integration, primitive grant/model/schema cleanup and the final tool/store authority audit.
