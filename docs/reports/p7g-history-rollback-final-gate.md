# P7G — Admin history, deprecation/rollback, and final P7 gate

**Status:** W07 closed at `ae83bfcd6c89100cd1da2ee89789a6a1b1f8408f` (review 0139 / review-0051 PASS). W08 local candidate gate and hosted exact-SHA CI remain.

**Baseline:** `03e350af1ec343c17d8486b4ffcc8fb6ecac8bb0` (W06 P7F closure)

**W07 closure HEAD:** `ae83bfcd6c89100cd1da2ee89789a6a1b1f8408f`

## Scope delivered (observed, partial)

- Append-only `AdminEvents` persistence (SQLite migration + in-memory parity) with idempotent `OperationId`.
- `GET /api/v2/admin/events` owner-protected list API with target filters.
- `PublicationCreated` admin events recorded atomically with durable draft publish (when operation id is supplied), including allowlisted `changedSections` metadata from the publish-time diff.
- `PublicationDeprecated` admin events recorded atomically with metadata-only publication deprecate (operation id from lifecycle), including safe `metadataRevision` summary.
- `DraftCreated` admin events recorded atomically with durable draft create/fork (operation id from lifecycle), including bounded `sourceKind` / `sourceVersion` summary.
- `ManagedInstanceCreated` admin events recorded atomically with durable managed instance create (operation id from lifecycle), including bounded `definitionId` / `instanceId` / `version` summary.
- `InstanceDefinitionVersionChanged` admin events recorded atomically with durable managed active-version reassociate/rollback (operation id from lifecycle), including bounded `definitionId` / `instanceId` / `fromVersion` / `toVersion` summary.
- `PersonaChanged` admin events recorded atomically with durable managed persona update (operation id from lifecycle), including bounded `definitionId` / `instanceId` / `fromPersonaRevision` / `personaRevision` / `personaFingerprint` summary (no persona payload).
- `InstanceArchived` / `InstanceUnarchived` admin events recorded atomically with durable managed lifecycle update, including bounded `definitionId` / `instanceId` / `fromLifecycle` / `toLifecycle` summary.
- `MemoryItemDeleted` / `MemoryScopeReset` admin events recorded after successful learned-memory delete/reset (operation-id idempotency via `IAdminP7eHistoryMutator`), including bounded `instanceId` / `scope` / `memoryId` or `itemsRemoved` summary (no memory content).
- `TriggerRegistrationRevoked` admin events recorded after successful automation cancel, including bounded `instanceId` / `registrationId` / `revision` summary.
- Shared `AdminEventSummaryPolicy` validation for in-memory and SQLite event append paths.

## W07 verification (partial)

| Check | Command | Result |
| --- | --- | --- |
| Admin event store parity | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminEvent` | Pass (10) |
| Publication history durability | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminPublication` | Pass (8) |
| Publication deprecation history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminDeprecation` | Pass (8) |
| Admin event summary policy | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminEventSummary` | Pass (13) |
| Memory admin history | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminMemoryHistory` | Pass (3) |
| P7E history atomicity | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminP7eHistoryMutator` | Pass (13) |
| Draft created history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminDraftCreated` | Pass (10) |
| Managed instance created history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminManagedInstance` | Pass (12) |
| Instance definition version history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminInstanceDefinitionVersion` | Pass (9) |
| Instance persona history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminInstancePersona` | Pass (9) |
| Instance lifecycle history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminInstanceLifecycle` | Pass (6) |
| Admin events API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_events` | Pass (7) |

## W07 final gate (partial)

