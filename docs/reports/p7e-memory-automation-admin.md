# P7E — Memory and automation administration

**Status:** in progress (W05 slice gate; UI batch review-0027 PASS at `9d5fdb9`; evidence batch review-0028 PASS at `cb2d935`)

**Approved product baseline (incremental batches):** backend `10bd0dc5b075a84d475ea7891be69be42e6ba798`; full W05 UI batch `9d5fdb947122c98c79bc136dcaa8421d6c91cbde` (from `10bd0dc`); Playwright/helpers evidence `cb2d9357336a762dfe6b40866a938ae8a0acf30b` (from `9d5fdb9`, review 0077)

## Scope delivered (observed)

- `AdminMemoryService` with Session / IdentityUser / User scopes, pinned-session policy for Session, transactional scope reset on structured-memory store.
- Owner-protected Admin HTTP: learned-memory list/delete/reset and automation list/cancel with explicit scope and destructive confirmation.
- `AdminAutomationService` and `TriggerInstancePolicyReconciliationService` on managed version/lifecycle changes (suspend ineligible, reactivate `SuspendedPolicy` when eligible).
- Admin instance UI: Memory and Automation tabs (effective policy, scope selection, provenance, schedule next-run, load/mutation race guards).
- Application tests: `AdminMemoryServiceTests`, `TriggerInstancePolicyReconciliationTests`, `AdminAutomationServiceTests`, structured-memory scope-reset contract tests.
- API tests: owner capability and confirmation guards on memory/automation routes (`AdminApiTests`).
- Frontend: `instanceMemoryAutomation` Vitest (6 panel tests); P7E and P7D Playwright journeys with owner-protected API assertions; searchable managed Identity picker for stable E2E selection.

## W05 verification (partial; slice gate open)

| Check | Command | Result |
| --- | --- | --- |
| Admin memory application | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminMemoryServiceTests` | Pass (observed on batch) |
| Trigger reconciliation | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~TriggerInstancePolicyReconciliationTests` | Pass (observed on batch) |
| Admin API memory/automation | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (observed on batch) |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass (38) |
| P7E browser journey | `cd web && CI=1 pnpm exec playwright test e2e/z-admin-memory-automation-journey.spec.ts --project=synthetic` | Pass (1) at `cb2d935` |
| P7D + P7E combined | `cd web && CI=1 pnpm exec playwright test e2e/z-admin-managed-instance-journey.spec.ts e2e/z-admin-memory-automation-journey.spec.ts --project=synthetic` | Pass (2/2) at `cb2d935` |

## Remaining before W05 slice gate

- Full AC-P7E acceptance table vs frozen contract (map each AC to named tests/journeys).
- P6 approval-preservation regression explicitly tied to Admin trigger policy path (sensitive WorkItem still `AwaitingApproval`).
- Combined W05 browser gate (admin shell + prior slice regressions) and hosted CI on exact gate SHA.
- Canonical memory/trigger observed sections beyond `docs/14` Admin route table.

## Acceptance mapping (draft)

| ID | Requirement (summary) | Evidence (observed) |
| --- | --- | --- |
| AC-P7E-01..03 | List Session / IdentityUser / User scopes with policy gates | `AdminMemoryServiceTests`; `AdminApiTests`; P7E Playwright |
| AC-P7E-04..07 | Delete one item; scope reset isolation; no persona/definition drift | `AdminMemoryServiceTests` reset + delete matrix; structured-memory scope-reset contracts |
| AC-P7E-08 | No model-facing memory admin tools | Tool registry unchanged; no memory-write tools added for Admin |
| AC-P7E-11 | Admin lists owned active/suspended registrations | `AdminAutomationServiceTests.List_maps_safe_provenance_fields` |
| AC-P7E-12 | Cross-owner registration mutation denied | `AdminAutomationServiceTests.Cancel_cross_instance_registration_returns_not_found` |
| AC-P7E-09..10, 13..15 | Effective policy, publication path, reconciliation, archive scheduler | `TriggerInstancePolicyReconciliationTests`; effective config/UI (observed) |
| AC-P7E-11 (cancel) | Revoke active and policy-suspended registrations | `AdminAutomationServiceTests` cancel matrix; `TriggerStoreContractTests.Both_stores_cancel_suspended_policy_registrations` (InMemory + SQLite) |
| AC-P7E-16 | Sensitive actions still require P3/P6 approval | `TriggerOccurrenceRoutingTests.Occurrence_evidence_is_not_a_user_turn_and_email_still_requires_approval` (approval invariant; Admin policy does not grant standing approval) |
