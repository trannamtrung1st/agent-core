# Active tenant Run cancellation — 2026-10-17

Successor to `f103ef30` and the [model credential-check checkpoint](tenant-model-credential-check-verification-2026-10-17.md). This delivers authenticated whole-Run cancellation through the existing tenant host and Session Runtime. It advances the proposal's execution/control boundary; it does not complete a phase or the full cutover.

## Delivered behavior

The existing `POST /api/v2/tenants/{tenantId}/agent-runs/{runId}/cancel` body stays `{ expectedRevision, expectedRunRevision }`. It requires the actual current human/session, exact Run cancel/read, owning Session read and Instance chat. Preparation records trusted owner/Session/Run/generation references and revision witnesses, never copied permissions or model-selected identity. The owning mailbox repeats current authority and reference checks before the transaction. A terminal or stale request conflicts; denial leaves work running. The target is one exact Run, not a sibling or successor selected by implication.

`ITenantRunControl` shares the production dispatcher singleton. Matching live work receives an urgent mailbox command; absent/released ownership uses a host reservation against admission. The command commits the Domain terminal cancellation before superseding local execution. Existing schema/rows and Run transitions remain the source of truth; no Task entity, scheduler or public debug/effect endpoint is added.

The transaction closes the Run and only saved Streaming Assistant entries for its exact response, preserving text and received/heard offsets. Changed entries become Interrupted with userStop and a completion timestamp. Only that change advances Session revision; Session transport/lifecycle state is retained. Security revision and safe cancellation audit commit with the Run. Running work uses both Domain request/commit transitions atomically; queued/waiting work uses its existing terminal transition.

Pending approval becomes Cancelled, or Expired if its deadline passed; cancellation attribution records the actual stopper while the Run's original requester stays unchanged. Whole-Run stop produces no continuation signal. Approved decisions remain historical consent, and Available exact grants revoke. Claimed, Consumed and Indeterminate evidence remains available for factual completion/recovery. The stopper needs no approval decision/cancellation or credential-use permission: this cleanup authorizes no protected effect. Standalone approval cancellation still resumes its exact continuation under the existing contract.

After commit the mailbox supersedes the response before cancelling workers, disables queued suffix dispatch and suppresses pending/stale saves. Host provider/Runtime cancellation callbacks execute outside the registry lock. Shared capacity releases even while a noncooperative model has not returned. A client disconnect after mailbox admission does not abandon the admitted stop. Already-dispatched provider work may settle afterward; cancellation cannot undo that effect, revive the Run or permit another dispatch.

## Executed scenarios

| Scenario and expected result | Observed evidence |
| --- | --- |
| Actual authenticated HTTP stop during deliberately noncooperative generation; stale request rejected, saved response closed and capacity reusable before releasing the model | Both InMemory and SQLite hosted cases assert Cancelled, one security/audit write, rejected old-frame save, a separate completed Session, then no late text in the retained interrupted response. The inspection turn is stopped through HTTP too. |
| Saved partial response with a claim timestamp finer than one millisecond, attached and detached | Four both-store cases preserve saved text and receipt offsets, close the exact row, preserve Session state, advance Run/security/Session revisions correctly and reject stale writes. |
| Current authority and reference checks between preparation and commit | Eight both-store cases cover permission revocation, logout, forged owner and forged generation, with no cancellation write/audit. |
| Run-only human stops pending/expired/approved consent without approval or use authority | Six both-store cases verify closure/expiry or immutable prior decision, Available-grant retirement, retained requester, exact stopper attribution, no effect receipt/audit and no successor dispatch. |
| Credential check already claimed and held in provider I/O when B is stopped through HTTP | Two hosted both-store cases retain the claimed dispatch fence, settle one Indeterminate receipt, keep B Cancelled and replay the receipt without another verifier or model generation. |

The model-credential fixture now sets one shared Runtime slot and waits for its release after a durable approval wait. This proves source supervisor cleanup before manual negative-path successor dispatch, rather than assuming the durable wait and registry cleanup happen at the same instant. An old SQLite constraint test now expects attached-Session cancellation to succeed; its cross-tenant/executor/profile/requester FK and payload-corruption assertions remain intact. Initial regression failures from those two outdated fixture assumptions were corrected before the final pass.

## Focused verification

**535 distinct affected checks passed**: 470 Infrastructure and 65 API, with none skipped. Both final commands below exited successfully. All fixtures are disposable and Synthetic, with no provider networking.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~TenantSessionAccessTests|FullyQualifiedName~Model_credential_check_waits|FullyQualifiedName~Credential_approval_HTTP_projects_exact_consent' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~TenantRunAdmissionTests|FullyQualifiedName~TenantSessionLifecycleTests|FullyQualifiedName~TenantOperationAuthorizationTests|FullyQualifiedName~TenantPreferenceOperationTests|FullyQualifiedName~TenantCredentialIntegrationTests' --logger 'console;verbosity=minimal'
```

Narrow development runs already passed: active HTTP cancellation (2), saved-prefix/precision and prepare/commit boundaries (12), approval retirement (6), in-flight credential cancellation (2), budget-exhausted successor handling (2) and the SQLite relational/payload constraint case (1). These overlap the final selections and are not counted twice. Changed Markdown local-link targets and balanced fences, plus `git diff --check`, also passed.

## Remaining scope

No frontend code changed. The private text execution/cancellation path has HTTP/mailbox/store evidence; live tenant Chat/history/output, Hub/voice control, recipient delivery revalidation, original-Chat approval notifications, other operation/resource families, autonomous-origin completion and obsolete-authority/schema removal still require the proposal's remaining work. Saved generated text is not a received/heard acknowledgement. No CI, Real provider call, deployment or installed-data migration/reset was performed. The full goal remains active.
