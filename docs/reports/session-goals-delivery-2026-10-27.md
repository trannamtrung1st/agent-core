# Session Goals delivery — 2026-10-27

Status: **Initiative 8 closed for its verified local scope.** Deployment, hosted CI and live-provider certification are separate. Initiative 9 (Task Delegation) and Group Chat are not started.

## Scope and ownership

Implemented Initiative 8 from the owner-approved final proposal on baseline `9f5c22bcd0b5bb2c376b2a9f65c9b953bf5d091f`. The qualified implementation commit and source fingerprint are recorded in the source section. Historical milestone reports retain their original scope.

Explicit Goals belong to their tenant Session and creating Space, with bounded accepted plans, stable ordered checkpoints, independently qualified evidence, immutable activity and one Session focus. Human APIs and four claimed model tools share authority and transitions. Ordinary text/voice is unbound; deliberate Work uses existing UserTurn admission and a frozen Goal/checkpoint/plan/Space/focus witness. GoalContinuation uses existing Activation/AgentRun lineage and coordinator, not another scheduler. Focus persists through the Session mailbox writer. Goal completion leaves Session lifecycle independent.

Plans support 1–40 checkpoints, at most eight earlier dependencies and explicit all-of verification methods. Required outcomes cannot be skipped. Material changes require current qualified human acceptance; verified facts remain immutable. Evaluation evidence pins the exact saved Suite version/hash and terminal Run revision/fingerprint. Artifact receipts prove publication only. Human acceptance is attributable and cannot be forged by model arguments. Optional automatic continuation is off by default, at most five total successors and a human-approved absolute horizon within 24 hours; durable allowance survives restart and never resets.

