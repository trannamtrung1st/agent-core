# Formal Workflows implementation ledger

Status: **implemented and locally qualified**, 2026-10-29. Proposal baseline `f8449f7566e55f90aa0fe963bdfbf19281277861`; integration base `c556e3bd62e577ae0e3025b96befb4d69470a09d` preserves the intervening roadmap update. The final source commit is recorded below after committing the verified tree. This is focused feature delivery, without a claim of platform milestone acceptance or production deployment.

## Final qualification and self-review

This final section supersedes pending statements and intermediate counts in the chronological checkpoints below. Phases 0–7 are complete within the proposal's single-host, closed-DAG delivery scope. Production Hardening remains the next roadmap initiative.

| Gate | Delivered and observed evidence |
| --- | --- |
| 0: architecture, compatibility and design | Bounded Elsa exception documented before implementation; exact Elsa/SQLite package pins; independent-process bookmark spike; Ant Design direction and shared-component inventory. |
| 1: catalog | Closed graph validation, typed inputs, immutable published versions, retirement, current IAM, revision conflicts and original-key command receipts; both stores and real HTTP exercised. |
| 2: authentic execution and recovery | Trusted WorkflowContinuation admits incumbent Task/Session/Activation/Run; no fabricated source Run or human; persisted Core/Elsa correlation and duplicate admission guards; SQLite reopen and live host restart. |
| 3: proof and control flow | Independent exact Evaluation, separate Goal-owner acceptance, procedural decisions, deterministic branch, mandatory proof, cancellation, expiry and uncertain-effect preservation. |
| 4: invocation | Actual human, claimed-model Run and existing Automation occurrence sources; all twelve origin/store Automation cases run genuine Synthetic workers and Evaluation; existing event/scheduler ownership retained. |
| 5: product UI | Tenant catalog, React Flow designer, keyboard Outline/properties, structured stale comparison, typed launcher, execution evidence, Chat discovery and existing Automation destination; visible browser authoring through completion and rejection. |
| 6: feature hardening | Both-store source retention, revocation, exact decision scope, claim/budget checks, protected-operation approval/rejection, concurrent starts, publication recovery, bookmark fault boundaries and stopped-database backup/restore. |
| 7: design and consistency | Bounded visual fix batch and confirmation at five widths; nested focus/keyboard checks; canonical architecture/API/persistence/operations/roadmap synchronization; scoped design fingerprints; final local link/fence/JSON review. |

Final focused automated results: **150 passed, 0 failed, 0 skipped** across the selections below. These are distinct cases, not a sum of repeated intermediate runs. The 47-case Infrastructure selection preceded the final decision-read fence; the two new both-store reviewer-scope cases then qualified that final change. The HTTP case and frontend selection were rerun against the final source.

| Selection | Result |
| --- | --- |
| Domain `FullyQualifiedName~Workflow` | 36 passed |
| Application `FullyQualifiedName~WorkflowCausation` | 7 passed |
| Infrastructure `FullyQualifiedName~Workflows_` plus incumbent `Tasks_human_cross_Agent_atomic_admission_replay_conflict_messages_and_reopen` | 47 passed |
| Infrastructure `Workflows_assigned_reviewer_requires_current_process_source_read_and_decide_authority` | 2 passed |
| API `FullyQualifiedName~Formal_workflow_HTTP` | 1 passed |
| Frontend eight affected files, sequential | 57 passed |
| TypeScript `tsc --noEmit` | Passed |
| Vite production `build` | Passed; existing large-chunk warnings retained |
| Changed Markdown links/fences/complete JSON examples, scoped design hashes, `git diff --check` | Passed; 1,980 relative links, 26 source fingerprints, zero broken targets |