| Check | Command | Result |
| --- | --- | --- |
| Solution backend suites | `dotnet test AgentCore.sln --nologo` | Pass (1596 passed, 8 skipped) |
| P3 approval/tool policy | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~ToolApprovalTests` | Pass (12) |
| P4 identity/memory + P7D isolation | `dotnet test tests/AgentCore.Application.Tests --filter "FullyQualifiedName~P4ClosureTests\|FullyQualifiedName~IdentityUserMemoryTests\|FullyQualifiedName~ManagedInstanceP7DRegressionTests"` | Pass (8) |
| P5 trigger/scheduler | `dotnet test tests/AgentCore.Application.Tests --filter "FullyQualifiedName~TriggerDurablePolicyTests\|FullyQualifiedName~TriggerOccurrenceRoutingTests"` | Pass (23) |
| P5 store parity + P6 detached approval | `dotnet test tests/AgentCore.Infrastructure.Tests --filter "FullyQualifiedName~TriggerStoreContractTests\|FullyQualifiedName~WorkItemStoreContractTests\|FullyQualifiedName~DurableReminderTests.P7E_detached"` | Pass (19) |
| P6 durable journey API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~DurableWorkJourneyTests` | Pass (5) |
| Frontend unit | `pnpm run test --run` | Pass (468) with `--maxWorkers=2` on candidate HEAD |
| Frontend build | `pnpm run build` | Pass |
| Synthetic Playwright (`synthetic` project) | from `web/`: `rm -f ../data/playwright/w08-synthetic-gate.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w08-synthetic-gate.db CI=1 pnpm exec playwright test --project=synthetic` | Pass (51) |
| Compose SQLite volume smoke | `./scripts/compose-sqlite-volume.sh` | Pass |

**Solution gate verification:** `dotnet test AgentCore.sln --nologo` — Pass (1596 passed, 8 skipped) on the W07 regression-fix candidate containing this report; approved baseline `6ec1c675d1a896b3fe95b85d1077f27110577df5` is a git ancestor of that candidate.

## Remaining (phase gate)

- Hosted `synthetic` workflow green on the exact final candidate SHA (push `ae83bfc` or later freeze candidate; local `gh` auth was unavailable in the worker environment).
- W08 further canonical sync (05, 12, 14, 18; partial `docs/16` faithful-manual note) and hosted exact-SHA CI remain.

## W08 local gate (partial)

| Check | Command | Result |
| --- | --- | --- |
| Solution backend suites | `dotnet test AgentCore.sln --nologo` | Pass (1596 passed, 8 skipped) on `dac549f` |
| P3–P6 regression selections | filters in W07 final gate table (ToolApproval, P4/P7D, P5 trigger, P5 store + P6 approval, P6 journey API) | Pass (43 + 19 + 5) on `dac549f` |
| P7 migration + legacy reopen | `dotnet test tests/AgentCore.Infrastructure.Tests --filter "FullyQualifiedName~P7EnsureCreatedReopen\|FullyQualifiedName~DefinitionLifecycleMigration"` | Pass (7) on `b96942e` |
| Frontend unit | `pnpm run test --run --maxWorkers=2` | Pass (468) on `a013b5c` |
| Frontend build | `pnpm run build` | Pass on `a013b5c` |
| Owner/trusted-local + Admin redaction API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (56) on `a013b5c` |
| P7G whole-phase journey (disposable DB) | from `web/`: `rm -f ../data/playwright/w08-admin-lifecycle.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w08-admin-lifecycle.db CI=1 pnpm exec playwright test --project=admin-lifecycle` | Pass (1) on `a013b5c` |
| Synthetic Playwright (`synthetic` project) | from `web/`: `rm -f ../data/playwright/w08-synthetic-gate.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w08-synthetic-gate.db CI=1 pnpm exec playwright test --project=synthetic` | Pass (51) on `4e868c3` |
| Compose SQLite volume smoke (sessions, work items, Admin fork/resource/publish/instance/managed session) | `./scripts/compose-sqlite-volume.sh` | Pass (local; resource blobs on `/data/definition-resources`; bytes checked after recreate) |
| Faithful Manual-A wall-clock (P5/P6 detached reminder) | from `web/`: `rm -f ../data/playwright/w08-faithful-manual.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w08-faithful-manual.db PLAYWRIGHT_FAITHFUL_MANUAL=1 CI=1 pnpm exec playwright test --project=faithful-manual` | Pass (1) on `df761d8` |

## W07 browser

| Check | Command | Result |
| --- | --- | --- |
| P7G §8 whole-phase Admin lifecycle (local) | `PLAYWRIGHT_SQLITE_PATH=<disposable.db> CI=1 pnpm exec playwright test --project=admin-lifecycle` | Pass (1) |
| P7G §8 whole-phase Admin lifecycle (CI) | `.github/workflows/synthetic.yml` step `P7G whole-phase Admin lifecycle Playwright` | Wired (isolated `admin-lifecycle-isolated.db`; excluded from default `synthetic` project to avoid shared-DB pollution) |
