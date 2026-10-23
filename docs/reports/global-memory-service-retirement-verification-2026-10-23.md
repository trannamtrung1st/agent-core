# Global Memory admission/service retirement — 2026-10-23

This follows `32093c08` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). Canonical tenant Memory retains its existing admission, consent, response transaction and source owners. This module removes the remaining global service/admission authority; it does not establish complete proposal acceptance.

## Removed paths and preserved rules

- Removed `IStructuredMemoryService`, `StructuredMemoryService`, global `MemoryAdmission`, its test interceptor and the DI registration. Profile-based writes, updates, promotion, retrieval, reset and direct trusted-command deletion no longer have an Application service facade.
- Removed the unused global OpenLoop resolution store methods/partials, three caller-built trusted-owner records, service-only proposal/query DTOs and resolution result. Canonical tenant close/repeat and protected forgetting retain their own transactions. Historical physical rows, `IStructuredMemoryStore` lifecycle primitives and installed migrations are unchanged.
- Extracted `MemoryContentRules` with the exact original validation body, static secret patterns and reserved subjects. Normalization, content/provenance/source-entry bounds, forbidden/unheard fragments, trusted-subject collision rejection and rejection telemetry are preserved. The rules have no store, owner selection or IAM authority. Canonical Memory operations, Experience, continuity and pure formatter callers use these rules directly.
- Replaced the throwing global resolver instrumentation in actual tenant Runtime tests with built-assembly absence assertions for the retired service port/admission type. Actual three-scope learning, private Evidence, close/repeat and retained body/ID assertions still execute.
- Retired the obsolete opt-in global identity-maintenance scenario. Its shared live-test opt-in/key helper remains for the separate existing fixture, with unchanged opt-in/key behavior; no live provider check is claimed.

Canonical interface/persistence/testing text now points to owning tenant contracts instead of repeating the retired Profile-based write/promotion description. No schema, migration, policy floor, transport shape, execution framework or frontend change is introduced.

## Coverage migration

The removal manifests retain **54 obsolete test method bodies across nine fixture paths**: `/private/tmp/global-memory-service-retirement-test-manifest-20261023.json` and `/private/tmp/global-memory-service-retirement-files-20261023.json`. They include exclusively global service fixtures and exact obsolete methods in mixed fixtures; they are not counted as passing current tests. Pure parser/immutable Definition, marker source-spoof rejection, unowned Runtime refusal, formatter/privacy, lifecycle pin/retention, Session deletion, Secretary Skill/Artifact and accepted-work checks remain.

Mixed lifecycle tests now insert raw historical rows only to verify persistence, archive retention and physical cleanup. They no longer invoke an admission service or claim canonical tenant authorization.

| Retired concern | Current qualification |
| --- | --- |
| Global scope writes, corrections, promotion, isolation and reopen | Existing canonical learning/management/store cases exercise actual tenant/User/Session identity, independent raw permissions, admitted/current policy, immutable source/value identity, private copies, response CAS rollback and SQLite restart. |
| Staged proposals after failure/cancellation, replacement and repeated semantic output | Eight new actual HTTP/model/worker/store cases, both stores. Provider failure after semantic staging and actual human Run cancellation leave all three scopes empty and publish no save receipt. Replacement stores only the replacement value; repeated output produces one assistant entry and one fact per scope with a factual receipt. |
| Content/privacy and trusted fields | 28 new static-rule cases cover all five kinds, unchanged normalization, eight shape bounds, reserved/occupied fields, secret formats, forbidden fragments in subject/body and the encoded-blob threshold. Existing formatter voice/unheard privacy and canonical projection caps remain. |
| Structured/function/marker source transport | Existing offline semantic-parser/provider cases retain source restrictions, malformed proposal diagnostics and upsert/resolve/legacy-delete normalization. Format parsing never grants write authority. |
| Global loop close/repeat and forgetting | Actual canonical close/repeat keeps stored/resolved/alreadyResolved receipts and independently readable retained content; exact protected operations retain ordinary/exceptional consent, primary-only grants, source revalidation and factual replay. |

## Focused local checks

| Group | Observed result |
| --- | --- |
| Application content rules, formatter/receipts, projection and retained lifecycle | **85 distinct cases qualified**: 84 initially pass; the corrected 28-case privacy group passes and supersedes its one initial assertion failure. |
| First runnable staging/no-global/close-repeat workflows | **12 passed**, zero failed/skipped, 46 s. |
| Canonical learning/management HTTP, Runtime and restart | **38 passed**, zero failed/skipped, 2 m 23 s. Includes the first 12 cases again; they are counted once. |
| Canonical Memory/Experience/continuity stores, protected operations and semantic parsers | **120 passed**, zero failed/skipped, 4 m 25 s. |
| Final serial solution build | Passed, zero warnings/errors, 5.65 s. |

**243 distinct focused backend cases are qualified**: 85 Application, 38 API and 120 Infrastructure. Repeated first-workflow and privacy-recheck identities are counted once.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

```text
Application: FullyQualifiedName~MemoryContentRulesTests|FullyQualifiedName~SessionMemoryPromptTests|FullyQualifiedName~MemoryProposalAdmissionTests|FullyQualifiedName~ExplicitUserMemoryTests|FullyQualifiedName~MemoryReceiptProjectionTests|FullyQualifiedName~TenantMemoryProjectionTests|FullyQualifiedName~AgentInstanceTests|FullyQualifiedName~SessionCatalogTests|FullyQualifiedName~ManagedInstanceP7DRegressionTests
Privacy recheck: FullyQualifiedName~MemoryContentRulesTests
First Runtime: FullyQualifiedName~Tenant_learned_memory_staging|FullyQualifiedName~Tenant_learned_memory_Runtime_does_not_resolve_global_recall|FullyQualifiedName~Tenant_learned_memory_close_and_repeat
Runtime/HTTP: FullyQualifiedName~Tenant_learned_memory|FullyQualifiedName~Tenant_human_memory
Stores/parsers: FullyQualifiedName~Tenant_learned_memory|FullyQualifiedName~Tenant_human_memory|FullyQualifiedName~Tenant_memory_operation|FullyQualifiedName~Tenant_continuity_historical_projection|FullyQualifiedName~Tenant_experience_inspects_records_recalls_and_reopens|FullyQualifiedName~Function_channel_invalid_memory_proposal|FullyQualifiedName~Model_output_drops_application_and_admin_memory_sources|FullyQualifiedName~NativeSemanticResponseParserTests|FullyQualifiedName~MarkerSemanticResponseParserTests
```

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs use `/private/tmp/global-memory-service-retirement-` with `build-final-20261023.log`, `contracts-20261023.log`, `privacy-final-20261023.log`, `runtime-first-20261023.log`, `runtime-20261023.log` and `stores-20261023.log`.

The first compile found one removed parser-fixture constant; its attribute now retains the same literal expectation. The first privacy group rejected content correctly, but its disclosure assertion matched `en` inside the generic word “content.” The corrected assertion checks the exact safe generic messages; no product validation was relaxed. Passing unaffected cases are retained rather than repeating their full group after this test-only correction. The delivery ledger now uses one retirement table, preserving all 12 prior evidence targets and the existing incomplete-work boundaries; detailed counts remain in their owning reports.

Other global/internal Profile/Experience/Automation roots, historical fixture reconciliation, final store/tool authority inventory, safe retained preparation/schema consolidation and proposal-wide security, isolation, functional and rendered UI acceptance remain open. No new UI/browser qualification is claimed. All executed state is disposable offline Synthetic. No CI, hosted provider, user-data reset, Real restart, push or deployment was performed. The full goal remains active.