Commands used for the focused .NET selections follow this pattern, with the project and filter from the table:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Workflows_|FullyQualifiedName~Tasks_human_cross_Agent_atomic_admission_replay_conflict_messages_and_reopen' -m:1 -p:UseSharedCompilation=false -nodeReuse:false --logger 'console;verbosity=minimal'
```

The final frontend selection used Vitest with one worker and file parallelism disabled:

```sh
pnpm --dir web exec vitest run --maxWorkers=1 --no-file-parallelism src/features/workflows/WorkflowRecovery.test.tsx src/features/chat/SessionGoalEvidence.test.tsx src/features/chat/SessionSandboxFrame.test.tsx src/features/chat/TenantSessionSandboxDrawer.test.tsx src/features/chat/TaskCenterDrawer.test.tsx src/features/access/TenantAgentAutomations.test.tsx src/services/tenantWorkflows.test.ts src/app/appRoute.test.ts
pnpm --dir web exec tsc --noEmit
pnpm --dir web build
```

Local logs are `/tmp/agent-core-workflow-final-{domain,application,infrastructure,api,frontend-sequential,types,build}.log`, `/tmp/agent-core-workflow-reviewer-scope.log` and `/tmp/agent-core-workflow-consistency.log`. They are local aids rather than tracked portable artifacts.

### Browser actions, expected and observed outcomes

Playwright MCP used the disposable Synthetic SQLite installation at API 5096/web 5186, with no provider credentials or provider HTTP. Prerequisite agents, grants and Evaluation Suite were explicit fixtures; the process graph itself was authored through visible controls.

| Action | Expected | Observed |
| --- | --- | --- |
| Author, validate, publish and start Start → Task → Evaluation → sign-off → Branch → Success/Failure | Real worker and independent proof precede sign-off; only acceptance reaches success | Version 1 completed with 3/3 proofs; rejection failed with 2/3 retained proofs. |
| Stop/restart the same host/database at sign-off | One Task, Evaluation and bookmark survive; no repeated work | Same execution resumed and completed with one Task, one Evaluation and one consumed bookmark. |
| Save/reopen an existing Automation workflow destination and Run now | Actual occurrence, pinned version, no fake source Session/Run/model | Real worker/Evaluation reached sign-off; acceptance completed 3/3. |
| Concurrent draft edit | Preserve local work and require deliberate reconciliation | Stale refusal retained local fields; structured comparison and explicit base replacement worked. |
| Drop committed save/start replies | Recover the original receipt without duplicate writes | One PUT and one POST respectively; original execution and request key recovered. |
| Drop a save before it commits, restart, read missing receipt and explicitly resume | Literal JSON null exposes deliberate reuse of the frozen command | One revision committed using the original key and business body. |
| Start v2 with required text input containing expression-like text | Treat it as bounded data | Literal input retained; worker/Evaluation reached human wait; cancellation closed the process. |
| Read-only observer, then revoke actual membership | No authoring or separately denied source links; erase forbidden open details | Controls/Task links absent; next poll cleared execution name and steps. |
| Start from a real Chat Session | Preserve source provenance and the Chat composer | Typed launch reached Task/Evaluation/sign-off with original Session link and existing composer. |
| Open Task/Evaluation evidence, then close with Escape | Show exact saved evidence and restore focus one layer at a time | Real result and Passed assessment visible; nested Escape/focus worked without closing the parent. |
| Inspect 320/390/768/1024/1440 widths, keyboard Outline and reduced motion | No horizontal page overflow; usable alternative to canvas dragging | Confirmed after one visual fix batch. A 720×450 CSS viewport at scale 2 checked a 200% layout equivalent. |

The final fresh editor navigation loaded current catalog/editor requests with HTTP 200 and zero console errors/warnings. React development lifecycle cancelled superseded reads (`ERR_ABORTED`); these were not server failures. Earlier logs retain deliberately injected lost replies, stale/denied responses and startup failures. Captures and snapshots remain local under `.playwright-mcp/formal-workflow-*`; the final navigation snapshot is `.playwright-mcp/page-2026-10-28T19-54-00-360Z.yml`.

### Review conclusions and deployment boundary

Self-review traced graph/proof ownership, current source and reviewer scope, original-key recovery, protected-operation continuation, migration guards, source-content retention and nested UI ownership. Findings corrected and rechecked include duplicate canvas selection updates; nested Escape/focus ownership; absent Automation model preference incorrectly becoming Inherit; an empty missing-receipt response; conflated manual Evaluation and operation-approval waits; confidential terminal input/decision retention; and missing current read authority on decision submission. No material high-severity feature defect remains identified by this focused review.

Elsa types remain confined to Infrastructure. No Studio/server endpoints, scripting, scheduler, second execution path or UI kit were added. Native Task-delegation Review cannot be impersonated by the bridge: it refuses `workflow_task_review_required`; actual worker protected operations continue through existing Run approval ownership. Canvas coordinates are local view state; saved graph behavior is explicit nodes/edges/bindings.

Existing installations must stop writers, retain keys/resources and run the documented `migrate-workflows` backup/migration command before starting this release. Six additive Core migrations plus both vendor histories were exercised. The final retention migration was applied to the confirmed-stopped disposable installation with a new backup, all five retained process histories and empty foreign-key checks, followed by normal host restart. Rollback uses the complete matching backup and application; retained workflow facts intentionally prevent unsafe schema downgrade.

Unrun external qualification: hosted CI (not requested), live model/provider and physical voice devices, native browser 200% zoom, real screen-reader output, production deployment, sustained load and multi-host operation. Native keyboard interactions, reduced motion and the documented zoom-layout equivalent were exercised. These limits do not imply production readiness or platform milestone acceptance.

## Chronological implementation checkpoints

The remaining sections record intermediate findings and earlier pending work; the final qualification above is authoritative for delivery status.

## Phase 0

The [architecture contract](../formal-workflows-architecture.md), [technology exception](../10-technology-decisions.md#decision-bounded-embedded-formal-workflows) and [design direction](../../.impeccable/surfaces/formal-workflows.md) establish the implementation boundaries. The UI direction follows the owner-authorized proposal; it has static review only, with no rendered acceptance yet.

Executed a disposable embedded .NET 10 spike with exact package pins `Elsa` 3.9.0, `Elsa.Persistence.EFCore.Sqlite` 3.9.0 and `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12. Inspected vendor source tag 3.9.0 and package manifest before adoption. No full Elsa Server template is used. Automatic activity registration and startup migrations were disabled; the proof registered only a custom waiting activity and code-defined sequence. Explicit migrations initialized separate management/runtime schemas in the same SQLite file with WAL and 30-second default timeout.

