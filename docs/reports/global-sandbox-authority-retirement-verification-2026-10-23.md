# Global Sandbox command authority retirement — 2026-10-23

This follows `7da7986f` and advances the [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It removes unowned Sandbox command/provisioning and process-control entry points, preserves canonical tenant SDK execution, and fixes a real tenant-verification failure found during Docker qualification. Remaining global credential/browser/configuration/Admin roots, raw Runtime fixture reconciliation and final proposal-wide acceptance remain open.

## Authority and cleanup cutover

`OpenSandboxService` no longer exposes raw `ExecuteAsync`, global inspection or process controls, and `SandboxAccess` is removed. The global Profile-based `AgentRunAuthority` implementation/registration is removed; its interface remains implemented by canonical `TenantRunExecutionAuthority`. The SDK constructor drops raw Run, Instance, Definition and global authority dependencies, from 18 parameters to 14. Its workspace dependency is the existing `ITenantSandboxWorkspacePreparer`.

`ITenantSandboxCommandExecutor.ExecuteApprovedAsync`, exact tenant verification, current generation/execution authority, shared SDK command streaming, bounded output/storage checks and finalization remain. No replacement execution framework is introduced. `ISandboxService` now carries native cleanup/reconciliation only. Remaining raw Session metadata is read solely to classify historical cleanup reasons, not authorize execution.

Every nonterminal unowned lease is cleanup-only: no command refresh, process/log inspection, policy reauthorization or generation adoption. Native policy enforcement reports pending until those retained leases terminate. Linked deletion checks exact remote generation metadata; an unowned record cannot delete a tenant-tagged generation. Unlinked cleanup validates every returned item before deletion and retains mismatched evidence. Six new cases cover ready/background receipts, partial files, foreign metadata, original outcomes, orphan inventory and once-only cleanup. Native files/receipts remain intact.

## Real Docker defect and fix

The new canonical container journey initially failed during actual profile verification. The pinned server returned HTTP 400 before creating a tenant probe. Inspection of its installed validator confirmed the cause: the combined `tenant UUID + '.' + attempt UUID` label was **65 characters**, exceeding the server's **63-character** limit. Earlier controlled SDK fixtures had accepted that invalid value.

Verification now uses separate 32-character `agent-core-verification-tenant` and `agent-core-image-verification` values. Both witnesses are checked against returned metadata. The private authenticated capability requires `verificationFencing=true` and **`verificationScopeVersion=2`** before filesystem/create effects; an older adapter cannot treat the new format as an unfenced native probe. Actual provider fences still key the same tenant/attempt in the existing SQLite table. No Core/provider schema migration or consent rehash is introduced. A strict cleanup-only decoder can retire exact old combined-label evidence without accepting it for creation. Generation ownership and ordinary command capability remain unchanged.

The disposable runner was reloaded with its state volume retained. Existing user deployments were untouched. Installed adapters require deliberate reconciliation/reload; unsupported versions fail closed.

## Current container qualification

`Tenant_Docker_Sandbox_verifies_owned_source_executes_exact_review_once_and_retires_only_its_generation` reuses the existing tenant fixture and actual owning services, and runs the complete journey in **both InMemory and SQLite**:

1. Create actual owned source/Definition/Agent/Session/Run state and perform real tenant profile verification through the SDK and private provider fence.
2. Prepare and decide exact human Review, resume the canonical Run and reserve one new generation/command.
3. Seed the admitted immutable template, initialize once, run the reviewed command and verify actual mounted bytes, stdout and one durable completed effect.
4. Observe the same operation again without repeating the command or initialization; global stores expose no tenant lease/Session.
5. Revoke execution authority, reconcile the exact generation, confirm remote retirement and retain canonical output bytes.

The preserved native verification case also exercises successful/failed initialization, bounded private errors and disposable cleanup in real containers. **Both reported tests pass**, zero skipped, **40 s**; the canonical test iterates both storage implementations. The existing runner's `accept` filter now includes this canonical journey. This does not qualify the old unscoped browser runner, every historical Workbench scenario, or proposal-wide UI acceptance.

The task-owned runner used the pinned server/execd/egress images and an explicitly built identity-compatible Workbench. Its package/license inventories and private configuration remain under ignored `local/opensandbox-runners/agent-core-opensandbox-iam-retirement`. The runner and all verification workloads were stopped; its metadata volume and image cache are retained. No user database, Real host or unrelated container was reset/restarted.

## Fixture reconciliation and remaining failure evidence

**34 obsolete method bodies across two fixture paths** are retired: 33 global command/lifecycle Docker journeys from `OpenSandboxIntegrationTests` and one global unknown-create wrapper from `OpenSandboxCleanupTests`. Original bodies are recoverable from `7da7986f`; exact paths, hashes and names are in `/private/tmp/sandbox-authority-retirement-tests-20261023.json` and its baseline manifest. Two native verification scenarios remain with a literal disposable source fixture, which creates no global Session/Run/claim. Removed bodies are not counted as passing current tenant qualification. Canonical provisioning, current source/configuration/consent, background/process Stop, reset, profile switching and recovery are exercised by the current groups below; the new Docker journey supplies actual mounted execution evidence within its stated scope.

A broader Application probe selected 67 cases and initially found **47 passed / 20 failed**. An isolated `git archive 7da7986f` snapshot, restored only from cached packages and built separately, reproduced **the identical 20 failures**. One was a stale Sandbox offering assertion: it incorrectly required the entire authorization tool to disappear when another independent operation could still be offered. It now checks that the Sandbox branch/direct dispatch is absent; all 22 focused Sandbox contracts pass.

The other **19 inherited raw Runtime fixture cases remain failing** across eight methods in `AgentRunDurabilityTests` / `BackgroundSemanticRecoveryTests`: typed waits, initial completion, retry ownership, semantic/format recovery, completion-only cutoff and conversational-output exclusion. Their global helper no longer establishes current tenant execution authority. They were not deleted or reclassified as passed here and require explicit canonical reconciliation before final acceptance. The failure-case manifest is `/private/tmp/sandbox-authority-retirement-application-failures-20261023.json`; baseline and current logs retain exact outputs. This scoped module does not claim a green broad Application probe.

The [canonical protocol successor](canonical-background-protocol-verification-2026-10-23.md) subsequently reconciles those 19 inherited failures. Its eight obsolete positive methods are retired only after actual owned recovery/outcome/deadline and current wait/interrupt qualification; the remaining 46-case Application probe passes. The failure evidence above remains the historical checkpoint, not the current outstanding list.

## Focused local verification

| Check | Observed result |
| --- | --- |
| First native cleanup/current tenant SDK use cases | **6 passed**, 23 s; overlaps final groups. |
| Grouped SDK provisioning/commands/process controls/reset/switch/recovery and native cleanup | **170 passed / 2 failed initially**, 9 m 59 s. Both failures were new orphan fixtures missing the SDK-required entrypoint in their response. All six new cleanup cases pass after correction, **669 ms**; these supersede both failures without repeating the full group. |
| Authenticated HTTP/model foreground/background/Automation Sandbox | **34 passed**, 1 m 40 s. Includes actual host absence/constructor guards plus scoped observations, exact consent and runtime outcomes. |
| Tenant verification, source/cleanup/retirement and old-adapter refusal | **32 passed**, 1 m 30 s; includes both-store scope-version refusal. |
| Instance verification SDK ownership | **2 passed**, 10 s. |
| Sandbox authorization/read/control/setup/failure contracts | **22 passed**, 159 ms. Broader inherited failure boundary is recorded above. |
| Real Docker canonical tenant and native verification | **2 passed**, 40 s, zero skipped; canonical case executes both stores. Initial failed label qualification is superseded by the versioned fix. |
| Provider compatibility/retirement unit checks | **19 passed**, 0.066 s. Includes exact historical cleanup, malformed/mixed owner refusal, current label bounds and restart/late-create fences. |
| Pinned-image ASGI/auth/provider-hook proof | **Passed** with network disabled, read-only mounts and no Docker socket; Docker peer controlled. |
| Final serial solution build | **Passed**, zero warnings/errors, **30.53 s**. |

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Test commands use existing projects with `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`. Reproducible filters are preserved in the logs and include:

```text
SDK/runtime: OpenSandboxCleanupTests; Tenant_Sandbox_approved_command_crosses_real_SDK_once_; Tenant_Sandbox_SDK_keeps_launch_; Tenant_Sandbox_model_continuation_; Tenant_Sandbox_first_generation_; Tenant_Sandbox_private_source_is_rechecked_; Tenant_Sandbox_lost_create_reply_; Tenant_Sandbox_old_server_; Tenant_Sandbox_new_generation_; Tenant_Sandbox_worker_; Tenant_Sandbox_human_Stop_; Tenant_Sandbox_human_reset_; Tenant_Sandbox_profile_switch_
Verification: Tenant_sandbox_verification_; Tenant_instance_sandbox_verification_SDK_lost_create_
API: Tenant_Sandbox_HTTP_returns_scoped_; Tenant_Sandbox_model_request_waits_for_exact_; Tenant_background_native_Sandbox_; Independent_Automation_Sandbox_preserves_
Application contracts: TenantSandboxAuthorizationContractTests; TenantSandboxInspectionContractTests; TenantSandboxReadAuthorizationContractTests; TenantSandboxControlContractTests; SandboxFailureSummaryTests; SandboxSetupResolverTests
Docker: Tenant_Docker_Sandbox_; Verification_executes_initialization_rejects_broken_scripts_
```

Each name is a `FullyQualifiedName~` selector joined with `|`. Python command: `python3 -m unittest discover -s scripts/tests -p 'test_sandbox_*.py'`. The [pinned-image ASGI command](../../scripts/tests/README.sandbox-retirement.md) uses the updated sources. Docker execution used the existing runner environment and only the explicit Docker filter, rather than the full local/hosted CI suite.

Logs use `/private/tmp/sandbox-authority-retirement-`: `runtime-20261023.log`, `cleanup-final-20261023.log`, `api-20261023.log`, `verification-final-20261023.log`, `instance-verification-final-20261023.log`, `application-20261023.log`, `contracts-final-20261023.log`, `baseline-application-20261023.log`, `docker-runtime-20261023.log`, `docker-runtime-r2-20261023.log`, `python-final-20261023.log`, `asgi-final-20261023.log`, `build-r7-20261023.log` and `docker-cleanup-20261023.log`. Intermediate build failures from retired inspection dependencies/imports were corrected before qualification. No approval-default, public permission, hosted-provider or product UI change is made. CI and proposal-wide rendered UI acceptance remain unrun.
