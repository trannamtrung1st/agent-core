# Approved workspace mutation effects — 2026-10-19

This continues the full proposal from consent checkpoint `8772b043`. It integrates trusted approved mutation execution and body-free continuation with the existing Run, approval, grant, receipt and canonical filesystem owners. Model offering and the shared inspector remain pending; the full proposal stays active.

## Execution and recovery ownership

`TenantWorkspaceMutationEffectService.ExecuteApprovedAsync` accepts only a trusted Run reference and exact saved request/operation/hash/claim. Supplied native arguments come from the stored, rebuilt `PreparedWorkspaceMutation`. Current admitted/current capability and HomeAccess, actual owner/lifecycle, current raw prerequisites and secondary permissions, primary Review permission or exact grant, current deciding-human qualification/independence, expiry and private source witnesses gate execution.

The existing workspace operation store atomically claims an Available exceptional grant and reserves the existing approved Run external-effect origin with security revision and audit. No direct-call metadata, extra dispatch table, file version, content snapshot or alternate execution engine is added. The canonical adapter alone performs I/O outside the transaction. Current owner/claim/grant/authority callbacks repeat at native boundaries. Write/patch repeat their full private source witness at commit. Structural operations bind their complete initial footprint before native work and at their first callback, then retain current authority checks without comparing their own ordered prefix against the initial state. Ordinary concurrent filesystem semantics and possible partial copy/batch effects remain explicit; there is no filesystem CAS, rollback or all-tree atomicity.

Settlement atomically records an immutable body-free request-bound receipt, factual Run disposition, consumed/revoked/Indeterminate grant and audit. A live exact successor can seal its bounded tool result and pending-runtime-admission marker in that same transaction. An unusable delivery checkpoint or exhausted headroom must not erase already completed facts. The existing off-mailbox continuation phase performs native work; mailbox resumption consumes its receipt only. Unknown outcomes use `applied:null`, explicit possible partial effects and `replayAllowed:false`. Caller cancellation bounds waiting on noncooperative native tasks while retaining supervised completion and durable uncertainty.

Abandoned/unsettled dispatch recovers uncertain through retained Run/Session ownership after lease expiry, current access loss or Session deletion. Factual settlement never reopens files or repeats source/permission qualification. It does not require the live resource to remain readable. Known exact receipts are factual results, never authority for another operation.

## Executed development evidence

The first representative eight cases passed across both stores, home/working and Review/exceptional grants (`/private/tmp/agent-core-approved-workspace-effects-first.log`). They approved a blind literal overwrite, applied exactly its saved bytes, consumed only its exact grant, verified a body-free request-bound receipt, checked one dispatch/result audit and returned the same receipt without another native call.

The successor/recovery expansion passed 16/16 (`/private/tmp/agent-core-approved-workspace-effects-continuation.log`), adding a real saved authorization checkpoint → durable successor → off-mailbox effect → atomic receipt/checkpoint → mailbox result workflow. It also rejected changed private source before dispatch, retained terminal lost-acknowledgement uncertainty and recovered an unavailable settlement after claim expiry without replay.

The next 30/30 run passed (`/private/tmp/agent-core-approved-workspace-effects-boundaries.log`): current Review permission loss before/at commit, source replacement at commit, concurrent callers, mixed ordered delete/mkdir under an exact delete grant, and abandoned dispatch after access loss or Session deletion. SQLite reopen recovered the exact retained fence. Native invocation counters remained zero during recovery; terminal grants became Indeterminate, and repeated recovery found nothing to dispatch.

The 50/50 workflow run passed (`/private/tmp/agent-core-approved-workspace-effects-workflows.log`). Additional scenarios exercised patch/mkdir/cross-root copy/move/delete from saved arguments, a failed batch retaining only its completed prefix, current independent-review policy invalidating the original reviewer/grantor, expiry while the claim remained live, and cancellation returning durable uncertainty before a deliberately blocked native task resumed. Partial results were checkpointed with `applied:null` and no replay. No earlier failed build/test run is being counted as passing.

