# Tenant typed wait verification — 2026-10-20

This checkpoint converts bounded `execution.wait` for human-attributed tenant conversational/background tasks and their exact approval successors. The full proposal and original goal remain active. The subsequent [completion-inbox checkpoint](tenant-completion-inbox-verification-2026-10-20.md) converts list/inspect/take/acknowledge. Protected completion-read consent, native background effects, autonomous origins, remaining interaction/System/audit families and final authority/schema consolidation remain open. No milestone or proposal-wide acceptance is claimed.

## Implementation and ownership

The pinned and current explicit capability, actual execution kind, live generation/lease, current parent state and original-human root authority independently gate suspension. A shared closed parser freezes the exact pending call's duration or background condition; transaction validation compares the current saved checkpoint rather than accepting replacement arguments. Limits remain eight unique targets, 300 seconds per wait, eight waits and 900 requested seconds per Run. Completion-report Runs cannot wait.

Targets are initial children of the exact parent Session, with actual tenant/Agent/executor ownership. Persisted exact approval handoffs resolve the current task Run; an initial Continued outcome alone is not completion. Current executor child Session and initial/source Run reads, including operation policy, gate status observations. Missing/unreadable admission targets return NotFound; lost access or read policy requiring separate consent wakes with Unavailable and no source status. A wait supplies no read grant. Retained source observations are checked again before provider context use.

The existing Run transaction saves WaitingForSignal without a claim. The tenant Runtime releases its generation/provider worker; no live pending object is continuation authority. Existing coordinator wake appends exactly one result and resumes the same Run/Activation/Response with a fresh generation and unchanged logical attempt. Duration elapsed, background timeout and unavailable are normal results. Waits never take, acknowledge or handle a completion. Readiness filtering precedes the runnable result limit; an unready wait cannot hide later work.

The existing Runtime-admission boundary gains an optional `AwaitingSignalAdmission` checkpoint discriminator. It distinguishes a sealed signal wake from later approval admission, even when that task previously waited. Capacity/collision deferral and pre-admission claim recovery preserve the result, attempt and paused active budget. Actual mailbox admission clears both flags before execution. Historical checkpoints default to false and omit the new discriminator when false. No EF schema/migration or second queue, coordinator, executor or permission family is introduced.