Commands: `dotnet restore /tmp/agent-core-workflow-spike/Spike.csproj --disable-parallel`, `dotnet run --project /tmp/agent-core-workflow-spike/Spike.csproj --no-restore -- start /tmp/agent-core-workflow-spike/proof.db`, then a separate `dotnet run --project /tmp/agent-core-workflow-spike/Spike.csproj --no-build -- resume /tmp/agent-core-workflow-spike/proof.db`. Local disposable sources are retained at `local/evidence/formal-workflows/` (ignored evidence, not a production project).

Expected: one stable instance suspends, its exact bookmark survives process disposal, second process resumes to Finished with no bookmarks, duplicate bookmark delivery cannot reopen terminal state. Observed: all assertions passed; first result `Running/Suspended`, second `Finished/Finished`, duplicate retained Finished/no bookmarks. Service-registration assertions found no Elsa CSharp, JavaScript, Liquid, Scheduling or Workflows.Api module. This is an embedded service proof, not an HTTP exposure audit of the future composed host.

Unverified here: simultaneous Core/Elsa writes, Core start/step bridge fault boundaries, trusted workflow Task admission, InMemory parity, tenant isolation, actual authoring/Chat/browser journeys and deployed migration/restore. Subsequent phases must qualify these; the spike does not accept the feature.

## Foundation checks (working tree; not final delivery)

- Focused Domain checks: 39 passed, covering closed graph validation/canonical hashing, immutable versions, typed input rejection/normalization, explicit workflow source and incumbent Task delegation invariants.
- Catalog integration: four cases passed across InMemory/SQLite. Actions saved and retried a draft, validated/published, edited a new draft while retaining v1, refused conflicting/stale/retired publication, reopened saved receipts and checked revoked authentication, independent publish authority and denied catalog counts.
- Closed runtime integration: two cases passed across InMemory/SQLite. Actions compiled the Core graph, suspended, changed the draft without changing the pinned version, reopened SQLite, consumed the exact decision bookmark, refused mismatched/duplicate signals and followed approval/rejection to distinct terminal nodes.
- Later nullable-source regression run: 11 passed / 1 failed. The failure was an incumbent fixture performing retention after its intentional rollback refusal had removed General Assistant columns (`no such column: s.ManagedKind`). The fixture now reapplies current migrations before continuing. That correction and the newer execution-source migration still require the next focused rerun.

