# Tenant-only tool executor cutover — 2026-10-23

This follows `12e9eef8` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It is a shared execution-boundary cutover, not full proposal acceptance.

## Implementation

`SessionToolExecutor` now dispatches only through `TenantToolAuthorization`. Its execution policy refuses absent/unresolved authority, and execution returns a bounded forbidden result without interpreting caller arguments or selecting a generic provider handler. Actual store-resolved tenant ownership, Run/generation, current policy and protected-operation consent remain with the existing owners. Caller-built Definitions, IDs, tenant markers, Harness delegation and configured providers cannot create an execution path.

The generic knowledge, attachment, Workspace, Artifact, web, Email, Drive, Office, Browser and Sandbox handlers and their partials are removed. The constructor falls from 31 dependencies to seven: configuration metadata, Browser metadata, Sandbox lifecycle service/store, working cleanup, tenant authorization and logging. Configuration fallback reads only the supplied immutable Session Definition and its default Skill catalog; it reads no global Instance, binding, credential, resource or budget records and receives no Runtime tool offers. Browser projection supplies host metadata only. Canonical workspace projection uses the actual current tenant Definition; the old global restriction callback is removed.

Canonical attachment image delivery and unknown Workspace outcomes retain their existing result semantics. `WorkspaceSearchLimits` and `WorkspaceGlob` move unchanged into their own file because canonical search/attachment/resource readers use them. Server-owned terminal working cleanup and Sandbox finalization remain in a lifecycle partial, preserving active-execution refusal and generation/execution fencing.

The Runtime no longer probes global credential metadata or workspace ownership, rehydrates unowned captures or preopens an occurrence browser. Actual tenant configuration, image recovery and pinned budgets remain authoritative. The unused public Email chooser, automatic unowned configuration continuation and legacy completion-report admission mailbox are removed; canonical configuration continuation, typed wait, background inspection and report accounting remain. Unowned completion calls refuse before parsing or mutation. Inline Memory proposals likewise return Unavailable without global writes when no tenant Runtime owner exists; the canonical `TenantMemoryService` preparation/commit branch is preserved. This avoids weakening old live-policy checks when retiring the global Definition callback. Raw legacy prompt recall still requires its own retirement pass. No new permission, worker, framework, migration or approval default is introduced.

## Verification and limits

The initial canonical workspace Runtime selection passes **26 cases** in 1 min 46 s: actual HTTP/model dispatch, exact consent, working/home ownership, invalid/missing authority, uncertain or unsettled outcomes and policy reduction. `/private/tmp/tenant-executor-retirement-first-runtime-20261023.log`.

The registry-wide matrix iterates every current `ToolRegistry` descriptor plus an unknown name, using absent authority, caller-built Run/Session/generation and a forged tenant marker. All use a fully allowlisted Definition/configured gate and malformed authority-bearing arguments. It requires denial before argument parsing. A separate case requires no Runtime offers from an unowned context despite caller-provided credentials, workspace and continuity flags.

| Focused group | Observed result |
| --- | --- |
| Application boundary/registry/refusal/projection contracts | **526 cases qualified**: 522 initially pass; all four corrected cases pass in the 64-case registry/refusal recheck. `/private/tmp/tenant-executor-retirement-contracts-20261023.log`, `/private/tmp/tenant-executor-retirement-contract-recheck-20261023.log`. |
| Actual tenant store/dispatcher boundaries | **60 passed**, zero failed; native capture initially skipped pending opt-in. `/private/tmp/tenant-executor-retirement-boundaries-20261023.log`. |
| Explicit native Chromium capture | **2 passed**, zero failed/skipped, in 19 s; both stores use actual masked image creation/publication and exact no-replay/reentry fences. `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`; `/private/tmp/tenant-executor-retirement-native-capture-20261023.log`. |
| Explicit native Browser Runtime/worker | **21 passed**, zero failed/skipped in 3 min 14 s after the scripted assertion correction; `/private/tmp/tenant-executor-retirement-native-runtime-r2-20261023.log`. |
| Grouped actual HTTP/model/worker integration | **131 passed**, zero failed, two native theories skipped until explicit opt-in, in 9 min 38 s; `/private/tmp/tenant-executor-retirement-runtime-20261023.log`. |
| Inline Memory authority and canonical learning | **2 unowned refusal cases and 12 canonical learning cases passed**, zero failed/skipped; `/private/tmp/tenant-executor-retirement-inline-memory-20261023.log`, `/private/tmp/tenant-executor-retirement-tenant-learning-20261023.log`. |
| Serial build | Final solution passes with zero warnings/errors in 66.47 s; `/private/tmp/tenant-executor-retirement-build-r7-20261023.log`. |

