# Global Admin Memory and Automation retirement — 2026-10-23

This follows `ab042bed` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). The old local-Profile Admin facades are retired; canonical tenant Memory and Automation retain their existing owners. This is scoped cutover evidence, not proposal-wide acceptance.

## Retired authority and dependencies

- Removed `AdminMemoryService`, its list/get/delete/reset records and local-Profile/policy checks, and `AdminMemoryHistoryService`.
- Removed `AdminAutomationService` and `AdminAutomationHistoryService`, the only other effect caller of the parallel history adapter. Canonical human/model Automation authoring and current due-work admission remain unchanged.
- Removed `IAdminP7eHistoryMutator`, both store implementations, the dedicated SQLite Memory tombstone/reset and Profile-owned cancellation writer, all six composition registrations, and four unused InMemory Memory snapshot/restore helpers. The incumbent synchronization root stays because canonical decommission still uses it.
- Removed the unused Memory scope HTTP parser, ten obsolete Memory/Automation DTOs and six mapping/status methods. The public endpoint inventory is unchanged; these types did not serve canonical routes.
- Removed six unreferenced effect replay helpers. Historical event factories/summary validation and existing current lifecycle replay remain. `AdminLearnedMemoryScope` retains its original namespace, names and values solely as historical body-free event vocabulary; no new owner, service or conversion path is introduced.

No canonical permission, review floor, effect transaction, store schema or migration changes. No saved private bodies, installed databases or old event rows are rewritten.

## Fixture retirement and current coverage

Four exclusively obsolete fixtures are retired with their owners: `AdminMemoryServiceTests`, `AdminMemoryHistoryTests`, `AdminAutomationServiceTests` and `AdminP7eHistoryMutatorTests`. Their 22 test method bodies represented 28 former runnable cases after inherited store parity. They are not counted as passing current tests. The removal manifest retains their exact names and paths at `/private/tmp/admin-memory-automation-retirement-manifest-20261023.json`.

| Retired concern | Current exercised owner/evidence |
| --- | --- |
| Memory list, exact delete and explicit scope reset; independent copies and policy/owner refusal | Actual canonical human Memory store/HTTP cases bind tenant User, actual source Agent/Session, original login, raw read/manage, item/count/security revisions and private tombstones. Stale or changed selections refuse without partial forgetting; unrelated scopes remain. |
| History reset/delete and operation replay | Canonical Memory revisions and atomic audit replace the parallel history writer. Actual SQLite application recreation retains forgotten values and rejects stale repeat commands without replaying model work. This is not a new injected append-failure qualification. |
| Automation inspection, model selection, editing and cancellation | Canonical authoring/editor/model/ownership cases retain actual parent/source/destination authority, optimistic revisions, immutable provenance, current models, exact consent and factual replay. Due edits preserve the selected occurrence through reopen and actual worker completion. |
| Historical safe event summaries | Six new actual store append/readback cases cover Memory item deletion, all three reset scopes/counts and Automation revocation in both stores. Original summary JSON and IDs round-trip without private body/subject fields. Existing summary/idempotency/filter/refusal cases remain. This checks the retained format using disposable records, not conversion of a user's database. |

The human Memory HTTP journey also asserts that the five retired Application authority types are absent from the built assembly before actual learning/inspection/forgetting/reset. This supplements executed effects; type absence alone is not treated as functional evidence.

## Focused local verification

| Group | Result |
| --- | --- |
| Application historical summary policy, canonical Automation construction and Memory projection | **39 passed**, zero failed/skipped. |
| Canonical human Memory stores and historical AdminEvent stores | **38 passed**, zero failed/skipped, 1 m 25 s. Includes all six new readback cases. |
| Canonical Memory HTTP/recovery, Automation authoring/editor/model and exact HTTP inventory | **37 passed**, zero failed/skipped, 1 m 57 s. |
| Automation ownership/cancellation, exact model deletion and factual replay | **42 passed**, zero failed/skipped, 2 m 3 s. |
| Final serial solution build | Passed, zero warnings/errors, 51.53 s; `build-closure-20261023.log`. |

**156 distinct focused backend cases pass.** The first two actual Memory HTTP cases pass separately and overlap the final 37-case API group; they are counted once.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

```text
Application: FullyQualifiedName~AdminEventSummaryPolicyTests|FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~TenantMemoryProjectionTests
Stores: FullyQualifiedName~Tenant_human_memory|FullyQualifiedName~AdminEventStoreContractTests
First actual workflow: FullyQualifiedName~Tenant_human_memory_HTTP_inspection_exact_forget_and_reset
API group: FullyQualifiedName~Tenant_human_memory|FullyQualifiedName~Tenant_automation_authoring_|FullyQualifiedName~Tenant_automation_editor_|FullyQualifiedName~Tenant_model_Automation_mutations_create_inspect_edit_enable_disable|FullyQualifiedName~Tenant_automation_preset_disabled_drafts_can_retain|FullyQualifiedName~Tenant_HTTP_cutover
Automation boundaries: FullyQualifiedName~Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes|FullyQualifiedName~Tenant_model_existing_Automation_deletion|FullyQualifiedName~Tenant_Automation_direct_replay
```

Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.

Logs use `/private/tmp/admin-memory-automation-retirement-` with `contracts-20261023.log`, `contracts-final-20261023.log`, `stores-20261023.log`, `runtime-first-20261023.log`, `api-20261023.log` and `automation-boundaries-20261023.log`. After passing store/Runtime checks, the final build additionally prunes six unreferenced effect replay helpers; the focused Application contract group is rechecked on that build. No canonical call path changes after its passing workflow. Canonical architecture/interface/implementation/testing owners now identify the successor; historical milestone reports retain original-version evidence.

## Remaining scope

The full goal remains active. `StructuredMemoryService`/`MemoryAdmission` and other internal unowned Profile/Experience/Automation services and historical fixtures still need retirement or coverage-mapped reconciliation. Final store/tool authority inventory, safe retained preparation/schema cleanup and proposal-wide security, isolation, functional and rendered UI acceptance remain open. This backend retirement changes no UI and claims no new browser acceptance. All verification uses disposable offline Synthetic stores/adapters. No CI, hosted provider, user-data reset, Real restart, push or deployment was performed.
