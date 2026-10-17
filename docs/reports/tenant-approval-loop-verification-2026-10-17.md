# Semantic approval refusal and storm controls

Date: 2026-10-17. Baseline: `78086ad4c58e4d5638239404bcf847b4ef707c40`. This follows the [cross-Run approval checkpoint](tenant-cross-run-approval-verification-2026-10-17.md). The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; this is a focused correction for proposal §8.4, without milestone acceptance or installed-data changes.

## Reproduced and corrected

The integrated preference scenario prepared an authorization request, recorded a human rejection, resumed the same Run root, changed the Instance configuration revision and proposed the same arguments again. Both adapters incorrectly returned RequiresApproval with a second request. The new semantic correlation returns Denied `previously_rejected`, the original request ID and no additional request/audit. Pending, Cancelled and Expired variants retain their corresponding stable results across revision changes.

Semantic correlation includes the closed operation kind, permission, actual target and normalized typed arguments, excluding operation IDs and mutable Instance/persona witnesses. Exact ActionHash remains revision-bound for human decisions, grants, receipts and effects. SQLite derives the semantic JSON from validated canonical action content through parameterized queries. The existing legacy-named Fingerprint index/foreign-key column remains the exact hash. Old payload getters remain readable; no migration or database reset is needed.

Direct preference service and store transactions reject the same-root action after a refusal, including when the executor subsequently gains ordinary update permission. Historical duplicate request resolution prefers a terminal refusal, then Pending, then Approved, with deterministic timestamp/ID ordering. A conflicting fresh approved effect records Denied `root_operation_conflict`, leaves configuration unchanged and revokes its exact available grant; immutable prior effect receipts retain their existing replay behavior.

Approval admission limits pending-or-created-within-one-minute requests to 128 per tenant, 32 per executor, 32 per original requester and 8 per semantic operation. Null requester shares one bucket. Root lifetime is limited to 32 distinct requests across executors. Cutoffs use the actual store clock. Duplicates resolve before quotas. Limit failure returns structured Denied and commits no request, wait, security revision or request audit. These controls bound the current durable inbox; no separate notification transport was introduced.

## Executed scenarios

All 24 newly added focused cases passed on disposable SQLite and memory stores:

- Reject/cancel/expire/retain Pending, change target revision, repeat the same preferences: return the original stable result and request ID with one request/audit.
- Reject, grant the executor read/update, claim the resumed Run, attempt direct service and direct store execution: deny `previously_rejected`; no receipt, setting change or security revision.
- Admit eight independent roots for the same operation, deny the ninth atomically, advance the clock while requests remain Pending, then reject them: Pending stays charged; older terminal rows release rolling capacity. A duplicate still resolves at the limit.
- Vary material arguments through 32 same-root requests with decisions/resumption and clock advancement: the root ceiling persists beyond the rolling window and a duplicate refusal spends no additional slot.
- Spread independent requests across five executors/requesters, or isolate one executor/requester while varying the other dimension: each respective limit denies exactly the overflow request, without relying on root/operation dedupe.
- Use Unicode, quotes and HTML-like persona text: changing witnesses keeps semantic correlation but changes exact ActionHash; a material argument or new root does not match.
- Seed retained legacy duplicate history with the old serialized getter, then attempt the approved exact operation: prefer the refusal, deny the fresh effect, revoke the matching grant and preserve its exact hash.

## Focused regressions

All 411 focused regression tests passed: 53 Domain, 101 Application, 225 Infrastructure and 32 hosted API. The 24 new cases are included in the Infrastructure total. The hosted Synthetic journeys exercise actual tenant authorization, durable wait, human decision, successor execution, exact effects, cancellation, admission deferral and SQLite recovery.

Commands (all with `--no-restore --nologo`; filters below are literal OR expressions):

| Project under `tests/` | `--filter` | Result |
| --- | --- | --- |
| AgentCore.Domain.Tests | `FullyQualifiedName~ActivationAdmissionTests\|FullyQualifiedName~AgentRunContractTests\|FullyQualifiedName~ExecutionBudgetPolicyTests` | 53 passed |
| AgentCore.Application.Tests | `FullyQualifiedName~AgentRunDurabilityTests\|FullyQualifiedName~TenantApprovalToolContractTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~ToolRegistryPolicyTests` | 101 passed |
| AgentCore.Infrastructure.Tests | `FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests\|FullyQualifiedName~TenantRunAdmissionTests` | 225 passed |
| AgentCore.Api.Tests | `FullyQualifiedName~TenantSessionAccessTests` | 32 passed |

The command form is `dotnet test tests/<Project>/<Project>.csproj --no-restore --nologo --filter '<filter>'`. New scenarios were executed first with method-name filters before expanding to these affected regressions. Final whitespace checks passed. The five changed Markdown documents passed local link/fragment/fence validation (269 links).

## Remaining boundaries

This correction covers the current persona/preference approval path and durable inbox admission. Other protected families, runtime credential use, complete authorization-decision audit/proactive revocation, notification delivery, full tenant Chat/output/audio and approval UI, and obsolete-authority/schema removal remain open. Synthetic/offline fixtures use disposable data and no live provider calls. Browser/hardware behavior was not changed or requalified. CI, deployment and live-provider acceptance were not requested or run.
