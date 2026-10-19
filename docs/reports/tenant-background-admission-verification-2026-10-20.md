# Tenant immediate-background admission storage qualification — 2026-10-20

This checkpoint implements the atomic ownership/admission prerequisite for the remaining background module in the original multi-tenant IAM proposal. It does not enable model offering, child dispatch/outcome settlement, completion delivery, autonomous occurrences or a public background endpoint. Existing tenant adapters keep those paths closed. No complete-module, milestone or full-proposal acceptance is claimed; the original goal remains active.

## Behavior and ownership

`BackgroundSessionAdmissionFactory.ForImmediate` now supports tenant parents without evaluating the throwing local-profile accessor. It derives a child's trusted tenant/executor/original initiator, source Run/Activation and inherited approval root from the parent's immutable context. Child Sessions carry no local profile and remain separate from their parent Session. Legacy parents retain their existing behavior.

`ITenantRuntimeStore.AdmitTenantImmediateAsync` is an internal server-held-frame boundary implemented by the current InMemory/SQLite IAM owners. Its transaction requires the stored active UserTurn parent, exact current generation, unexpired claim, active tenant/executor/Instance, current Instance/persona revision, current and admitted background capability, a canonical child snapshot/admission, and independent executor `sessions.create` plus `agent-runs.create` authority for future roots. The original human must retain parent read and Agent chat authority. A creation policy requiring Review returns `background_review_required`; this primitive neither grants nor creates consent.

The same parent/owner/tool-call key and unchanged objective/title/report preference returns the original admission under current authority, without another owner, Run, security revision or audit. Changed intent refuses. Per-parent and active-owner capacity use the incumbent limits. The original input, owned Session root, Activation/Run lineage, security revision and body-free actual-executor audit commit atomically. Refusal, stale generation, expired/cancelled source, revoked/missing creation authority, required Review, malformed snapshot and unavailable capability leave no orphan owner/Run or success audit. SQLite store recreation preserves the trusted context, root and exact dedupe.

The existing TenantSessions root retains original-human provenance under its User FK; Run context separately preserves actual Agent execution, and the admission audit names that executor. Provenance is not read permission. No artificial User, local profile, owner-capability shim, duplicate background store, schema column or migration is introduced. Autonomous roots without a human parent still need their own converted ownership boundary.

## Focused local evidence

**28 distinct passing cases**, zero skipped: 22 new admission cases and six affected legacy/runtime regressions.

| Check | Result | Observed boundary |
| --- | --- | --- |
| Owned immediate admission | 2 passed | InMemory/SQLite actual human parent admission and claim, factory-derived child identity, no local profile, inherited root/parent/executor/original human, atomic owned Session/Run/audit, exact fresh-candidate retry, changed-task conflict, foreign-tenant denial; SQLite store recreation retains facts and dedupe. |
| Prepared-admission boundaries | 18 passed | Both stores: wrong generation/tenant, absent/revoked creation authority, expired claim, parent cancellation, creation Review required, injected snapshot and missing current/admitted capability. Strict expected AccessDenied outcomes; every case proves no orphan child, no new success audit and unchanged security revision. |
| Capacity and exact retry | 2 passed | Both stores: two incumbent allowed immediate children, exact retry succeeds at the limit, a third new child refuses without a new owner or security revision. |
| Legacy immediate admission | 4 passed | Existing both-store fresh-candidate receipt dedupe and missing/foreign/cancelled parent rejection remain intact after the shared factory change. |
| Tenant runtime regression | 2 passed | Existing both-store claim/generation/scope/revision/later-input fencing and SQLite reopen continue to work. |

Executed from the repository root:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Tenant_immediate_background_admission|FullyQualifiedName~Tenant_background_prepared_admission|FullyQualifiedName~Tenant_background_exact_retry|FullyQualifiedName~Background_receipt_replay_with_fresh_candidate_ids|FullyQualifiedName~Missing_foreign_or_cancelled_parent_cannot_admit_a_background_child|FullyQualifiedName~Tenant_runtime_fences_generation_scope_revision_and_later_input' --nologo
```

The final batch passed28 cases in29 seconds. It compiles actual Domain/Application/Infrastructure and crosses production IAM, Session, entry, Activation and Run boundaries using disposable stores. This is functional backend integration evidence, not a static-only claim. The SQLite restart distinction is **store recreation**, not an application-host/process restart.

## Corrections and remaining work

Initial compilation caught an xUnit analyzer requirement for filtered `Assert.Single`; it now uses the predicate overload. The first functional batch passed19 of20 and exposed duplicate Entry tracking in SQLite: the Session mapping already includes its original entry, so the extra insertion was removed. Negative tests now require the intended AccessDenied type rather than accepting arbitrary exceptions. The final expanded batch passes all28.

TenantAgentRunStore still refuses immediate admission; runnable/claim/runtime and outcome/completion adapters remain unconverted. This checkpoint deliberately supplies their atomic owner prerequisite without enabling a partially secured execution path. It does not prove tool offering, background model execution, protected child effects, completion notifications, human activity UI, occurrence scheduling, live transport or complete isolation/schema cutover. Those remain required by the original proposal. No frontend source changed, so no browser/UI acceptance is claimed. No provider credentials, external accounts, native browser, Docker or user data were used. Optional hosted/full CI was not requested or run.

[Port owner](../04-backend-interfaces.md#tenant-immediate-background-admission), [backend owner](../12-backend-implementation-spec.md#tenant-immediate-background-admission-storage), [persistence owner](../15-persistence-and-configuration.md).
