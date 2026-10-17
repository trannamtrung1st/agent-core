# Exact approved persona effects — 2026-10-17

Baseline: `b6dff54368adfbd89f1a8cafee647dec89fb8ad7`. The full tenant IAM/approval goal remains active. This successor implements the trusted original-Run local persona effect and durable result, not production tenant model/worker/tool dispatch or the complete UI/resource cutover.

## Implemented behavior

`TenantApprovedOperationService.ExecutePersonaAsync` receives trusted Run/request/operation/generation/hash references. It loads the saved exact persona, target, witnesses, human decision and optional exceptional grant. Callers cannot replace arguments, select another executor/human or extend authority to a whole Run. The exact operation still needs current tenant/executor/requester eligibility, current human grantor action/approve/exception authority, current independent-review policy and pinned/live Settings management permission. Freeze, disabled management, an expired approval/grant or a changed target cannot be overridden by human consent.

Both stores commit the actual Instance persona/revisions, immutable operation receipt, consumed exact exceptional grant (or no grant for ordinary Review), Run effect checkpoint, tenant security revision and audit together. The live Run generation and lease are checked at commit. Global grantor Account status is also checked inside the transaction, because suspension need not change a tenant revision. A failed revalidation records a stable denied receipt and revokes the available exact grant without changing the persona or prematurely failing unrelated Run work. Wrong hash/operation/tenant/generation never consumes the legitimate grant.

The immutable receipt supplies local at-most-once behavior across restart, lost acknowledgment and simultaneous callers. Replaying the exact operation returns its saved result without another configuration revision, grant, checkpoint or audit. The operation remains the same saved action; a denial is not adapted to new arguments. An uncertain side effect or missing completion receipt cannot authorize replay. This uses a local atomic transaction; no exactly-once external-provider claim is made.

`20261017024144_TenantApprovedLocalOperationReceipts` adds the normalized receipt table. Its primary operation key and unique request result combine with restrictive exact tenant/request/executor/operation/hash, root/target and actual Run/Session/executor foreign keys. Receipt content is status/revisions/time, not persona bytes, credentials or a bearer capability. No Task, scheduler, public execution endpoint or second runtime is introduced.

Canonical approval read/list now expose nullable safe decision attribution and operation result separately from the historical human consent status. Fresh human detail authorization follows metadata I/O. An approved request may therefore have no result yet, a completed result or a precise denied result. Grant IDs and effect idempotency authority are not projected.

Canonical tenant configuration writes no longer emit `CoreEventPersistence.Instance` events using a fabricated legacy `LocalUserProfile` owner. They retain actual tenant audit/receipts. Non-tenant Instance event behavior remains intact; tenant-aware Core Event ownership/delivery remains a required cutover boundary.

## Runtime evidence

Four initial cases exercised actual effect mutation in InMemory and temporary SQLite, for ordinary Review and ExceptionalGrant. They enable Settings management through an actual owned Instance configuration, admit a new actual tenant Session/Run with those pins, prepare and approve the exact operation, sign out the human, drain the durable signal and execute under a saved live domain claim. Assertions inspect the actual persona and revisions, Run effect checkpoint/attempt count, consumed grant or no grant, one audit and identical receipt after SQLite reopen/replay. They also assert no fabricated legacy configuration event.

The first SQLite run exposed a real transactional bug: the owning-resource query returned an untracked Instance, so the receipt/grant committed while persona changes were not saved. The corrected adapter attaches that actual row before mutation. All four integrated cases then passed; no expected outcome was weakened.

Twelve additional cases passed grantor/executor authority loss, live freeze, stale persona witnesses, disabled pinned management and forged execution references. They inspect a durable denial/revoked grant with no persona overwrite, or confirm a forged caller leaves the legitimate grant available for its exact authorized execution.

Twelve delayed-commit cases passed Account suspension without tenant revision, role revocation, live lease expiry, cancellation, competing exact execution and grant expiry under a recovered live claim. Account suspension becomes a durable denial/revocation. Stale IAM or expired worker claims cannot commit. Concurrent callers observe the same one saved effect. The expiry case uses the existing domain recovery transition and a new valid lease; an expired old grant is still revoked without applying the persona.

Raw SQLite checks reject forged receipt action hash, executor and root, preserving the saved result/persona. The Synthetic HTTP journey uses the canonical management, Session/Run admission, exact human approval and protected result read/list routes around the internal effect invocation. It verifies decision attribution and completed receipt without returning grant authority.

The fixture persists `AgentRun.TakeClaim` (and recovery where relevant) as a domain/store contract before invoking the internal service. This is real effect-service/store/HTTP evidence, not proof that the production tenant worker already claims or dispatches model work. No debug HTTP execution endpoint was added.

## Focused verification

All `dotnet test` commands used `--no-restore --nologo` and `FullyQualifiedName~` for each filter. All execution used disposable Synthetic/in-memory/SQLite fixtures; no provider credentials or calls were required.

| Project/check | Filters and observed result |
| --- | --- |
| Infrastructure | `TenantOperationAuthorizationTests\|TenantRunAdmissionTests\|IamIdentityIntegrationTests\|CanonicalStartupMigrationTests\|CanonicalSchemaRefusalTests`: 121 passed; `/private/tmp/tenant-persona-effect-infrastructure-final.log`. Includes all 29 new effect workflow/boundary/race/schema cases and the affected existing foundation. |
| API | `TenantSessionAccessTests\|Committed_config_fans_out_only_to_owner\|SelfManagementOperationTests`: 14 passed; `/private/tmp/tenant-persona-effect-api-final.log`. Canonical result/decision projection and protected management/approval journeys pass; legacy configuration-event fanout/ownership/reopen remains functional. |
| Application | `AgentRunDurabilityTests\|ToolRegistryPolicyTests\|SessionRuntimeCompletionTests\|BackgroundCompletion`: 69 passed; `/private/tmp/tenant-persona-effect-application-final.log`. Existing retry/completion and tool-policy behavior remains functional. |
| Build | API build passed with zero warnings/errors; `/private/tmp/tenant-persona-effect-build.log`. |
| Final commit boundary | `Approved_persona_effect_commits_once\|Exact_effect_commit_fences\|Expired_exact_grant_is_revoked`: 16 passed after sampling lease/expiry immediately before staging; `/private/tmp/tenant-persona-effect-last-boundary-check.log`. Overlaps the Infrastructure filter above. |
| EF model | `dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build` reports no pending model changes; `/private/tmp/tenant-persona-effect-model-check.log`. Existing tools 10.0.5/runtime 10.0.12 warning remains; generation/comparison succeeded. |
| Documentation/whitespace | 12 Markdown files, 781 local links, 200 fragment anchors, balanced fences and 20 complete JSON examples passed; `git diff --check` passed. |

After final inspection, the adapters additionally sample lease/expiry after validation I/O immediately before staging the local mutation. The focused mutation/replay and delayed-commit checks were rerun for that final boundary.

## Remaining full goal

Production tenant worker claims/dispatch/recovery, mailbox/Hub/audio and recipient disclosure integration remain required. So do model tool offering and procedure/capability admission, direct Allowed effects, trusted continuation consumption in another Run, proactive authority-change revocation propagation, other resource/effect families and external indeterminate reconciliation. Available grants are revalidated/revoked at attempted use here; this does not claim every IAM mutation already propagates revocation before a later permission restoration. Complete aggregate/requester/notification storm controls, tenant-aware Core Events/Automation, full Approvals/Chat/Access UI, Sandbox/decommission and final global-authority/schema cutover remain open.

No installed database reset/migration, application deployment/restart, CI, hosted provider call or milestone acceptance was requested or performed.