Canonical ownership: [architecture](../03-system-architecture.md#session-goals), [ports](../04-backend-interfaces.md#session-goal-ports), [backend](../12-backend-implementation-spec.md#session-goal-implementation), [frontend](../13-frontend-implementation-spec.md#session-goals-in-chat-and-activity), [HTTP](../14-api-and-realtime-protocol.md#tenant-session-goal-http-contract), [schema/cutover](../15-persistence-and-configuration.md#session-goal-persistence-and-cutover), [testing](../16-testing-strategy.md#session-goal-focused-qualification) and [operations](../17-observability-and-operations.md#session-goal-operations).

## Observed verification

All runs were local and focused. VSTest and Chromium required macOS loopback/process permissions; initial sandbox launch failures were environment failures, not product passes. No GitHub Actions were dispatched, rerun or awaited. Native proof reused the existing authenticated local deployment without restart/reset.

| Check | Observed result |
| --- | --- |
| Domain Goal rules, Session lifecycle and affected Run rules | 18 passed |
| Application context trust, completion and realtime lifecycle group | 46 passed |
| Shared tool registry and explicit permission approval catalog | 171 passed after synchronizing the closed catalog fixture, including preceding Script/Evaluation entries and new Goal permissions |
| Goal integration matrix, staged InMemory/SQLite runs | 24 offline cases passed; targeted follow-ups rechecked changed source/evidence, tools, permission and migration paths |
| Existing startup and Run-content retention regression group | 10 passed |
| Supported predecessor startup refusal plus forward conversion/refusal | 8 passed including six startup regression cases; two migration cases overlap the Goal matrix |
| Native Goal proof on real OpenSandbox and SQLite | 3 passed: Passed, Failed followed by reviewed repair, timeout saved Inconclusive |
| Web service/Activity/Run-presentation tests | 20 passed in three files |
| TypeScript and production Vite build | Passed; existing large-chunk advisory remains |
| Real Synthetic SQLite browser journey | Passed, including delayed-response privacy regression; committed-build host replay passed in 19.2 seconds |
| Impeccable detector on four changed UI targets | One scan, empty findings |
| Whitespace and documentation checks | Recorded in completion below |

Principal commands (filters deliberately exclude the full CI suite):

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Goals_' --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~CompletionEvaluatorTests|FullyQualifiedName~SessionRuntimeCompletionTests|FullyQualifiedName~SessionRealtimeLifecycleTests' --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~PermissionApprovalFloorTests' --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-restore --filter 'FullyQualifiedName~GoalRulesTests|FullyQualifiedName~SessionLifecycleTests|FullyQualifiedName~AgentRunTests|FullyQualifiedName~TrustedExecutionContext' --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
# From web/, with the separate task-owned Synthetic API/SPA already running:
pnpm exec vitest run src/services/tenantSessionGoals.test.ts src/services/tenantActivity.test.ts src/features/chat/runPresentation.test.ts
pnpm exec playwright test --config playwright.session-goals.config.ts
pnpm run build
git diff --check
```

The native methods are explicitly opted in using `AGENTCORE_OPENSANDBOX_TESTS=1` and trusted `AGENTCORE_OSB_PROOF_URL`, `AGENTCORE_OSB_PROOF_KEY`, `AGENTCORE_OSB_PROOF_MOUNT_ROOT`, `AGENTCORE_OSB_PROOF_IMAGE`. Keys were read from the installed deployment without printing them. The installed immutable image was `sha256:985c3477b1bcf54385a33c048e465ed6bf7f590d09e9dd4bbfae06d62af91561`; each fixture used its own UUID namespace under the existing allowed canonical root.

### Backend behavior

- A real completed Synthetic Run plus exact independent saved Evaluation satisfies the accepted criterion. A Passed attempt from an unaccepted Suite, a mismatched Run reference or missing source read permission leaves the Goal unverified or denies the reference. Publication is consumed through the existing owning Artifact operation/store; its receipt alone cannot satisfy an independent Evaluation requirement.
- Actual claimed model tools read, propose a new Proposed Goal, report a bound blocker and replay one command receipt. Forged generation, forged human acceptance and current Definition capability removal deny. Protected pagination filters readable Goals before exposing `hasMore`. No global provider was used for Synthetic execution.
- Both stores enforce human/tenant/Session/Space scope, read-only denial, CAS race winner, material amendment review, authenticated acceptance and logout denial. Confirmed creation requires `goals.manage` as well as `goals.accept`; acceptance permission does not create objectives by itself.
- Live SessionRuntime focus changes persist through later Run writes. Running work keeps its original Goal witness; a later normal human message has no Goal binding. Queued Goal continuations cancel before claim after pause or new human input.
- Real Goal successor Activation/Run admission uses the incumbent runtime and causal root. Duplicate observation/request replay creates no second successor. Fresh SQLite store reads retain the admitted child and total allowance. Off, pause and human-input boundaries admit none. Candidate selection uses the checkpoint's actual saved source and excludes consumed parents; bounded SQLite scans rotate past temporarily ineligible Goals.
- Exact Temporary due time hides Goal reads/activity before physical cleanup. Existing Session retirement removes private Goal/checkpoint/activity rows and retires Run task prose. The Saved lifecycle/retention regression remains passing.

### Native failure and repair

A reviewed Script checks immutable completed Run metadata in a fresh environment. Its saved Passed attempt verifies only the bound checkpoint. An `exit 7` result is saved Failed and cannot verify. The repair saves a new Script/Suite version, proposes a revised exact policy, obtains explicit human acceptance, admits a new bound Run and executes a new independent Evaluation. Only its Passed receipt completes the Goal; the original attempt remains Failed.

A separate real timed-out Script is saved Inconclusive and leaves the checkpoint Verification pending. Pending approval evidence also stays unverified. Repeated Goal verification and Evaluation observation do not rerun native work. Assertions observed one evaluation command in the pass case, two total across failure plus explicit repair, and one in the timeout case. Every final receipt confirmed termination with no cleanup pending. Profile verification qualified setup separately; it was never treated as checkpoint evidence.

### Browser and finish review

The integrated browser test creates protected fixtures through authenticated HTTP and uses real Ant Design controls: empty state → proposed plan → human start → focus → deliberate bound Synthetic Run → exact saved Evaluation → human criterion/final acceptance → Goal Completed while Session remains Active. It inspects source evidence and follows the Run-to-Goal deep link. It then exercises multiple Goals, 40 long checkpoints, pause/resume, a blocker, retained draft after 409, successful amendment proposal and rejection, failed-network recovery, denied reads clearing content and reload.

A deterministic privacy regression holds a factual Run inspector response, denies the Goal refresh, then releases that response after private content is retired. The Run drawer remains absent. Scope retirement advances the epoch, aborts pending reads and clears every inspector/draft/action/activity state; late callbacks cannot reopen retired private data.

Playwright MCP independently exercised real human-only completion and a five-checkpoint blocked plan. After restarting the committed API build against retained fixtures, the same focused blocked Goal and its saved attributable activity reloaded correctly. Settled layouts were checked at 320/390/768/1024/1440; Escape returns focus to the Goals trigger. Reduced motion and 200% CSS layout zoom were exercised. This is not physical-device/browser chrome zoom certification.

The [design-first surface contract](../../.impeccable/surfaces/session-goals.md) preceded UI implementation. Baseline inspection used source and recent rendered incumbent evidence while migration tooling was being repaired; it is not claimed as a live pre-change screenshot. A distinct bounded polish pass followed integrated behavior. Valid captures under `.impeccable/review/` cover all five widths, desktop/mobile editor and CSS layout zoom. Own fixes made the mobile selector full width, added accessible evidence names, preserved unopened checkpoints and immutable Suite pins, displayed exact policy witnesses and removed terminal editing controls.

The fresh shipped Impeccable reviewer returned **fix** for one late-private-inspector race. Its verdict pass scored that fix **resolved**, disposition **ship**, after the delayed-response test and eight validated recaptures. That verdict covers the listed correction, not a new whole-application review. The documenter compared source and incumbent system, recorded reuse in the surface brief and found no material mismatch. DESIGN.md and its sidecar are preserved; preexisting stale sidecar/two orphan briefs were reported without unrelated repair. No raster assets were introduced.

## Offline conversion and installed inventory

`20261027040000_SessionGoals` follows actual predecessor `20261027020000_EvaluationSandboxExecutions`; historical migrations are unchanged. Tests create the actual predecessor, encode a legacy Goal-purpose description/deadline/metadata, copy a SQLite backup, then migrate. The result is Proposed and unverified in the true tenant Session/Space, with original deadline/metadata preserved and Session purpose normalized to Ongoing. An unmappable source aborts atomically with the named repair constraint. The supported predecessor refuses ordinary startup with explicit backup/offline-migration guidance. Down migration refuses once Goal data exists; a copied matching backup is the lossless rollback.

Read-only local inventory found `data/native/core.db`, schema head `20261023190000_TenantPublicHttp`, with zero SessionSnapshot rows and zero Goal-purpose records. A read-consistent copy was written to `/tmp/session-goals-installed-inventory-backup.db`. That older installed database was neither migrated nor reset. Other machines/customer backups were not available and are not certified by this inventory. The qualification API used a separate database and protected roots under `/tmp/agent-core-session-goals-20261027-v2`.

## Self-review and limits

Material findings resolved during implementation: untrusted Goal task role/manifest handling; new-origin attachment and SQLite Core-event guards; expression-index startup inspection; admission/claim intent boundaries; equal-time/consumed-parent selection and scan fairness; current-source evidence qualification; confirmed-create permission separation; collapsed checkpoint omission; exact Suite pin preservation; accessible field names; same-Session Goal deep links; private late-response retirement. Focused checks verify each affected behavior. The shared permission catalog fixture was also synchronized rather than weakening its closed-list assertions.

Local readiness does not certify production data conversion, customer backup restoration, live model quality/cost, physical microphone/speaker/device behavior, browser-engine breadth, or hosted CI. Existing native BeforeRun/Publication Script restrictions and platform isolation limits remain unchanged. This initiative does not implement Task Delegation, Group Chat, a new execution engine or a second UI kit.

## Source and completion

The qualified implementation starts at baseline `9f5c22bcd0b5bb2c376b2a9f65c9b953bf5d091f`. A sorted manifest of {path,sha256} for the 97 changed/new implementation and test files under src/tests/web has SHA-256 `87a9b7ae8340fc7f1f0e927cfbac7c1df5d512997c4adfca98c98cfe3d6bd748` (compact JSON, sorted keys). The local manifest is `/tmp/session-goals-source-manifest.json`. Qualified implementation commit: `b2ab0d21d34451a645b7dfa0951e4aeabb914667`. All 97 manifest entries were checked against that commit with matching hashes. The following documentation-only handoff commit records this immutable identity without changing implementation or tests.

Documentation checks passed: 14 changed Markdown documents, 1800 relative local links/anchors, balanced fences and the complete new Goal creation JSON example. `git diff --check` passed. README/TODO/implementation plan agree on Initiative 8 and retain historical milestone scope. The final local verdict is **Ready** with the explicit external limits above; no material finding remains in the requested implementation scope. No further code refinement is planned.

## Focused stabilization after implementation review

The owner review of `4330b0238c251195701092c01e9e91921b4b2ec2` identified three reachable transition defects. This follow-up corrects those defects only, without a schema change, new scheduler or architecture redesign. The earlier delivery evidence above describes its original implementation commit; the following evidence supersedes its transition-closure claim.

- Initial acceptance remains available for a Proposed Goal. Accept/reject decisions on an Active or Paused Goal require an actual pending revision. Rejected decisions leave security/Goal/plan revisions, progress, blockers, pending evidence and immutable activity unchanged. Original request-key replay still returns its saved receipt.
- A Verified checkpoint keeps its successful evidence, source Run, note and status. A different failed/stale/missing reference is rejected before replacement. A fresh request for the identical saved nonhuman proof records an audited recheck without altering the checkpoint; replay of that same request key adds nothing. Current loss of source access cannot erase the historical fact. Final Goal completion continues to qualify required saved sources against current authority. No implicit evidence invalidation was added.
- Optional checkpoints cannot be skipped while they are direct or transitive prerequisites of an unfinished required outcome. Independent optional work remains skippable. The UI disables protected Skip with a reason, removes replacement inputs from Verified rows and offers a saved-proof recheck.

The new store regressions reproduced the defects before correction. The initial proof fixture used a predicate unsupported for Run subjects and correctly produced Inconclusive; it was corrected to an actual independently assessed Failed Evaluation. The corrected proof regression then reproduced the overwrite on both stores. After the fix, **8 new InMemory/SQLite cases passed**, and the complete focused offline Goal group passed **32 cases**. Tests cover Active/Paused repeated decisions, accepted-command replay, progress/blocker/pending-evidence retention, exact successful proof, actual Failed replacement, stale/missing references, source-read denial, durable SQLite reopen, direct/transitive skip restrictions and successful required delivery after independent optional work is skipped.

Focused checks run for this follow-up:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Goals_stabilization' --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Goals_&FullyQualifiedName!~Goals_native' --nologo
dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~GoalRulesTests' --nologo
# From web/:
pnpm exec vitest run src/features/chat/goalCheckpoints.test.ts src/services/tenantSessionGoals.test.ts
pnpm exec playwright test --config playwright.session-goals.config.ts
pnpm run build
```

Domain validation passed (1 case); focused frontend tests passed (6 cases). Backend build passed with zero warnings/errors and the frontend production build passed with the existing chunk-size advisory. The final combined browser run passed both journeys (2 cases, 31.3 seconds). The new stabilization journey passed after correcting a test locator that matched both displayed progress and the retained textbox. It exercises real protected HTTP and Ant Design controls: rejects invalid repeated plan decisions without losing the blocker, disables transitive/direct prerequisite Skip, skips an independent appendix, verifies prerequisites, executes a bound Synthetic Run, consumes exact saved Evaluation evidence, rechecks its unchanged proof, rejects a replacement and completes the Goal while the Session stays Active.

Playwright MCP additionally exercised the changed controls in the same running Synthetic SQLite app: protected Skip was disabled with the expected explanation; an independent optional checkpoint was skipped; human prerequisites were accepted; a real bound Run completed; its exact saved Evaluation passed; verifying and rechecking left the checkpoint byte-for-byte unchanged and replacement inputs absent. Reload retained Verified status. The drawer stayed within the viewport at 320 and 1440 pixels. Existing Ant Design controls, typography, spacing and Drawer ownership were reused; no system tokens or visual identity changed. The design hook reported no deterministic issue; the preexisting stale sidecar remains outside this focused scope.

Native Sandbox, physical-device/live-provider checks and optional CI were not rerun: the correction changes transitions and UI commands, and the focused tests consume actual saved Evaluation results without new native execution. No production data was migrated or reset. No remaining material finding is known for the three reviewed paths; Initiative 8 can close after this stabilization, and Initiative 9 is not started by this commit.

Stabilization source/test fingerprint: `4aebfffff2388701562b6e212d46f73a49f5bc295a657a4b8649f96eb3f6e19f` for the 8 changed/new implementation and test files (sorted {path,sha256} manifest, compact JSON with sorted keys). Local manifest: `/tmp/session-goals-stabilization-source-manifest.json`. Relative links/anchors, balanced fences and `git diff --check` passed.

## Initiative 8 closure

The owner accepted the transition stabilization at `c10beaf42e2a120ef71ba4869ee6b0b23d0716d5` and requested closure after the final wording refinement. **Initiative 8 — Session Goals is closed for its verified local scope.** Initiative 9 — Task Delegation & Communication is next and remains unstarted. No further discretionary Session Goals refinement is planned.

The final refinement renames the Verified-checkpoint action to **Record evidence audit** and explicitly states that it audits the saved reference without revalidating its source. The same label applies to saved Evaluation and receipt evidence. This replaces the earlier UI “Recheck” terminology in this report; historical verification results remain tied to their original source. The backend command, immutable proof, audit semantics and final completion qualification are unchanged. Canonical ports/frontend specifications, README, TODO, roadmap and implementation status now agree.

Focused verification for this wording-only closure:

- `pnpm exec playwright test --config playwright.session-goals.config.ts session-goals-stabilization.spec.ts` from `web/`: **1 passed** (15.2 seconds). The actual Synthetic SQLite journey displayed the new explanatory copy, recorded the audit with unchanged saved checkpoint evidence, rejected evidence replacement and completed the Goal while the Session stayed Active. It also retained the repeated-decision and prerequisite-skip regression checks.
- Playwright MCP independently opened a retained task-owned Synthetic Goal with a completed Run and exact Passed Evaluation. Clicking Record evidence audit returned HTTP 200 and advanced the Goal revision from 9 to 10 while preserving the entire Verified checkpoint byte-for-byte. The changed action/copy fit at 320 and 1440 pixels without horizontal document overflow; the existing Ant Design Drawer, Flex and Typography spacing owners remain unchanged. No page errors or failed requests occurred during that interaction. Initial navigation encountered an expired verification login; signing in again restored access before the check.
- `pnpm run build` from `web/`: **passed**, including TypeScript; the existing large-chunk advisory remains. `git diff --check` and changed-document relative link/anchor and fence checks passed.

The prior 32-case backend Goal matrix, 6 frontend cases, 2 browser journeys and original native qualification remain the accepted baseline; they were not all rerun for this text-only change. No backend behavior, migration, scheduler or execution owner changed. Hosted CI, external deployment, live-provider certification and physical-device checks remain outside this local closure. The user review found no remaining critical/high-severity issue, and the final wording concern is resolved without expanding scope.