The following sections supersede this early foundation checkpoint with exercised bridge, verification and lifecycle results. Agent/Automation invocation, UI, final security/visual review and documentation synchronization remain pending. No feature completion, final SHA, push or hosted CI is claimed.

### Genuine continuation proof

Two focused cases passed across InMemory and SQLite: `Workflows_real_continuation_uses_one_Synthetic_worker_without_forged_source_and_recovers`. The scenario published a graph with a real coordinator, explicit worker/Space and exact Evaluation Suite; accepted an authenticated human start; refused dispatch without coordinator delegation authority; admitted and deduplicated one Task/Session/Activation/Run; executed the actual key-free Synthetic worker through the incumbent Runtime; saved its terminal Task result; reopened Elsa after the worker completed; and consumed the original bookmark into the waiting Verify node. A request to treat Task success as positive verification was refused. SQLite `foreign_key_check` was empty. No provider HTTP was sent.

The first executions found and corrected two integration defects: EF may update the reserved operation before inserting its Task, so the SQL source guard accepts either valid transactional order; and inherited retention originally hid a legitimate standalone workflow worker. Session origins now carry exact workflow/operation references and retain an actual optional original Chat parent for privacy lifetime, rather than fabricating a source Session. These are runtime observations, not final feature acceptance. The later 19-case focused regression below rechecked these fixes.

### Verification and lifecycle evidence (working tree)

The later focused workflow/Task regression passed **19 cases** across both stores, including the corrected incumbent rollback fixture. The focused Domain workflow run passed **35 cases**, including required proof, independent verification of optional Task routes, negative outcomes, typed branch facts, and independent bound Goal success.

The application bridge now records qualified procedural human decisions atomically with their exact step settlement. Four approve/reject cases passed, including forged-proof refusal, stable decision replay/conflict, exact branch selection, an interruption after Elsa consumed a bookmark but before Core acknowledged it, cancellation before runtime/Task admission, expired human decisions and SQLite receipt reopen. The dedicated stopped-database upgrade/restore case passed, preserving the installed tenant and restoring the predecessor backup. Elsa uses separate migration-history names to avoid a collision with Core's migration history.

Six subsequent cases passed: real Synthetic worker → independent Evaluation → mandatory final proof for positive and negative results in both stores, and interrupted publication recovery in both stores. A successful Task waited for manual Evaluation assessment; a failed independent assessment followed the failure route and never became completion. Publication first saves an inaccessible prepared intent, registers the closed definition, then commits the published catalog version. An injected interruption after runtime registration exposed no published version; the retry reused the exact prepared graph/hash and publication timestamp.

One real HTTP host case passed: cookie/CSRF admission, draft save/validation, closed compile/publication, idempotent human start, waiting sign-off, exact decision, bounded recovery and verified completion. Anonymous inspection was refused; the raw vendor workflow endpoint remained absent. Inspection excluded raw source/account/authentication, bookmark and operation internals. This is a key-free InMemory host test; browser authoring and live deployed providers remain unverified.

Two further Goal-owner integration cases passed across both stores: a pending checkpoint held the workflow; checkpoint acceptance still could not substitute for required final Goal acceptance; only the existing Goal owner completed the Goal; and source Session deletion hid the execution from both reads and counts. Reconciliation left Goal state unchanged.

Current work continues on claimed-Agent/Automation sources, UI, final lifecycle/fault qualification and canonical synchronization. These results do not claim the complete proposal, hosted CI, final review, commit or push.

### Claimed Run and Automation integration checkpoint (2026-10-29)