**780 distinct focused backend cases are qualified:** 528 Application, 62 Infrastructure and 190 API. The API total includes the 26-case initial workspace group. Overlapping assertion/approval rechecks are counted once. Native opt-in passes supersede the default skipped native theories. No full-suite or proposal acceptance claim is made.

The explicit native API group initially fails all 21 cases before child consent. Improved saved-failure/projection diagnostics isolate a scripted-model assertion against offering `background.start`; the current task contract intentionally offers it under current capability, IAM and exact consent. This fixture never calls it. The stale blanket assertion is removed while direct Browser navigation, Sandbox execution, credential fill and persona branches remain excluded. This changes no product authorization. The isolated approved paths now pass in both stores (2 cases, 25 s; `/private/tmp/tenant-executor-retirement-native-approve-r2-20261023.log`). The complete 21-case native matrix then passes, including rejection/cancellation, current revocation/source/capability changes, exact read Review, capture, original-byte upload/drop, terminal report delivery and actual SQLite host recreation.

The first Application group has four stale assertions: two expected `approval_required` instead of `forbidden`, one expected the previous Session-context message, and one expected a model follow-up tool result despite the new empty interface. Their replacements assert early refusal and no Email offer; unused disconnected provider spies are removed. These are fixture corrections, not relaxed authorization.

All checks use Synthetic providers and disposable data. Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. Each selector term below is prefixed `FullyQualifiedName~`.

```text
Inline Memory: Unowned_inline_proposals_cannot_write_private_memory
Canonical learning: Tenant_learned_memory_actual_HTTP_model_and_receipts_|Tenant_learned_memory_optional_learning_and_recall_|Tenant_learned_memory_current_read_policy_
Application: TenantToolExecutorBoundaryTests|ToolRegistryPolicyTests|ToolApprovalTests|BrowserScreenshotRecoveryTests|SandboxFailureSummaryTests
Infrastructure: Shared_executor_requires_current_generation|Tenant_sensitive_dispatch_|Tenant_knowledge_model_read_|Tenant_workspace_model_|Artifact_verification_|Tenant_Browser_capture_
Native API opt-in: Tenant_background_native_Browser_
API: Model_connected_account_read_|Tenant_background_model_|Tenant_background_completed_child_|Tenant_model_publication_|Artifact_model_HTTP_|Tenant_Office_mutations_|Tenant_Office_model_reads_|Tenant_request_isolation_|Tenant_background_native_Browser_
```

Builds use `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. The first build exposed shared Workspace declarations in a deleted partial; their unchanged canonical consumers are preserved by the extracted file. The module then builds with zero warnings/errors.

## Remaining work

Ninety-seven constructor call sites were adapted to the reduced dependency set. This is compilation evidence only. Positive legacy executor/Runtime journeys using unowned fixtures still require a coverage-mapped migration or retirement pass and are not claimed passing. Pure screenshot projection tests call their underlying projection utility; the removed public Email chooser, legacy completion admission and generic Sandbox attribution tests are retired with their owners. Existing canonical integration groups own current authority/effect evidence.

The complete goal remains active: remaining raw learned-Memory/private context and internal global-service roots, legacy fixture qualification, preparation/schema consolidation, approval-default review and final proposal-wide security, isolation, functional and rendered UI acceptance remain required. No frontend/wire change or new browser UI acceptance is claimed. CI and live providers were not requested or run. User databases and Real hosts are preserved.
