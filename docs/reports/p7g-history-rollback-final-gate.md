# P7G — Admin history, deprecation/rollback, and final P7 gate

**Status:** in progress (W07)

**Baseline:** `03e350af1ec343c17d8486b4ffcc8fb6ecac8bb0` (W06 P7F closure)

## Scope delivered (observed, partial)

- Append-only `AdminEvents` persistence (SQLite migration + in-memory parity) with idempotent `OperationId`.
- `GET /api/v2/admin/events` owner-protected list API with target filters.
- `PublicationCreated` admin events recorded atomically with durable draft publish (when operation id is supplied), including allowlisted `changedSections` metadata from the publish-time diff.
- `PublicationDeprecated` admin events recorded atomically with metadata-only publication deprecate (operation id from lifecycle), including safe `metadataRevision` summary.
- `DraftCreated` admin events recorded atomically with durable draft create/fork (operation id from lifecycle), including bounded `sourceKind` / `sourceVersion` summary.
- `ManagedInstanceCreated` admin events recorded atomically with durable managed instance create (operation id from lifecycle), including bounded `definitionId` / `instanceId` / `version` summary.
- Shared `AdminEventSummaryPolicy` validation for in-memory and SQLite event append paths.

## W07 verification (partial)

| Check | Command | Result |
| --- | --- | --- |
| Admin event store parity | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminEvent` | Pass (10) |
| Publication history durability | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminPublication` | Pass (8) |
| Publication deprecation history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminDeprecation` | Pass (8) |
| Admin event summary policy | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminEventSummary` | Pass (9) |
| Draft created history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminDraftCreated` | Pass (10) |
| Managed instance created history | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminManagedInstance` | Pass (12) |
| Admin events API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_events` | Pass (4) |

## Remaining (W07)

- Thread history through remaining Admin mutations, deprecation/rollback UX, `admin-lifecycle.spec.ts` whole-phase journey, and final gate evidence per frozen P7G contract.
