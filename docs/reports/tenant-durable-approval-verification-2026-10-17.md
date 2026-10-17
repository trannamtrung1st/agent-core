# Durable tenant approval management and Run wakeup — 2026-10-17

Baseline: `454f8851adc5cf1260ae5b27156b45850633fc86`. The full multi-tenant IAM/approval proposal remains active. This change implements real durable typed request/decision/grant/signal management and Run waits, not complete tenant model/effect execution or the approval UI.

## Delivered boundary

`TenantApprovalService.PreparePersonaAsync` is a trusted internal typed entry point over an actual saved tenant Run. Core derives `agents.persona.update`, `agents.update`, the actual executing Instance, the four persona fields, Instance/persona revision witnesses and canonical SHA-256. Operation identity is stable and separate from the fingerprint used to suppress same-root duplicates. No public generic action-preparation endpoint, model-selected identity/permission, copied login cookie or temporary RoleAssignment is introduced.

Both IAM stores commit request creation, Run wait, security revision and safe audit together. Canonical Runs reference a first-class request and reject embedded legacy approvals. Queued pre-execution preparation requires no fabricated worker claim or model attempt. The running transition requires the correct live generation and rejects expired claims and uncertain side effects. Parking releases the execution claim; resumption preserves the attempt and paused checkpoint budget.

The new normalized request table indexes tenant/root/executor/fingerprint, inbox, expiry and operation identity. Its restrictive foreign keys bind actual Run/Session/root, same-tenant Session/Instance/executor and original User. Prepared persona content is bounded configuration, with no credential or bearer-token field. Reading the persisted payload revalidates canonical content/hash and indexed identity. Exceptional grant foreign keys bind the exact tenant/request/executor/operation/hash, with one stable effect idempotency key. Grants never become roles or entire-Run authority.

`GET /api/v2/tenants/{tenant}/approvals` and `GET /{request}` require the current human session, actual request read and underlying target operation authority. Visibility filters apply before the bounded inventory limit. The safe projection contains exact persona, operation hash and witnesses plus lineage, kind/status and expiry, without a grant token or unrelated private configuration.

`POST /{request}/decision` requires cookie/CSRF, exact hash, request/security witnesses, current qualified human action plus approve authority and applicable independence. Exceptional decisions additionally require grant-exception. Current executor/requester authority and material witnesses are checked. Original live human session, security CAS and current expiry are checked again inside the owning transaction. Request terminal state, immutable human decision, audit, optional exact exceptional grant and processed signal commit together. Approval does not apply the persona.

`POST /{request}/cancel` uses scoped `approvals.cancel` rather than operation-grant authority. It can release a materially stale pending request without disclosing or granting the prepared action. Core expiry creates a terminal request and signal without inventing a human decision. The existing Run worker drains these signals before its normal coordinator pass and atomically acknowledges each receipt with requeue of the same Run. Repeated signal delivery cannot requeue again. There is no new scheduler, broker, Task entity, chat connection or response-epoch requirement.

Same-root pending requests return the existing request. Rejected/expired/cancelled operations are suppressed within that root; already-approved operations cannot obtain another request through preparation. Independent roots can prepare again after fresh policy checks. A transactional bound of 32 pending or last-minute requests per tenant/executor limits this typed path; complete aggregate/requester/notification storm controls remain required.

Concurrent same-root preparers that lose security/Run CAS re-resolve current saved identity and return the winning exact request instead of creating another request or returning a misleading stale result. Running preparation also rechecks the actual lease at the owning transaction, so a claim that expires after service preparation cannot release the Run or save a request.

## Executed evidence

The initial integrated workflow passed four cases over both stores: prepare an actual queued Run, inspect its durable wait, repeat preparation without another audit/security update, sign out, sign in again, reopen SQLite, retain the exact operation, reject a wrong hash, approve or reject, inspect the exact grant/no-grant outcome and signal, drain once, reject repeated delivery/decision and suppress a duplicate root request. Neither outcome changes the actual persona or adds an executor RoleAssignment.

Eighteen subsequent boundary cases passed: ordinary Review with current independent human authority, expiry/new-root retry, and delayed decision races against logout, role revocation, competition, cancellation and expiry. Delayed failures leave no grant, false decision audit or processed signal; the competing decision remains the sole winner. Two further cases passed material target invalidation followed by scoped cancellation without effect authority.

