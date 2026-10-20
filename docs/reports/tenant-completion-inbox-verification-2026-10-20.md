# Tenant completion inbox verification — 2026-10-20

This checkpoint converts human-attributed tenant `background.list`, `background.inspect`, `background.take` and `background.acknowledge` through the existing Session Runtime and Run transactions. The full proposal and original goal remain active. Protected completion-read consent, native background effects, autonomous origins, remaining interaction/System/audit families and final authority/schema consolidation remain open. No milestone or proposal-wide acceptance is claimed.

## Ownership and behavior

The narrow `ITenantCompletionRuntimeStore` facet is bound to the actual parent mailbox frame. It keeps the initial child as task identity, follows only exact saved approval handoffs to the current source Run, and preserves generic tenant Run-store Session isolation. Pinned/current explicit capability, current parent generation/lease/Session state, actual tenant/Agent/executor ownership and original requester authority gate operations. Completion-report Runs cannot use these controls.

Current executor child Session, initial Run and source Run reads, including operation policy, precede list paging/counting and detailed inspection. Missing, foreign or unreadable task/cursor references fail closed. Lists/inspection remain untrusted observations and never consume a result. Artifact links contain independently permission-filtered metadata from the exact source Run, at most two visible links plus an authorized-more indicator; no artifact bytes are read. Retained source and metadata observations are checked again before provider use and inside final outcome persistence. Read policies requiring separate consent provide no implicit grant.

Take and acknowledgment must match the exact saved pending model call. An exclusive token belongs to the current parent Run, generation, call and lease. Repeating the same take returns its existing token/revision. Acknowledgment remains provisional. The existing final-answer transaction commits the successful answer and Handled accounting together; separate completion commands cannot bypass it. Current acknowledged-source authority is checked even before a tool observation reaches the saved checkpoint. Stale Session revision or lost read authority cannot save the answer or mark the item Handled.

Failure, cancellation, retry, recovery to Queued or expired claims release Pending without acknowledgment/token/handled evidence. Renewal and same-Run signal wake transfer an unexpired token to the current lease/generation; an expired claim cannot revive. A handled item cannot create an automatic report. The independent report-creation consent module remains separate; take cannot consume an already queued report. Only reportable terminal tasks support take; a child admitted with `reportCompletion=false` gains no reporting intent.

Memory uses existing ownership gates; SQLite uses existing Run/inbox transactions and revision concurrency. There is no new persistence owner, queue, executor, permission family or migration. [Port](../04-backend-interfaces.md#tenant-completion-inbox-runtime-facet), [execution](../12-backend-implementation-spec.md#tenant-completion-consumption-execution), [persistence](../15-persistence-and-configuration.md#tenant-completion-consumption-persistence).

## Focused local verification

All checks use Synthetic fixtures without provider credentials. Commands use `dotnet test <project> --filter '<selection>' --logger 'console;verbosity=minimal'`; local sandbox escalation allows .NET IPC/loopback. Logs use `/private/tmp/tenant-inbox-`.

| Selection; log suffix | Result |
| --- | --- |
| Actual model/HTTP ordinary inbox workflow; `api5.log` | Two passed: Memory and SQLite. Parent starts child, waits, lists, inspects, takes and acknowledges. At the model barrier the inbox is Claimed with no handled Run. Saving the final answer atomically changes it to Handled, clears its token and leaves no report candidate. |
| Actual child read-approval successor consumption; `api-handoff4.log` | Two passed: Memory and SQLite. Exact HTTP review creates the child successor; list/inspect/take/acknowledge consume its terminal result, the initial child remains Continued, and the leaf inbox is Handled by the successful parent. |
| Inbox authority, accounting and legacy consumption; `store.log` | 38 passed of 42. Four new failure/retry fixtures used an invalid failure-code format; their named corrective checks below supersede those failures. |
| Corrected interruptions and acknowledged-source final-save fence; `store-corrective.log` | Six passed: four corrected interruption cases and two new final-save read-revocation cases. Two new paging fixtures exceeded the existing child budget; their bounded successors below supersede those failures. |
| Resource-filtered cursor pages/counts; `paging.log` | Two passed. Two readable tasks page correctly; revoking the newer task's exact Run read filters it before the limit and total, and refuses its cursor/detail lookup. |
| Tool offering, canonical tenant projection and incumbent wait contracts; `application.log` | 18 passed. Acknowledge is offered only with a live claim; missing capability, wrong Session/model/output/origin fail closed. |
| Affected actual wait/approval-handoff and report-consent regressions; `api-regression.log` | 16 passed, including the corrected child completion payload in background/handoff wait cases. |
| Final solution build; `final-build.log` | Passed, zero warnings/errors. |

The store group also exercises token/generation/pending-call mismatch, current read and stronger policy refusal, artifact metadata filtering and retained-source rechecks, provisional acknowledgment with stale final save, all interruption/recovery cases, and lease renewal plus same-Run wait transfer. Legacy consumption cases retain automatic-report exclusion, acknowledgment atomicity and expiry/generation behavior.

Initial API fixture compilation corrected a model event type and assertion analyzer issue. Functional diagnosis found a missing required `attentionRequired` in its child `work.complete` payload. Core correctly rejected the call and exposed failed-child evidence; the fixture now supplies the closed contract. The earlier wait fixture received the same correction and its affected cases passed. Failure/retry fixtures now use valid hyphenated failure codes. Paging qualification stays within the existing child budget. No product gate was relaxed. Passing groups were retained rather than repeatedly rerun after fixture-only changes.

The additional exact-successor HTTP cases both passed and complete the four distinct actual inbox workflow cases. Their fixture corrected a local-name collision and removed context-controlled `approvals.request_authorization` from its authored always-offered projection. One automatic approval review timed out without a safety finding; the allowed retry completed, and its unchanged fixture rejection revealed that the timed-out command had not saved that correction. The correction was then saved separately before the next targeted run.

The qualified groups contain **84 distinct passing local cases**: 46 store cases (38 initial plus six corrected/additional and two bounded paging), 18 application cases and 20 HTTP cases (four inbox workflows plus sixteen affected regressions). Overlapping fixture reruns are not counted twice.

The user-approved preceding 51-file report-consent module is committed to main as `4273ac17`. Typed waits and this inbox module remain separate working-tree changes. No CI/full CI suite, Real host, live provider, deployment, installed-data reset/migration, PR or push was run. This backend-only module changes no frontend presentation and claims no new browser acceptance.

Final checks resolve 1,707 relative link targets across eleven changed Markdown files, with balanced fences and clean tracked/untracked source whitespace. Every owned test/build process reached a terminal result, and disposable hosts were closed by their fixtures. No additional commit was made for the wait/inbox working-tree modules.
