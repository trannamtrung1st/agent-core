# Tenant credential-check execution and no-repeat recovery — 2026-10-17

This checkpoint implements the trusted backend execution/recovery boundary after the exact approval-target prerequisite. The full multi-tenant IAM proposal stays active. It does not enable the credential-check operation in the model tool or claim complete tenant/cross-Run/browser acceptance.

## Delivered behavior

`TenantCredentialCheckService` consumes only actual saved request/operation/Run/generation/hash references. Preparation and human decisions still use the existing durable approval engine. Executor credential-read, current use/review policy, qualified grantor, current requester, actual pinned binding, purpose capability, connected credential, target revisions/epoch, expiry and host gates apply before dispatch. Mandatory Sensitive review is preserved; no role or human-only authority is delegated.

The store transaction reserves one unfinished dispatch per credential, marks the Run in flight and moves the exact exceptional grant from Available to Claimed. Only a newly returned dispatch fence may invoke the existing Google verifier. A duplicate has no provider authority. The service bounds provider work by 30 seconds and remaining lease, recomputes time after persistence, observes late task failures and never retries refresh. Provider values/bodies stay behind the existing Infrastructure adapter; no bearer token reaches model, receipt or audit.

The completion transaction writes protected rotation or reconnect health, immutable receipt, grant result, closed fence, Run marker and security/audit. A known result preserves valid protected rotation even when the previously authorized dispatch completes after logout, role loss or Run cancellation. It restores no authority and never revives a terminal Run. A changed logical/secret/auth-epoch witness wins over the late result. A fault, timeout, invalid result or stale target records Indeterminate; the unchanged original connection becomes reconnect-required. Indeterminate grants/receipts cannot authorize another call.

The existing hosted worker closes abandoned expired/terminal dispatches without invoking a provider. The narrowly retained ownership read works after Session logical deletion and grants no runtime/history access. A late completion cannot overwrite its saved uncertain receipt. SQLite adds composite ownership/lineage FKs, a partial unique active-credential index and a lossy-downgrade guard, including completed fences.

## Executed backend scenarios

Both-store integration cases execute exact consent, resumed claims, protected rotation, stable receipt replay after adapter reopen and logout, actual bounded Google token/profile transport through an offline HTTP handler, concurrent exact callers, independent-root credential exclusion, permanent reconnect and uncertain provider failures, current read/use/host/witness denial, newer grant races, noncooperative timeout, expired/Session-deleted recovery, late-result suppression, cancellation without Run revival and completion without restoring revoked roles. A delayed dispatch-return fixture proves an expired claim cannot begin provider I/O after the fence commit. SQLite verifies exact credential/Run/action foreign keys and refuses downgrade after a completed fence. The first boundary pass caught a real millisecond-rounding race; Run transition precision is now preserved.

Actual WebApplicationFactory Synthetic HTTP scenarios on InMemory and SQLite create/connect/bind a credential, admit/claim a Run, prepare the exact request, approve/reject through cookie/CSRF APIs, execute through the composed trusted service and read completed or uncertain receipts. They assert the existing secret-redaction contract, consumed/indeterminate grants, immutable replay and zero provider networking. These are not model-issued checks.

All backend commands use `dotnet test <project> --no-restore --nologo --filter <filter>`.

| Project / selection | Result |
| --- | --- |
| `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`; `FullyQualifiedName~TenantOperationAuthorizationTests\|FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantCredentialIntegrationTests` | 391 passed |
| Same Infrastructure project; `FullyQualifiedName~Factual_completion_after_executor_role` | 2 additional cases passed |
| Same Infrastructure project; `FullyQualifiedName~Expiry_during_dispatch_commit\|FullyQualifiedName~Timed_out_noncooperative\|FullyQualifiedName~Factual_completion_after_executor_role\|FullyQualifiedName~Approved_runtime_credential_check_rotates` | 8 passed; includes 2 additional expiry cases, otherwise overlaps |
| Same Infrastructure project; `FullyQualifiedName~Uncertain_runtime_check\|FullyQualifiedName~Abandoned_credential_dispatch\|FullyQualifiedName~A_newer_credential_grant\|FullyQualifiedName~Timed_out_noncooperative\|FullyQualifiedName~Expiry_during_dispatch_commit` | 14 passed after final audit-status correction; overlaps |
| Same Infrastructure project; `FullyQualifiedName~Runtime_credential_check_uses_the_actual` | 4 passed; adds 2 Gmail cases to the earlier both-store Drive coverage |
| `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`; `FullyQualifiedName~TenantSessionAccessTests\|FullyQualifiedName~TenantCredentialAccessTests\|FullyQualifiedName~AgentRunHostedServiceTests` | 54 passed |
| Same API project; `FullyQualifiedName~Credential_approval_HTTP` | 6 passed on final lease/audit implementation, including receipt and audit readback; overlaps |
| `tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj`; `FullyQualifiedName~TenantApprovalToolContractTests` | 5 passed |
| `web/`; `pnpm run test --run src/features/access/TenantApprovals.test.tsx src/features/access/TenantAudit.test.tsx` | 17 passed |
| `web/`; `pnpm run build` | Passed; existing large-chunk warning remains |