The first broad affected filter found a real parity defect: the existing SQLite Session execution index counted canonical WaitingForApproval as holding the sole slot. A second independent pending root failed while InMemory admitted it. The corrective migration releases canonical durable waits, preserving legacy waits and the single-running-Run invariant. Five focused cases then passed: scoped inventory before limit, actual multiple pending roots followed by an enforced single running claim, generation/lease/same-attempt invariants, raw SQLite lineage/exact-grant rejection and canonical payload corruption detection. The temporary constraint diagnostic was removed after reproduction.

The final Synthetic HTTP workflow passed cookie/CSRF read/list/exact-decision and cancellation, safe projection, anonymous/old-owner-token/foreign-tenant denial, ignored forged identity/action fields, invalid decision/hash bounds, exact-hash conflicts and repeat-decision CAS. Approval and cancellation leave the actual persona unchanged; cancellation creates no exceptional grant.

Final focused commands all used `--no-restore --nologo` with `FullyQualifiedName~` for each filter:

| Check | Observed result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`, filters `TenantOperationAuthorizationTests\|TenantRunAdmissionTests\|IamIdentityIntegrationTests\|CanonicalStartupMigrationTests\|CanonicalSchemaRefusalTests` | 88 passed; `/private/tmp/tenant-durable-approval-infrastructure-final-verified.log`. Includes both-store transactions, race/reopen, same-root suppression, exact/scoped inventory and canonical schema/Run/IAM regressions. |
| Final Infrastructure filters `Durable_request_creation_rechecks\|Concurrent_same_root_preparation\|Durable_decision_commits\|Durable_approval_retains` | 18 passed; `/private/tmp/tenant-durable-approval-final-commit-and-dedupe.log`. Includes four additional both-store cases for commit-time running lease expiry and concurrent same-root CAS loss, plus decision/reopen regressions; overlaps the broad filter above. These claim tests persist a domain claim in the disposable fixture to verify the store contract, not to claim production tenant worker execution. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`, filters `TenantSessionAccessTests\|DiagnosticLoggingTests` | 7 passed; `/private/tmp/tenant-durable-approval-api-final.log`. Actual Synthetic cookie/CSRF HTTP and existing worker failure-diagnostic paths. |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj`, filters `ToolRegistryPolicyTests\|AgentRunDurabilityTests\|SessionRuntimeCompletionTests\|BackgroundCompletion\|ToolActionPreparationTests\|EmailToolTests` | 88 passed; `/private/tmp/tenant-durable-approval-application-final.log`. Existing Synthetic execution, retry/completion and exact legacy preparation regressions. |
| API/Infrastructure builds | Passed with zero warnings/errors. |
| `dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build` | No pending model changes; `/private/tmp/tenant-durable-approval-model-check.log`. Existing EF tools 10.0.5/runtime 10.0.12 warning remains; generation/comparison succeeded. |
| Changed documentation and whitespace | 12 Markdown files, 770 local links, 200 fragment anchors, balanced fences and 20 complete JSON examples passed; `git diff --check` passed. |

## Schema and verification limits

The additive migrations are `20261017015020_TenantDurableApprovals` and `20261017020353_ReleaseTenantApprovalExecutionSlot`. Existing strict canonical startup/refusal policy remains. No installed database or user history was reset or migrated by this work. Disposable integration fixtures exercised the schema.

Tenant worker/model claim/dispatch and actual live mailbox/Hub/audio admission remain pending. The running-claim assertions are domain/persistence contract evidence, not a production tenant worker executing a model. This typed preparer is not yet a model-offered management tool; capability/self-management/safety eligibility and exact effect authority must be integrated before enabling it. Unsupported tenant effects remain fenced, including old-style tool approvals.

Required next work includes loading the saved exact operation/outcome on continuation; protected effect claim/consume, current grantor/policy/target/binding/capability revalidation, unconsumed-grant revocation and indeterminate reconciliation; additional operation/resource families and trusted continuation/autonomous ingress; aggregate/requester/notification storm controls; complete tenant Approvals/Chat/Access UX and browser qualification; remaining global-authority/schema/decommission cutover. Approval records and grants here are not evidence of completed protected effects.

No hosted provider call, user-data reset, application deployment/restart, CI or milestone acceptance was requested or performed.
