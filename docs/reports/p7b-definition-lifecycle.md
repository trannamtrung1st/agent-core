# P7B — Durable definition draft and publication lifecycle

**Status:** in progress (W02 implementation; lifecycle API/UI batches approved at `0f39c32`)  
**Baseline:** `9169bfad2f70d212251e1462f442ff887b6d0caf` (post-P7A)  
**Latest approved HEAD (partial W02):** `0f39c32549f576355f2220abcca70a156f28d696`  
**Review ranges:** storage `6907ea4`; lifecycle APIs `0e7b99f`; deprecate + Admin draft UI `0f39c32`

## Scope delivered (observed)

- Domain records for draft/publication lifecycle; `IAgentDefinitionAdminStore` with InMemory and SQLite (`20260925071140_P7DefinitionLifecycle`) parity.
- `CompositeAgentDefinitionStore` over file built-ins and durable publications; exact and default lookup (default skips deprecated durable versions).
- `AgentDefinitionLifecycleService` + owner Admin APIs: draft list/read/create/fork/update/publish, publication list, publication deprecate (metadata revision).
- Candidate validation (structure, aliases, publish-time model/tool gates, bounded secret detection).
- Admin definition detail UI: per-version fork (`ForkBuiltIn` / `ForkDurable`), draft edit/save, save-before-publish, durable inventory metadata.
- Contract suite (InMemory + SQLite): edit/publish/deprecate, immutability, concurrency, reopen, composite overlay, default selection.

## W02 verification (this slice)

| Check | Command | Result |
| --- | --- | --- |
| Admin API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (22) |
| Admin read | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~AdminReadServiceTests` | Pass (2) |
| Store contracts | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AgentDefinitionAdminStoreContractTests` | Pass (13) |
| Session snapshot regression | `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~DefinitionLifecycleSessionSnapshotTests` | Pass (worker) |
| P7B migration | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~DefinitionLifecycleMigrationTests` | Pass (worker; from `20260924155535` + session preserve) |
| Admin UI unit | `cd web && pnpm run test --run src/features/admin` | Pass (6) |
| Admin shell Playwright | `cd web && pnpm exec playwright test e2e/admin-shell.spec.ts --project=synthetic` | Pending slice gate |
| Text conversation regression | `cd web && pnpm exec playwright test e2e/text-conversation.spec.ts --project=synthetic` | Pending slice gate |

## Acceptance mapping (P7B)

| ID | Evidence |
| --- | --- |
| AC-P7B-01 | Admin fork APIs + UI fork-by-source |
| AC-P7B-02 | `AgentDefinitionAdminStoreContractTests` immutability after publish |
| AC-P7B-03 | Stale draft update → conflict (store contract + API) |
| AC-P7B-04 | Publish uses expected draft revision |
| AC-P7B-05 | `Admin_definition_draft_fork_publish_assigns_next_version` |
| AC-P7B-06 | No publication update path in admin store |
| AC-P7B-07 | `Admin_publication_deprecate_rejects_builtin_version` |
| AC-P7B-08 | Composite + API resolve durable publications |
| AC-P7B-09 | `Composite_default_lookup_skips_deprecated_publication` |
| AC-P7B-10 | Deprecate API + contract |
| AC-P7B-11 | `DefinitionLifecycleSessionSnapshotTests` |
| AC-P7B-12 | InMemory/SQLite contract parity |

## Canonical documentation (updated with this slice)

- Architecture: [03-system-architecture.md](../03-system-architecture.md#p7b-definition-lifecycle-observed)
- Interfaces: [04-backend-interfaces.md](../04-backend-interfaces.md#p7b-definition-lifecycle-observed)
- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — Admin draft/publication routes (201 create/fork)
- Persistence: [15-persistence-and-configuration.md](../15-persistence-and-configuration.md#p7b-definition-lifecycle-store-observed)
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7B observed matrix
- Implementation plan: [18-implementation-plan.md](../18-implementation-plan.md) — W02 gate

## Gaps before W02 close

- Playwright journey for definition fork/edit/publish on Synthetic (beyond admin-shell).
- Stale-draft conflict reload UX polish.
- Hosted CI green on exact candidate SHA (slice gate).
- Optional: explicit built-in v1/v2 + durable v3 → next v4 collision test in API layer.

## Hosted CI

Pending exact candidate SHA after W02 slice gate.