**473 distinct focused tests passed:** 397 Infrastructure, 54 API, 5 Application and 17 frontend. `git diff --check` and changed Markdown link/fence checks passed. Earlier minimal/race/migration runs and final focused reruns overlap these totals; no case is counted twice. Backend/HTTP builds passed; the frontend build retains its existing large-chunk warning. No optional CI or live-provider acceptance was run.

## Inspector verification

The existing AntD table and Drawer share truthful receipt labels: Check completed, Not applied and Outcome uncertain. Credential/binding field names and precise reconnect/target-change reasons replace misleading generic outcome copy. No CSS, spacing, token system or UI kit was introduced.

Review also found the older two-outcome assumption in Audit. The typed effect transaction now records an explicit `indeterminate` audit result, while generic IAM decision append remains closed. Audit displays uncertainty distinctly from denial and labels confirmed connection checks correctly. Actual HTTP readback verifies the persisted audit status, and UI regressions cover the corresponding labels and exact references.

Playwright MCP used this task's disposable Synthetic InMemory host on 55292 and Vite on 55293, with a privately generated fixture login. The UI checks used normalized approval-read fixtures; backend persistence/provider effects were verified separately by the actual HTTP scenarios above. At 1440, 768, 390 and 320 px, the inspector showed uncertainty without false Applied/Not applied labels or decision buttons, kept its footer reachable and had no body/document horizontal overflow after responsive transitions settled. A failed refresh retained uncertainty; keyboard refresh then displayed a confirmed reconnect-required check. No approval POST occurred. No page errors occurred; the one console 503 was deliberately injected for read recovery. Early automation VM setup errors were corrected and are not product failures.

Evidence: `/private/tmp/agent-core-credential-ui.Imod3K/result-confirmed-{1440,768,390,320}.png`. Both known task-owned servers were stopped after verification; no unknown/Real host or user data was touched. Impeccable reports the existing design sidecar stale; no incidental drift repair was made.

The affected Audit consumer was then checked through Playwright MCP at 1440, 390 and 320 px using normalized audit-read fixtures. Both result labels survived refresh; desktop expansion and mobile Drawer retained exact effect references and Recorded effect result wording without horizontal overflow or a false Not applied tag. There were no page/console errors in that pass. Evidence: `/private/tmp/agent-core-credential-ui.Imod3K/audit-{1440,390,320}.png`. The fixture login file was removed and the restarted task-owned Synthetic/Vite processes were stopped after this check.

## Review follow-up after `b51746de`

Review reproduced a false-success path in `TenantCredentialCheckService`: the provider result was assigned before the final configuration recheck, and an exception from that recheck left the candidate result intact. Both InMemory and SQLite regressions failed before the fix with expected Indeterminate versus actual Completed. The catch now clears that result, so an unverified rotation cannot be committed as success.

The two store regressions now verify an immutable uncertain receipt, reconnect-required health with no retained candidate secret, an Indeterminate grant and Run marker, an indeterminate audit with no raw provider error, and replay with only one verifier call. Two additional integrated cancellation cases cancel the caller while a noncooperative verifier is pending: the service saves and closes an uncertain dispatch before returning cancellation, and a late successful rotation cannot restore the credential or cause another provider call.

The focused four-case fix/cancellation selection passed. The six existing Synthetic HTTP cases also passed: actual credential create/consent/bind/admission, human approve/reject, trusted service execution, and completed/uncertain receipt plus audit readback on both stores, with zero outbound provider networking. No frontend source or presentation contract changed; earlier browser evidence remains scoped to the inspector fixtures above. Natural model initiation and Run-B provider consumption remain unverified and disabled as described below.

Final checks passed with **168 distinct cases** (the four new cases are included in the 162):

- `dotnet test tests/AgentCore.Infrastructure.Tests --no-build --no-restore --filter FullyQualifiedName~TenantCredentialIntegrationTests`: 162 passed, none failed or skipped.
- `dotnet test tests/AgentCore.Api.Tests --no-restore --filter FullyQualifiedName~Credential_approval_HTTP_projects_exact_consent`: 6 passed, none failed or skipped.
- `git diff --check` and local Markdown link/fence checks for both changed documents passed. CI and live Google acceptance were not run; this follow-up does not claim milestone or proposal completion.

## Successor and remaining work

This backend checkpoint left model intent, capability offering, off-mailbox host dispatch and exact Run-B consumption pending. The [model credential-check successor](tenant-model-credential-check-verification-2026-10-17.md) now records that implementation and its hosted Synthetic evidence. Subsequent [active Run cancellation verification](tenant-active-run-cancellation-verification-2026-10-17.md) adds HTTP-to-mailbox stop, including during a claimed credential check. Live tenant Chat/notifications and live Google acceptance remain separate gaps. Other credential/external action families, live Google acceptance, tenant transport/resource cutover, obsolete authority removal and full proposal gates remain pending. No CI, live provider call, installed-data migration/reset or milestone acceptance was performed.