The real claimed-model scenario passed in both stores: explicit grants enabled discovery; a forged claim and insufficient output budget were refused; Synthetic called list/inspect/start twice/status; one saved execution retained its actual Run/Session/principal/original human/root and reserved 16 steps/120 seconds from that root. A separate real Task and independent Evaluation completed the process without refunding the source reservation or sending provider HTTP. Saved workflow tool references repeat current source visibility, with an immutable per-async-flow cycle guard for a source Run inspecting its own worker.

The manual/scheduled Automation checkpoint passed four cases across both stores, plus two catalog/reopen cases. The typed destination produces an occurrence/workflow link with no source Session, Run or model pin. A focused SQLite webhook-to-worker-to-Evaluation scenario subsequently passed after explicit occurrence-before-delivery flush ordering corrected a physical-guard failure. The full twelve-origin/store event matrix is still under qualification; this checkpoint does not claim it passed.

Two source lifecycle cases passed across both stores: an available source could not be falsely closed; deleting its real Session hid reads/counts, committed a technical closure, cancelled the actual waiting Elsa bookmark and expired the process. SQLite reopened the closure. Follow-up receipt-cutoff and retirement/replay assertions are included in the current qualification rerun.

The frozen Automation migration was exercised on a disposable predecessor schema: Up/Down preserved all 221 predecessor triggers (226 after five precise new guards), and both `foreign_key_check` results were empty. Actual EF migration and installed-data restoration remain part of the focused rerun, rather than inferred from this SQL syntax proof.

The subsequent focused invocation qualification passed **19 cases**, including all twelve manual/scheduled/webhook/built-in/immediate/coalesced and InMemory/SQLite combinations, two real claimed-model cases, two source-closure/retirement-replay cases, two catalog/reopen cases and installed-data cutover/restore. Each accepted Automation source ran one genuine Synthetic worker Task and exact independent Evaluation; duplicate receipts/admissions produced one execution and no source Session/Run/model. Command filter: `Workflows_automation_occurrence|Workflows_source_Session|Workflows_actual_claimed|Workflows_catalog_real_human|Workflows_stopped`. Result: 19 passed, 0 failed, 2m31s. This supersedes the still-pending event-matrix statement in the earlier checkpoint.

## Phase 5 browser checkpoint — 2026-10-29 (in progress)

A disposable SQLite Synthetic installation at ports 5096/5186 was bootstrapped under `/tmp/agent-core-formal-workflow-browser-20261029`; no installed tenant was reset. Playwright MCP authored the graph through visible Ant Design controls: Start → real Agent Task → exact Evaluation → assigned process sign-off → recorded-fact Branch → Success/Failure. Prerequisite agents, IAM roles and Suite were explicit API fixtures. Version 1 completed with 3/3 proofs; sign-off rejection produced Failed with 2/3. Restarting the same database while sign-off waited preserved one execution, one Task, one Evaluation and one bookmark; accepting after restart consumed the bookmark and completed without another worker. Nested Task and Evaluation drawers showed their real saved evidence. A duplicate graph selection callback and nested Escape/focus ownership were corrected after these interactions exposed them.

The incumbent Automation editor saved/reopened the exact published workflow destination, then its Run-now control admitted a real occurrence without a source Session, Run or source model. Its real worker/Evaluation reached 2/3 and sign-off completed it. An HTTP mapping defect that converted absent model preference into Inherit was fixed and added to the real API regression. A concurrent draft writer caused a stale refusal; the local draft remained and a structured field comparison preceded deliberate replacement. Dropping the reply after a committed save required only the original read-only receipt (one write). Version 2 added a required text input through the designer; dropping the committed start reply recovered the same execution with one POST, then reached the same evidence gates. Cancellation ended that process and retained factual proof.

Current focused frontend results: Task Center 8/8; workflow model + navigation guard + incumbent Automation editor 33/33; workflow receipt/comparison + incumbent Session sandbox 13/13; nested Escape/focus regression 1/1. The initial combined Task Center run had a timing failure; rerunning that full affected file passed all eight. API author/publish/start/sign-off/readback plus typed Workflow Automation authoring 1/1. TypeScript was clean before the last focused additions and will be rechecked with the final build.

