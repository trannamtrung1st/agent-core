# Global Memory recall retirement — 2026-10-23

This follows `92e6d68d` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It retires the remaining Runtime global learned-Memory readers; it does not establish complete proposal acceptance.

## Source cutover

`SessionRuntime` and `SessionRuntimeFactory` no longer accept or resolve `IStructuredMemoryService`. Initial context does not load global Session/IdentityUser/User facts, and live context restriction does not re-enter a global reader. Private Memory, Experience and continuity sections are removed before current canonical source projection. Actual tenant owners, raw permissions and source witnesses remain authoritative before provider/output use.

`SessionMemoryPrompt.LoadAsync` and `LoadOwnerAsync`, their unused layered-recall helpers and the unused `memory_retrieval` meter are removed. The class retains pure projection/rendering and admission/subject/privacy helpers consumed by canonical validation. No replacement empty reader, compatibility constructor, new permission, worker, framework or migration is introduced. `SessionManager` retains its separate structured-store dependency for lifecycle deletion; that is not a Runtime recall service.

The shared Runtime fixture and its exact callers are adapted to the reduced constructor. Seven Profile-based recall/Runtime journeys are retired with the removed reader, including the now-empty P4 fixture. Historical milestone reports retain their original evidence. Pure secret/unheard-tail/trusted-subject checks now project already-materialized facts, without another private reader.

| Retired concern | Current replacement evidence |
| --- | --- |
| Enabled/disabled policy, Session and User/Instance boundaries | Canonical `Tenant_learned_memory_*` HTTP/model and owning-store cases: independent raw read/manage, admitted/current restrictions, another requester/tenant and actual source owner. |
| Saturated Session hints starving cross-Session facts | `TenantMemoryProjectionTests`: canonical projection reserves IdentityUser/User evidence under item and character saturation, preserves deterministic order, and excludes resolved/corrupt facts. |
| Runtime injection through the old Profile service | New actual HTTP/model test with a throwing global resolver; learning stores three owned scopes, later recall includes private Evidence, no non-Evidence layer gains the body, and global resolver calls stay zero. |
| SQLite reopen and retained source/provenance | Actual application-host recreation plus owning-store upgrade/retention/CAS/witness tests. |
| Closing work and repeated close | New canonical HTTP/model journey in both stores: stored → resolved → alreadyResolved receipts, active recall exclusion and independently readable retained content/ID; the throwing global resolver stays unused. |

The no-global test is not merely a constructor or health assertion: it submits actual tenant requests, runs the provider/worker, checks durable history receipts, and observes a later Run's private Memory section. The close/repeat journey also reads the retained fact through canonical human HTTP inspection.

## Focused verification

**116 distinct focused backend cases pass**, zero failed/skipped: 50 Application, 25 API and 41 Infrastructure. The first two no-global API cases are included again in the complete 25-case group and counted once.

| Group | Observed result and log |
| --- | --- |
| Pure privacy/formatting, canonical saturated projection, unowned unavailable receipts, prompt/telemetry contracts | **50 passed**, 431 ms; `/private/tmp/legacy-memory-recall-contracts-20261023.log`. |
| First runnable no-global HTTP/model workflow | **2 passed**, 12 s, both stores; `/private/tmp/legacy-memory-recall-no-global-runtime-20261023.log`. |
| Canonical HTTP/model learning and recall | **25 passed**, 1 min 40 s; `/private/tmp/legacy-memory-recall-canonical-runtime-20261023.log`. Includes close/repeat, permissions/policy changes, source/requester isolation, receipt persistence, actual SQLite host restart and Session deletion retention. |
| Canonical owning stores | **41 passed**, 2 min 16 s; `/private/tmp/legacy-memory-recall-owning-stores-20261023.log`. Includes atomic scope changes, current raw rights/CAS, owner/source witnesses, provenance and SQLite retention guards. |
| Final serial solution build | **Passed**, zero warnings/errors in 1.74 s; `/private/tmp/legacy-memory-recall-build-final-20261023.log`. |

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`, with each term prefixed `FullyQualifiedName~`:

```text
Application: SessionMemoryPromptTests|TenantMemoryProjectionTests|Unowned_inline_proposals_report_unavailable|MemoryReceiptProjectionTests|RuntimeTelemetrySurfaceTests|PromptContextBuilderTests
First API: Tenant_learned_memory_Runtime_does_not_resolve_global_recall
API and Infrastructure: Tenant_learned_memory_
```

Builds use `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Initial build attempts exposed only a missing test namespace import for `MemoryLimits`; the corrected solution and the final API fixture build pass with zero warnings/errors. Tests use Synthetic providers and disposable data only. No UI/wire/schema change, user-data reset, Real host restart, live provider call or CI was requested or performed.

## Remaining scope

The full goal remains active. Other internal global-service/Profile roots, broader legacy fixture migration/retirement, approval-default review, safe preparation/schema consolidation and final proposal-wide integration/security/isolation/rendered UI acceptance still require work. Historical unowned Memory success fixtures are not current tenant acceptance evidence; the testing strategy now names the canonical owners and replacement workflows. This report does not claim that the complete test suite or proposal has been accepted.
