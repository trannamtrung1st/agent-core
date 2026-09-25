# P7E — Memory and automation administration

**Status:** approved (W05 slice gate; batch review-0029 PASS, review 0081)

**Baseline:** `10bd0dc5b075a84d475ea7891be69be42e6ba798` (W05 backend); `9d5fdb947122c98c79bc136dcaa8421d6c91cbde` (W05 UI batch)

**Approved HEAD:** `59a812a20720e9e87b127320f62480e7b390d0d5` (W05 slice gate)

**Closure-evidence revision:** review-0030 amendment on `59a812a` (AC-P7E-03/07/16 regressions and gate-doc alignment; orphaned `cd60fa9` is not in this lineage)

**Review ranges (incremental):** Playwright/helpers `cb2d935` (review-0028); slice gate docs/tests/protocol `59a812a` (from `cb2d935`, review-0029); closure evidence `59a812a..HEAD` (review-0030 amendment)

## Scope delivered (observed)

- `AdminMemoryService` with Session / IdentityUser / User scopes, pinned-session policy for Session, transactional scope reset on structured-memory store.
- Owner-protected Admin HTTP: learned-memory list/delete/reset and automation list/cancel with explicit scope and destructive confirmation (`docs/14` route table).
- `AdminAutomationService` and `TriggerInstancePolicyReconciliationService` on managed version/lifecycle changes (suspend ineligible, reactivate `SuspendedPolicy` when eligible); Admin revoke supports `Active` and `SuspendedPolicy`.
- Admin instance UI: Memory and Automation tabs (effective policy, scope selection, provenance, schedule next-run, load/mutation race guards).
- Application tests: `AdminMemoryServiceTests`, `AdminAutomationServiceTests`, `TriggerInstancePolicyReconciliationTests`, structured-memory scope-reset contracts, `TriggerStoreContractTests.Both_stores_cancel_suspended_policy_registrations`.
- API tests: owner capability and confirmation guards on memory/automation routes (`AdminApiTests`).
- Frontend: `instanceMemoryAutomation` Vitest; P7E and P7D Playwright journeys with owner-protected API assertions; searchable managed Identity picker.

## W05 verification (slice gate)

| Check | Command | Result |
| --- | --- | --- |
| Admin memory + automation application | `dotnet test tests/AgentCore.Application.Tests --filter "FullyQualifiedName~AdminMemoryServiceTests\|FullyQualifiedName~AdminAutomationServiceTests\|FullyQualifiedName~TriggerInstancePolicyReconciliationTests"` | Pass (10) at `59a812a`; Pass (13) at review-0030 closure amendment HEAD (+3 `AdminMemoryServiceTests`) |
| P6 approval preservation (AC-P7E-16) | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~P7E_detached_sensitive_work_waits_for_approval_without_http_side_effect` | Pass (1) at review-0030 closure amendment HEAD only (Admin registration + reconciliation linkage) |
| Suspended cancel store parity | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~Both_stores_cancel_suspended_policy_registrations` | Pass (1) at `59a812a` |
| Admin API (incl. memory/automation) | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (36) at `59a812a` |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass (38) at `59a812a` |
| W05 combined browser gate | `cd web && CI=1 pnpm exec playwright test e2e/admin-shell.spec.ts e2e/z-admin-resource-journey.spec.ts e2e/z-admin-managed-instance-journey.spec.ts e2e/z-admin-memory-automation-journey.spec.ts --project=synthetic` | Pass (5/5) at `59a812a` |

## Acceptance mapping (P7E)

Criteria follow the frozen P7E contract (`p7e-memory-automation-admin.md` §10).

| ID | Requirement (summary) | Evidence |
| --- | --- | --- |
| AC-P7E-01 | List Session learned memory for selected session | `AdminMemoryServiceTests`; `AdminApiTests`; `z-admin-memory-automation-journey.spec.ts` |
| AC-P7E-02 | List IdentityUser memory for managed instance + trusted profile | `AdminMemoryServiceTests`; Admin UI + Playwright memory tab |
| AC-P7E-03 | User-wide list when effective policy permits | `List_user_returns_empty_when_user_retrieval_disabled`; `Reset_user_scope_throws_when_user_retrieval_disabled` |
| AC-P7E-04 | Delete one eligible item | `AdminMemoryServiceTests.Delete_user_scope_item_leaves_other_scopes_unchanged` |
| AC-P7E-05 | Reset one explicit scope | `AdminMemoryServiceTests.Reset_identity_user_scope_leaves_other_owners_unchanged` |
| AC-P7E-06 | Scope reset affects only targeted owner/scope | Reset/delete matrix tests; structured-memory scope-reset contracts |
| AC-P7E-07 | Memory mutation does not alter definition/persona/triggers/transcript | `Reset_session_scope_preserves_definition_persona_profile_transcript_and_triggers`; reset/delete matrix tests |
| AC-P7E-08 | No model-facing memory admin tools | Tool registry unchanged; Admin HTTP only |
| AC-P7E-09 | Inspect effective TriggerPolicy | Instance effective-config + Automation tab (UI/API observed) |
| AC-P7E-10 | TriggerPolicy change requires new publication | Definition draft/publish path (P7B); no per-instance override |
| AC-P7E-11 | List/revoke owned registrations | `AdminAutomationServiceTests`; `TriggerStoreContractTests` suspended cancel parity |
| AC-P7E-12 | Cross-owner mutation denied | `AdminAutomationServiceTests.Cancel_cross_instance_registration_returns_not_found` |
| AC-P7E-13 | Reconciliation prevents ineligible future execution | `TriggerInstancePolicyReconciliationTests` version + archive matrices |
| AC-P7E-14 | Re-eligible instance reactivates `SuspendedPolicy` rows | `TriggerInstancePolicyReconciliationTests` upgrade/unarchive paths |
| AC-P7E-15 | Completed occurrence/history unchanged | Reconciliation tests preserve `Completed` registrations |
| AC-P7E-16 | Sensitive actions still require P3/P6 approval | `DurableReminderTests.P7E_detached_sensitive_work_waits_for_approval_without_http_side_effect`; `ManagedInstanceP7DRegressionTests` archive/work preservation |

## Canonical documentation (updated with this slice)

- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — learned-memory and automation Admin routes
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7E observed matrix
- Roadmap: [08-development-roadmap.md](../08-development-roadmap.md) — W05 gate
- Implementation plan: [18-implementation-plan.md](../18-implementation-plan.md) — W05 gate
- Observed status: [TODO.md](../../TODO.md) — P7E section

## Hosted CI

Pending: no workflow URL on exact gate candidate SHA until pushed to `origin`.
