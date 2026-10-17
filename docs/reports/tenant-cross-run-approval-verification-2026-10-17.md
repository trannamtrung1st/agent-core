# Exact approval continuation across Runs

Date: 2026-10-17. Baseline: `c98f3d9456c3919900fa1c27cc4bea802a3fc440`. This follows the [typed preference approval checkpoint](tenant-preference-approval-verification-2026-10-17.md). The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; no milestone acceptance, deployment or installed-data reset is claimed.

## Implemented boundary

Model-originated persona/preference waits now resume their exact saved operation in a recorded successor Run. One adapter transaction retires source A as Completed/Continued, admits B, acknowledges the decision signal and appends a safe continuation audit. A's result links B without fabricating a completed response. Internal pre-execution requests without a model checkpoint retain the existing original-Run path.

`ApprovalContinuation` and `TenantApprovalResume` retain the same executor, Session, original requester, root and parent; exact request/operation/hash; frozen configuration/persona/model and capabilities; remaining checkpoint budget and attempt accounting; and original response/transcript identity. B owns no input entries. Original input references are read-only and must resolve; no Task entity, scheduler, general permission or temporary RoleAssignment is introduced.

Claim and local-effect stores resolve the actual source and require its recorded successor link, matching lineage, ownership and exact operation. A generic sibling, changed operation/hash or different requester cannot gain authority through lineage. A first successor effect also requires its matching pending authorization checkpoint. A saved receipt permits safe historical replay after recovery without another mutation, while stale generations cannot resume the Runtime. The existing approval/grant/receipt relational constraints remain active; no migration or user database change was needed.

Scoped history now excludes later independent input and output, including when another turn completed while the source waited. Scoped saves preserve those unseen entries. Queue ordering treats an Activation without owned inputs consistently in both stores. Source metadata exposes outcomeKind=continued and continuationRunId under existing current Run/Session read permissions; clients follow the link to observe execution.

## Capacity and cancellation correction

A focused hosted test reproduced a checkpointed successor remaining Running while the sole Runtime slot was occupied. The coordinator's ordinary unstarted-dispatch refund does not apply to a saved checkpoint, so the claim would otherwise wait for lease recovery.

The tenant host now joins dispatch attempts for a Run/generation, reloads the actual claim and cancels only proven stale live state. Known pre-admission slot refusal uses a fenced DeferApprovalDispatch transition. Its Core-only awaitingRuntimeAdmission marker preserves the checkpoint, active budget and last attempt across resource retries and clears on the next claim. Unknown or possibly accepted failures retain ordinary lease recovery semantics. Authenticated Run cancellation also accepts an unclaimed WaitingToRetry successor; it cannot be revived to consume the approval afterward.

The contention reproduction failed with observed Running before correction. It then passed in InMemory and SQLite: occupy the sole shared slot → approve the model's exact request → observe successor WaitingToRetry with unchanged checkpoint/attempt/budget and no effect → release the slot → join duplicate dispatches and refuse a stale generation while the model worker is gated → complete one saved effect/response. A separate both-store test repeats deferral on the last allowed attempt without spending waiting time or granting a fresh allowance.

## Executed focused checks

All 387 focused tests passed: 53 Domain, 101 Application, 201 Infrastructure and 32 API. Offline Synthetic providers and disposable data only. No live provider calls, credentials, user database, existing application process or hosted CI was used.

| Check | Command/filter | Result |
| --- | --- | --- |
| Domain Activation/Run/budget contracts | `dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~ActivationAdmissionTests\|FullyQualifiedName~AgentRunContractTests\|FullyQualifiedName~ExecutionBudgetPolicyTests'` | 53 passed |
| Application durability, typed tools and projection | `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~AgentRunDurabilityTests\|FullyQualifiedName~TenantApprovalToolContractTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~ToolRegistryPolicyTests'` | 101 passed |
| Scoped stores, admission/runtime and local effects | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests\|FullyQualifiedName~TenantRunAdmissionTests'` | 201 passed |
| Actual hosted tenant journeys | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~TenantSessionAccessTests` | 32 passed |

Representative hosted workflow: authenticate → create an owned Instance → model emits exact authorization → persist wait and release Runtime → decide through canonical HTTP → atomically hand off → worker claims a different Run → apply the saved exact effect once → close pending tool checkpoint → model receives recorded outcome and completes the original response. Both persona and preference workflows survive SQLite host recreation before the decision, without reasking. Read projection identifies the actual continuation.

Store integration covers Review and ExceptionalGrant, one winner under duplicate/concurrent signals, identity-collision rollback, cancellation before/after handoff and during quiescent resource wait, invalid source checkpoint refusal, changed operation/hash metadata, missing/corrupt successor checkpoint denial, preserved original input ownership, later-history isolation and save preservation, queue parity, reopen/lease recovery, old-generation refusal and durable receipt replay. The existing 20-minute approval-delay budget scenario now follows B and retains its remaining execution budget. Existing direct/persona/preference and ownership checks remain part of the final regression.

Evidence logs: `/private/tmp/tenant-cross-run-flow.log`, `/private/tmp/tenant-cross-run-capacity-before.log`, `/private/tmp/tenant-cross-run-capacity-final.log`, `/private/tmp/tenant-cross-run-admission-budget.log`, `/private/tmp/tenant-cross-run-domain-final.log`, `/private/tmp/tenant-cross-run-application-final.log`, `/private/tmp/tenant-cross-run-infrastructure-final.log` and `/private/tmp/tenant-cross-run-api-final.log`.

## Remaining boundaries

This verifies exact cross-Run consumption for the two supported local operation kinds. Other protected resource/effect families, external reconciliation, complete authorization-decision audit and proactive revocation/storm controls, runtime credential use, full tenant Chat/output/audio and approval UI, and final obsolete-authority/schema removal remain required. Browser and hardware behavior were not changed or requalified. CI and live-provider acceptance were not requested or run.
