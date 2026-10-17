# Canonical tenant text Runtime execution verification

Date: 2026-10-17. Baseline: `5a84b87fbdba97e9ce7acc42610b4efe079095bb`. The owner authorized review, fixes, commit and push on the current branch. The [full delivery ledger](multitenant-iam-delivery-2026-10-16.md) remains active; this checkpoint does not accept a milestone or authorize deployment/reset.

## Delivered boundary

The existing hosted worker now invokes a tenant binding of the existing `AgentRunCoordinator`. Its dispatcher creates the existing `SessionRuntime` with actual tenant/Session/Run/generation references, scoped memory/Run adapters and current execution authority. There is no second reasoning engine, independent timer, Task entity or global Profile fallback.

An isolated host registry prevents legacy connection/control lookup from finding these runtimes. A private output sink deliberately supplies no tenant transport delivery. Both registries share the configured active-Session capacity. Runtime supervision waits for accepted work, handles bounded failure, disposes the Runtime and releases the reservation; duplicate dispatch waits for the original acceptance acknowledgement. Shutdown waits for registered runtimes even when initial dispatch has not finished.

Both stores use existing Session/snapshot/entry/Activation/Run rows, with no migration. Actual owner, active executor, Running status, cancellation, generation and unexpired lease fence content reads/saves. Snapshot writes retain revision CAS, same-content retry and entry upserts. Bound Run commands atomically reject a replacement generation, including a stale Runtime attempting to use its successor's fresh revision/generation. Later user input is excluded from the current Run's snapshot and both history directions. Legacy store/recovery scans remain excluded.

Current executor/Instance and original requester Session-read/chat authority are checked before dispatch and model work. Trusted execution references reach initial/rebuilt model context and tool admission. Only the two implemented IAM inspection tools are offered, even if the definition and loaded-capability set contain legacy tools. The tenant factory supplies none of the legacy credential, Profile, workspace, memory, Experience, browser or attachment services.

Ordinary user-turn Runs are the only supported admission class for this binding. Approval-bearing Runs are excluded from ordinary runnable scans and claims after their durable signal requeues them. This prevents generation from completing the Run before its saved typed operation has been consumed. The approved local persona executor remains a separate trusted service pending effect/continuation integration.

New input while a Run is Running returns `session_in_use`, preserving mailbox ownership until live ingress is implemented; exact duplicate replay remains idempotent. Compaction and unadmitted proactive work are fenced pending scoped lifecycle support.

## Executed scenarios

- **HTTP to actual worker:** log in with the disposable Synthetic owner, create an owned examiner and text Session, submit authenticated text, and await the real hosted worker. Expected and observed: a completed actual Run with a persisted response outcome, the original requester/executor/root and no global Profile. Legacy Session lookup returns NotFound. No manual claim substitutes for worker execution in this case.
- **Revoked executor before dispatch:** claim an admitted Run, archive its executor, then call the production dispatcher. Expected and observed: current authority denies, the Run becomes Failed with `authority-unavailable`, and no response result exists.
- **Scoped stores and recovery:** queue two inputs, claim the first, load/read forward/read backward, save/retry/reject a stale save, reopen SQLite, expire the lease, recover/reclaim and attempt a command from the stale Runtime. Expected and observed: later input stays excluded, stale ownership/generation/lease/revision operations fail, same-content retry succeeds and a replaced Runtime cannot adopt the new claim. Concurrent new input is rejected without another Run.
- **Approval processing:** approve or reject an exact persona request, drain its durable signal and reopen the store. Expected and observed: the Run is queued once but remains outside ordinary text dispatch; the saved operation/approval graph is preserved.
- **Shared capacity:** reserve the sole tenant slot, reject legacy attach, release it and attach the legacy Runtime. Expected and observed: tenant reservation is then denied while that Runtime occupies the slot.
- **Tool projection:** give trusted tenant context every definition/loaded tool. Expected and observed: only `iam.check_access` and `iam.get_effective_permissions` are offered; a mismatched Session offers none.

Review also found an older SQLite ownership fixture attempting to modify an EF key. It now performs a direct SQL update and verifies the actual foreign-key rejection, instead of failing before reaching SQLite.

## Focused local checks

All commands use `dotnet test <project> --no-restore --nologo --filter '<filter>'`:

| Project | Filter | Result |
| --- | --- | --- |
| `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` | `TenantRunAdmissionTests`, `TenantSessionIntegrationTests`, `TenantSessionLifecycleTests`, `TenantOperationAuthorizationTests` (FullyQualifiedName OR) | 127 passed |
| `tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj` | `AgentRunCoordinatorTests`, `AgentRunDurabilityTests`, `AgentRunToolCallCheckpointTests`, `CapabilityProjectionTests`, `CapabilityDiscoveryRuntimeTests`, `ApplicationMessageToolPolicyTests` (FullyQualifiedName OR) | 107 passed, plus the added tenant projection case: 1 passed |
| `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` | `TenantSessionAccessTests`, `SessionHostRaceTests`, `AgentRunHostedService` (FullyQualifiedName OR) | 14 passed |
| Infrastructure final guard recheck | `Tenant_runtime_fences_generation_scope_revision`, `Durable_approval_retains_exact_action` (FullyQualifiedName OR) | 6 passed, overlapping the 127 above |
| API final flow recheck | `Canonical_tenant_worker_completes_actual_HTTP_text`, `Canonical_dispatch_rechecks_executor_authority` (FullyQualifiedName OR) | 2 passed, overlapping the 14 above |

Total: 249 distinct focused cases passed, with overlapping guard/flow rechecks listed separately. Logs: `/private/tmp/tenant-runtime-infra-final.log`, `/private/tmp/tenant-runtime-application.log`, `/private/tmp/tenant-runtime-tools.log`, `/private/tmp/tenant-runtime-api-final.log`, `/private/tmp/tenant-runtime-approval-fence.log`, `/private/tmp/tenant-runtime-final-flow.log`.

Documentation checks passed for all ten changed Markdown files: 773 local links, 196 fragments, 20 complete JSON blocks and balanced fences. `git diff --check` passed.

The first broader Infrastructure run found the stale EF-key fixture described above; the corrected 127-case run passed. A sandboxed build stalled and was stopped by exact process identity; subsequent authorized test builds and execution succeeded. No provider credentials, real data reset, unknown-host reuse, deployment, Real restart or CI was used.

## Remaining boundaries

This proves private persisted text execution, not user-visible tenant Chat. Tenant Hub/history/output recipient authorization, delivery receipts, live mailbox ingress/cancellation and audio remain unimplemented. Generated text is not received/heard text. Actual worker/model execution was exercised with InMemory; SQLite has store/transaction/reopen/recovery evidence, not an end-to-end worker-host qualification.

Trusted continuation/autonomous admissions, complete approval/effect consumption, direct Allowed effects, other resource/secret/tool families, full runtime judgment context, summary lifecycle, proactive authority propagation, tenant Core Events/Automation, complete UI and obsolete-authority/schema/reset consolidation remain required. Approval-bearing Runs deliberately await their typed continuation path. This checkpoint does not qualify complete crash recovery, external effects, browser/voice behavior, the complete proposal or milestone acceptance.