Screenshots and interaction snapshots are local evidence in `.playwright-mcp/formal-workflow-*` and the tool transcript, not portable production qualification. Final visual confirmation, read-only/denied scope, retention/fault hardening, documentation synchronization, source SHA and push remain open. This checkpoint does not claim feature completion.

The UI checkpoint now includes actual 320/390/768/1024/1440 editor and failed-inspector checks without horizontal page overflow, native keyboard Outline selection, reduced motion, and a 720×450 CSS viewport at device scale 2 as a **200% layout equivalent** (not a claim of native browser zoom/screen-reader testing). One bounded visual batch removed a sticky action row that covered labels and separated textual branch outcomes; final screenshots confirmed the five widths. A read-only observer had no authoring controls or unauthorized Task source links; revoking that real membership cleared the open inspector on its next poll. The first observer fixture had a case-normalized login lookup error and the second helper tried to decode a successful empty 204; neither was a product failure. The corrected third fixture exercised revocation successfully.

Chat discovery/start was also exercised from a real saved Session: version 2 accepted typed inputs, retained the original source Session link, reached the real worker/Evaluation/human gate, and kept the existing composer present. Frontend TypeScript and production Vite build pass; incumbent bundle-size warnings remain and no new bundle-budget claim is made. Goal evidence inspection now resolves an explicitly linked Goal by exact ID even when it is outside the first list page. Reviewers and Goal bindings have bounded page controls. Final retention/fault and source-link review still precede feature completion.

## Phase 6 retention and proof-state checkpoint — in progress

Focused both-store execution passed 8 cases (source Session lifecycle plus all genuine worker/verification/repeated-step combinations). The source-bound workflow retained its original input hash and immutable request fingerprint while the incumbent deleted-Session cleanup replaced confidential typed inputs with the one-way `{}` tombstone after technical closure and quiescence. SQLite reopening retained that minimized receipt. The explicit `20261028190100_FormalWorkflowRetention` guard-only migration was checked Up/Down against a snapshot with no foreign-key violations; it performs no table rebuild. Normal execution writes cannot request this terminal-content exception. Task minimization now uses its real workflow original-Session witness, falling back to its own worker Session for standalone workflow Tasks; a focused direct Task-retirement test is being qualified.

A manual Evaluation assessment was incorrectly projecting WaitingForOperationApproval. That is corrected to WaitingForVerification with its own pending-assessment reason; native Sandbox operation approval has a separate reason and retains WaitingForOperationApproval. The real both-store worker/Evaluation cases exercise this distinction. Final fault/concurrency/protected-operation review and canonical documentation synchronization remain open.

Further hardening qualification passed **10 cases**: genuine protected file-operation approval and rejection (both stores), uncertain worker-effect cancellation without replay/minimization (both stores), both sides of Elsa/Core bookmark persistence and signal consumption (four paths), and source-input/decision-rationale minimization (both stores). Six real worker/verification cases plus the two protected-operation cases also passed in the preceding 8-case run, including standalone worker Session deletion and Task request/result minimization. An intermediate Task-retirement assertion accidentally compared `Fingerprint` method delegates; that assertion was corrected to call the methods and the full affected selection passed.

The recovery/concurrency selection then passed **7 cases**: simultaneous same-key human starts produced one logical execution (a losing stale contender can read back that exact receipt); interrupted publication retained its original prepared version/hash even after a current security-revision change; and stopped installed-data cutover/backup restore remained valid. Missing catalog/start recovery receipts now return literal JSON `null`, qualified by the real HTTP regression (1/1). The browser checked an explicitly resumed original command after a pre-commit dropped request; only one draft revision committed. Its pending draft survived the fixture host restart. Stable accessible labels were added to loading recovery actions because an exiting Ant Design loading icon could otherwise temporarily change the button's accessible name under reduced motion.

The first fixture cutover was dispatched before shutdown completion had been confirmed, so it is **not** used as stopped-writer evidence. After confirming host exit/no listener, the matching backup was restored and the additive migration was repeated to a new backup, retaining all five process histories and passing foreign-key checks. The migrated fixture restarted normally. No installed tenant data was reset. The native browser 200% zoom control and a real screen reader remain outside the measured local scope; only the documented layout equivalent and native keyboard interactions were exercised.