Final qualification adds complete tree-copy/empty-directory coverage, deterministic acknowledgement that the cancelled task has finished, and completed-fact retention when the delivery checkpoint changes after native I/O. Selected existing direct-effect, consent and shared continuation regressions are included in the results below.

The first changed-delivery-checkpoint cases returned Indeterminate because their post-write fixture attempted to reset consumed execution budget. Core correctly rejected that reset, turning the hook into a lost acknowledgement. The fixture must preserve current budget counters to isolate the intended delivery-state boundary; this failed run is not qualification evidence.

After preserving the actual checkpoint's consumed resources, the targeted final-source recheck passed **20/20** (`/private/tmp/agent-core-approved-workspace-effects-delivery-fixed.log`). It verifies retained completed facts after checkpoint substitution, canonical tree copy including empty directories, deterministic completion of the cancelled native task without late mutation, and approved-only retained-owner recovery. The previous broader attempt had 120 passes and these two fixture failures; it is not counted as a passing qualification run.

## Final focused qualification

The corrected final store/continuation matrix passed **122/122** (`/private/tmp/agent-core-approved-workspace-effects-final.log`): all fifty-four new approved-effect cases plus sixty-eight affected direct-effect, consent and shared continuation regressions. Both stores completed the changed-delivery-checkpoint case without erasing completed facts, tree copies preserved empty directories and cancelled native tasks acknowledged completion with original file bytes unchanged. Existing Browser receipt/checkpoint, Workspace read-consent successor and durable approval workflows passed alongside the new implementation.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_approved_workspace_effect|FullyQualifiedName~Tenant_workspace_effects|FullyQualifiedName~Tenant_workspace_mutation_Review|FullyQualifiedName~Tenant_Browser_model_checkpoint_and_receipt|FullyQualifiedName~Workspace_read_Review_returns|FullyQualifiedName~Durable_approval_retains' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The focused Application contract check passed **23/23** (`/private/tmp/agent-core-approved-workspace-contracts-final.log`). It covers the new full 32 KiB literal envelope and exact-match rules, rejection of read tools/caller authority, all seven typed mutation shapes, forged payloads and existing authorization schema/offering behavior. No mutation authorization branch was added to model offering; a guessed unoffered intent is explicitly denied at the model preparation boundary.

```sh
dotnet test tests/AgentCore.Application.Tests --no-restore --filter 'FullyQualifiedName~TenantWorkspaceMutationApprovalContractTests|FullyQualifiedName~TenantWorkspaceMutationAuthorizationContractTests|FullyQualifiedName~TenantApprovalToolContractTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Actual disposable Synthetic application hosts passed the existing model mutation matrix **16/16** (`/private/tmp/agent-core-approved-workspace-hosted-regression.log`). Direct saved/uncertain outcomes, IAM/policy refusal, admitted/current HomeAccess, malformed arguments, unavailable settlement and completed SQLite host recreation remained correct after extending recovery, required service dependencies and the shared parser. Provider HTTP attempts were zero. These are direct-model regressions, not an unimplemented approved-model journey.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_model_mutations_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Distinct focused qualification totals **161 passing cases** (122 store/continuation + 23 contract + 16 hosted), excluding repeated runs. All build/test handles returned terminal success before commit. Canonical architecture, ports, persistence, testing and implementation status were synchronized; relative documentation file links, fenced blocks and `git diff --check` passed. No schema/migration source changed in this execution checkpoint.

## Remaining full-proposal work

The bounded `mutate_workspace` parser and exact-match support currently validate trusted saved continuations only. They accept the complete 32 KiB literal-write envelope before the ordinary 16 KiB authorization bound, reject caller-supplied authority and do not add a model schema/offering or public preparation route. Model capability/context projection, normal authorization-tool routing, direct-result copy, shared exact-action inspector/Chat labels, actual approved model/HTTP/browser journeys and cold application-host qualification remain required.

Workspace virtual resources/Office/source publication, autonomous origins, other proposal families and final clean authority/schema cutover remain open. No installed/user database migration/reset, Real host restart/deployment, paid provider, CI/full suite or frontend source change was performed for this checkpoint. Store reopen and trusted continuation evidence do not claim unrun end-to-end model/UI acceptance.