New human input queues as its own attributed Run while a waiting task retains exclusive Session execution. Cancellation fences the old generation and cannot resurrect the wait. Progress delivery cannot reverse an already committed suspension. [Port contract](../04-backend-interfaces.md#tenant-typed-wait-ownership), [execution](../12-backend-implementation-spec.md#tenant-wait-execution), [persistence](../15-persistence-and-configuration.md#tenant-typed-wait-persistence).

## Exercised scenarios

| Scenario | Expected and observed evidence |
| --- | --- |
| Actual Synthetic duration/background/timeout, both stores | One wait and logical attempt; worker release, same Run/Activation/Response, one result and one durable final answer. |
| Actual cancellation while waiting | Cancelled without wake or final assistant output. |
| Actual new human message during wait | New input stays queued until current task ends, then completes independently. |
| Actual SQLite application host recreation | Saved condition/checkpoint survive full disposal/recreation; one normal response resumes through existing owners. |
| Direct old-frame/stale wake | Both stores fence the suspended generation and reject a repeated wake; no duplicate result or accounting. |
| Read revocation or stronger source-read policy | Admission refuses, or the existing wait wakes Unavailable without exposing status/Run identity. |
| Saved condition substitution | Both stores refuse a changed deadline without mutating the Run. |
| Unaccepted dispatch/recovery with real active-budget fields | Same sealed result, logical attempt and paused budget; fresh claim generation. |
| Retained source permission revoked before provider context | Current source guard refuses rather than reusing a read snapshot. |
| Exact child approval handoff | Both actual HTTP stores complete the child's own wait, prepare/approve its independently reviewed read, retain the parent's WaitingForSignal status while the initial child is Continued and its exact successor is blocked, then wake only after that successor finishes. Separate admitted-child/domain link checks reject broken chains. |
| Historical checkpoint and cumulative budgets | Approval pending state remains distinct from signal pending; JSON roundtrip, clock pause, acceptance clearing and incumbent wait ceilings pass. |

## Focused local checks

Commands use `dotnet test <project> --filter '<selection>' --logger 'console;verbosity=minimal'` and local sandbox escalation for .NET IPC/loopback. Logs are under `/private/tmp/tenant-wait-`.

| Selection; log suffix | Result |
| --- | --- |
| Actual model/HTTP lifecycle matrix; `api-final.log` | 11 passed: both stores × duration, background, timeout, cancel and queued input, plus actual SQLite host recreation. |
| Tenant wait transactions/authority/handoffs; `store-qualified.log` | 26 passed on the explicit discriminator and active-budget implementation. |
| Preserved legacy wait/reopen/cancel/unavailable guards; `store-module2.log` | 14 passed within the earlier grouped selection; unrelated new handoff fixture failures are superseded by the qualified group. |
| Closed parser, offering and canonical projection; `application2.log`, `application-corrective.log` | 14 distinct passed: ten grouped successes plus four corrected causal-context fixtures. |
| Checkpoint compatibility and existing count/time limits; `domain.log` | Eight passed. |
| Actual child approval handoff during parent wait and affected report/file regression; `handoff-regression.log`, `handoff-qualified2.log` | Eight distinct passed: six grouped regression successes plus two qualified actual handoff cases. |
| Solution build; `final-build.log` | Passed, zero warnings/errors. |

Initial integrated execution reached correct wait/resume behavior but the fixture incorrectly read history from metadata-only Session snapshots and then assumed a public history Response ID. The final assertion uses canonical messages and exact final text. Recreated value objects now compare serialized values rather than reference identity. New normalized-handoff fixtures supply required evidence and a fake clock; offering fixtures use immutable causal contexts. These fixture corrections do not weaken product gates. Passing groups were retained rather than repeatedly rerun after fixture-only changes.

The extra actual handoff fixture initially lacked the independently required `agents.read` permission for its home-workspace inspection; the existing gate refused preparation. Its qualified setup now supplies that permission and asserts the saved read-authorization result, which deliberately does not claim a mutation was applied. Bounded canonical successor diagnostics replace a blind pending-state wait. Both actual handoffs pass after these fixture corrections. No product permission gate was relaxed.

Review also found that a generic Runtime-pending flag plus historical wait count could confuse an approval continuation with a signal wake, and a budget-clock stamp could drop the pending boundary. The explicit optional discriminator and preserved clock flags correct both before final qualification. Admission/deferral tests use the real active-budget fields. Policy changes and transport-progress failure remain fenced at the existing owners.

No CI/full CI suite, Real host, live provider, deployment, installed-data migration/reset or external account action was run. API fixtures assert zero outbound provider HTTP attempts. This backend module uses real Synthetic integration fixtures; it does not change frontend presentation or claim new browser/UI acceptance. The preceding report-consent module was committed separately as `4273ac17` after the user explicitly approved committing its verified 51-file index to main.

The subsequent completion-inbox regression group also supplies the required `attentionRequired` field in this fixture’s child completion payload and requalifies the affected actual background/handoff cases. The original wait-readiness contract accepts any authorized terminal outcome; the correction supplies the intended normal child completion without relaxing that contract.

The qualified groups contain **81 distinct passing local cases**. Corrective and representative reruns overlap earlier selections and are not counted twice. All cited tests reached terminal outcomes; intermediate failures are disclosed above and completed only by their named corrective evidence.

Final documentation checks resolve all 2,096 relative links across fifteen changed Markdown files and balanced fences. Tracked, staged and untracked-file whitespace checks pass. Every owned test/build process is terminal; disposable application hosts were closed by their fixtures. The wait module remains in the working tree, separate from the user-approved report-consent commit `4273ac17`.

Focused selectors for reproducing this scope:

```text
API:
FullyQualifiedName~Tenant_wait_keeps_one_Run|(FullyQualifiedName~Tenant_background_report_creation_consent_&DisplayName~review)|FullyQualifiedName~Tenant_background_model_starts_|FullyQualifiedName~Tenant_background_file_read_Review

Infrastructure:
FullyQualifiedName~Tenant_typed_wait_|FullyQualifiedName~Tenant_wait_source_|FullyQualifiedName~Tenant_signal_dispatch_|FullyQualifiedName~Tenant_wait_observation_|FullyQualifiedName~Tenant_wait_follows_|FullyQualifiedName~Typed_wait_survives_|FullyQualifiedName~Wait_timeout_is_normal_|FullyQualifiedName~Wait_denies_self_|FullyQualifiedName~Suspended_wait_resolves_

Application:
FullyQualifiedName~TenantWaitContractTests|FullyQualifiedName~Tenant_context_offers_only_implemented

Domain:
FullyQualifiedName~SignalAdmissionCheckpointTests|FullyQualifiedName~Durable_waits_enforce_|FullyQualifiedName~Wait_descriptor_rejects_
```
