# Independent Automation wait verification — 2026-10-20

Admitted Schedule/manual Automation tasks and their exact approval successors can use the existing explicitly authorized `execution.wait` capability. Suspension retains the same logical Run, Activation, Response and attempt; the incumbent coordinator resumes with a fresh generation. Scheduled tasks have no human requester. Manual tasks retain the actual invoker and current Automation read/run authority. The frozen Automation task and immutable accepted-occurrence lineage remain prerequisites; registration authors and reviewers supply no inherited task authority.

## Executed behavior

| Group | Distinct cases | Expected and observed outcome |
| --- | ---: | --- |
| Independent duration/repeat waits | 8 | Both stores and both origins release the claim while waiting, remain unready before the exact deadline, append one result per wait and finish once. Repeat waits keep one logical attempt. The suspended generation cannot renew or mutate execution. |
| Stop | 4 | Both origins/stores cancel while suspended. Advancing past the deadline creates neither a wait result nor a later answer; a further worker pass cannot revive the Run. |
| Unrelated Session targets | 4 | An unrelated owned human Session is refused as a background wait target. No wait is admitted and no target status is disclosed. |
| Approval-successor waits | 4 | Exact protected workspace-read Review creates the established successor in the same task/Session. That successor may suspend and resume without another attempt. The current source, root and requester remain unchanged. |
| Current source-read revocation | 4 | After the approved read and suspension, revoking the executor's raw source-read role prevents resumed model generation and final output. The saved elapsed result does not grant source reuse. |
| Actual manual invoker revocation | 2 | An invited non-author human invokes without Chat authority. Revoking that human's Automation read/run role during suspension stops resumed generation and final output in both stores. No author substitution occurs. |
| SQLite host recreation | 4 | Both origins reopen the exact durable wait/checkpoint, or an already sealed signal result before mailbox admission. A fresh host/model resumes the original task with one result, one answer and unchanged attempt count, including expired-claim recovery. |
| Existing human HTTP/model regressions | 22 | Conversational/immediate-background duration, child readiness, timeout, Stop, queued input, approval handoff, restart, protected completion Review and exclusive take/acknowledge consumption retain their existing behavior. |
| Store regressions | 26 | Both stores retain exact checkpoint/source ownership, unreadable-target behavior, current source revocation, approval handoff, stale-generation rejection and signal deferral/recovery without another attempt. |
| Application contracts | 38 | Converted frozen Automation origins expose wait; unsupported origins/missing task identity do not. Existing closed argument bounds, human wait/completion capability checks and protected-completion schemas remain intact. |

The final focused groups pass **116 distinct cases, zero failures and zero skips**: API independent 30, API adjacent 22, Application 38 and Infrastructure 26. The three affected test-project builds pass with zero warnings/errors. Passing adjacent groups were retained after the final API-only fixture barrier; only the affected 30-case independent matrix needed another grouped run.

The [independent child/inbox successor](tenant-independent-child-verification-2026-10-20.md) extends the historical conversion boundary below. This wait report remains evidence for its original scoped change.

## Ownership and limits

`TenantWaitToolContract` reuses the converted task-shape contract. `TenantWaitRules` accepts actual independent tasks while retaining explicit human attribution for the earlier conversational/immediate-background path. The existing store suspension transaction verifies current capability, saved pending call/checkpoint, live frame and actual lineage. Resumed provider admission repeats current executor, requester, source and source-kind policy checks. No new scheduler, worker, permission family, database migration or task entity is introduced.

Independent completion-list/inspect/take/acknowledge and child creation remain unconverted. Their discovery and transaction gates continue to refuse independent tasks; enabling duration waits does not silently authorize completion reads or child creation. Background wait targets still require actual immutable initial-child ownership and separate raw source reads. The unrelated-target matrix is negative qualification, not independent child-creation/inbox acceptance.

The fixture uses `FakeTimeProvider`, explicit incumbent worker passes and the host's existing generation-stop signal. An internal read-only barrier observes release of the suspended provider/runtime and capacity slot before the test advances time. It adds no HTTP endpoint or execution authority. Reopen inspection holds dispatch until the original checkpoint has been compared; deferred dispatch advances only its persisted retry deadline. Initial grouped fixture failures and the isolated duration stall are retained in the earlier logs; final results supersede those runs only for the same exercised cases.

## Commands and evidence

Focused builds use `dotnet build <project> --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false` for API, Application and Infrastructure test projects. Focused test commands add `--no-build`, the corresponding project and the filters below; local socket permission was used for disposable test hosts.

- API independent: `FullyQualifiedName~Independent_Automation_wait`.
- API adjacent: `FullyQualifiedName~Tenant_wait_keeps_|FullyQualifiedName~Tenant_completion_read_Review_|FullyQualifiedName~Tenant_completion_inbox_actual_`.
- Application: `TenantIndependentTaskContractTests`, `TenantWaitContractTests`, `TenantCompletionContractTests`, `TenantCompletionReadContractTests`.
- Infrastructure: `Tenant_typed_wait_`, `Tenant_wait_source_`, `Tenant_signal_dispatch_`, `Tenant_wait_observation_`, `Tenant_wait_follows_`.

Final logs: `/private/tmp/tenant-independent-wait-final-api-tests.log`, `/private/tmp/tenant-independent-wait-api-regressions.log`, `/private/tmp/tenant-independent-wait-application-tests.log`, `/private/tmp/tenant-independent-wait-store-tests.log`. Build logs end in `final-api-build.log`, `application-build.log` and `infrastructure-build.log` under the same prefix. Earlier logs retain `api-tests.log`, `corrected-api-tests.log` and `duration-diagnostic.log`. Markdown link/fence and whitespace checks are recorded with the final work.

Synthetic tests make no provider calls; the actual outbound probe remains zero. SQLite evidence is application-host disposal/recreation, not a physical process kill. No new frontend journey, live provider behavior, CI run, full local CI suite, milestone acceptance or complete proposal acceptance is claimed.

Automation admission Review, independent child/inbox/report controls, event/webhook/watch ingress, existing-Session destinations, tenant Automation authoring UI, remaining interaction/voice/System/audit families and final authority/schema/requirements audit remain open. The original full-proposal goal stays active.

Canonical owners: [wait port/ownership contract](../04-backend-interfaces.md#tenant-typed-wait-ownership), [runtime implementation](../12-backend-implementation-spec.md#tenant-wait-execution) and [testing matrix](../16-testing-strategy.md#tenant-typed-wait-qualification).
