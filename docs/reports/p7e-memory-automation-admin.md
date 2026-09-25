# P7E — Memory and automation administration

**Status:** in progress (W05 slice; UI batch review-0027 PASS at `9d5fdb9`)

**Approved product baseline (incremental batches):** backend `10bd0dc5b075a84d475ea7891be69be42e6ba798`; full W05 UI batch `9d5fdb947122c98c79bc136dcaa8421d6c91cbde` (from `10bd0dc`)

## Scope delivered (observed)

- `AdminMemoryService` with Session / IdentityUser / User scopes, pinned-session policy for Session, transactional scope reset on structured-memory store.
- Owner-protected Admin HTTP: learned-memory list/delete/reset and automation list/cancel with explicit scope and destructive confirmation.
- `AdminAutomationService` and `TriggerInstancePolicyReconciliationService` on managed version/lifecycle changes (suspend ineligible, reactivate `SuspendedPolicy` when eligible).
- Admin instance UI: Memory and Automation tabs (effective policy, scope selection, provenance, schedule next-run, load/mutation race guards).
- Application tests: `AdminMemoryServiceTests`, `TriggerInstancePolicyReconciliationTests`, `AdminAutomationServiceTests`, structured-memory scope-reset contract tests.
- API tests: owner capability and confirmation guards on memory/automation routes (`AdminApiTests`).
- Frontend: `instanceMemoryAutomation` Vitest (6 panel tests); managed-instance Playwright journey exercises Memory/Automation load paths with API response assertions.

## W05 verification (partial; slice gate open)

| Check | Command | Result |
| --- | --- | --- |
| Admin memory application | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminMemoryServiceTests` | Pass (observed on batch) |
| Trigger reconciliation | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~TriggerInstancePolicyReconciliationTests` | Pass (observed on batch) |
| Admin API memory/automation | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (observed on batch) |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass (38) |
| P7E browser journey | `cd web && CI=1 pnpm exec playwright test e2e/z-admin-memory-automation-journey.spec.ts --project=synthetic` | Pass (1) on candidate SHA |

## Remaining before W05 slice gate

- Exact owner/scope mutation matrix assertions (A/B instances, User-wide, Session) beyond current service tests.
- FakeTimeProvider archive/scheduler proofs for ineligible registrations (no due fire while suspended).
- P6 approval-preservation regression with sensitive WorkItem path.
- Canonical docs (`docs/14`, memory/trigger observed sections) and full acceptance table vs frozen P7E contract.
- Combined W05 browser gate and hosted CI on exact candidate SHA.
