# P7G — Admin history, deprecation/rollback, and final P7 gate

**Status:** in progress (W07)

**Baseline:** `03e350af1ec343c17d8486b4ffcc8fb6ecac8bb0` (W06 P7F closure)

## Scope delivered (observed, partial)

- Append-only `AdminEvents` persistence (SQLite migration + in-memory parity) with idempotent `OperationId`.
- `GET /api/v2/admin/events` owner-protected list API with target filters.
- `PublicationCreated` admin events recorded atomically with durable draft publish (when operation id is supplied), including allowlisted `changedSections` metadata from the publish-time diff.
- Shared `AdminEventSummaryPolicy` validation for in-memory and SQLite event append paths.

## W07 verification (partial)

| Check | Command | Result |
| --- | --- | --- |
| Admin event store parity | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminEvent` | Pass (10) |
| Publication history durability | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AdminPublication` | Pass (8) |
| Admin event summary policy | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminEventSummary` | Pass (6) |
| Admin events API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~Admin_events` | Pass (1) |

## Remaining (W07)

- Thread history through remaining Admin mutations, deprecation/rollback UX, `admin-lifecycle.spec.ts` whole-phase journey, and final gate evidence per frozen P7G contract.
