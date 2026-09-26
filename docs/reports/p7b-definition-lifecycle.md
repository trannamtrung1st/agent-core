# P7B — Durable definition draft and publication lifecycle

**Status:** approved (W02 slice gate; batch review 0020 PASS)
**Baseline:** `9169bfad2f70d212251e1462f442ff887b6d0caf` (post-P7A)
**Approved HEAD:** `4a2bf999c553d362c828c617f0218516ab0baeb4`
**Review ranges:** storage `6907ea4`; lifecycle APIs `0e7b99f`; deprecate + Admin draft UI `0f39c32`; evidence/docs `137199c`; Playwright slice gate `4a2bf99` (from `137199c`)

## Scope delivered (observed)

- Domain records for draft/publication lifecycle; `IAgentDefinitionAdminStore` with InMemory and SQLite (`20260925071140_P7DefinitionLifecycle`) parity.
- `CompositeAgentDefinitionStore` over file built-ins and durable publications; exact and default lookup (default skips deprecated durable versions).
- `AgentDefinitionLifecycleService` + owner Admin APIs: draft list/read/create/fork/update/publish, publication list, publication deprecate (metadata revision).
- Candidate validation (structure, aliases, publish-time model/tool gates, bounded secret detection).
- Admin definition detail UI: per-version fork (`ForkBuiltIn` / `ForkDurable`), draft edit/save, save-before-publish, durable inventory metadata.
- Contract suite (InMemory + SQLite): edit/publish/deprecate, immutability, concurrency, reopen, composite overlay, default selection.
- W02 browser gate: dirty publish, durable fork/read-back, combined disposable-DB regressions (`z-admin-definition-lifecycle.spec.ts`).

## W02 verification (this slice)

| Check | Command | Result |
| --- | --- | --- |
| Admin API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (22) |
| Admin read + snapshot | `dotnet test tests/AgentCore.Application.Tests --filter "FullyQualifiedName~AdminReadServiceTests\|FullyQualifiedName~DefinitionLifecycleSessionSnapshotTests"` | Pass (3) |
| Store contracts + migration | `dotnet test tests/AgentCore.Infrastructure.Tests --filter "FullyQualifiedName~AgentDefinitionAdminStoreContractTests\|FullyQualifiedName~DefinitionLifecycleMigrationTests"` | Pass (14) |
| Admin UI unit | `cd web && pnpm run test --run src/features/admin` | Pass (6) |
| W02 combined browser gate | `cd web && rm -f ../data/playwright/w02-gate.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w02-gate.db CI=1 pnpm exec playwright test e2e/admin-shell.spec.ts e2e/text-conversation.spec.ts e2e/z-admin-definition-lifecycle.spec.ts --project=synthetic` | Pass (12; review 0020 + worker closure) |

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

- Decisions: [10-technology-decisions.md](../10-technology-decisions.md#p7b-definition-lifecycle-observed)
- Backend: [12-backend-implementation-spec.md](../12-backend-implementation-spec.md#p7b-definition-lifecycle-observed)
- Architecture: [03-system-architecture.md](../03-system-architecture.md#p7b-definition-lifecycle-observed)
- Interfaces: [04-backend-interfaces.md](../04-backend-interfaces.md#p7b-definition-lifecycle-observed)
- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — Admin draft/publication routes (201 create/fork)
- Persistence: [15-persistence-and-configuration.md](../15-persistence-and-configuration.md#p7b-definition-lifecycle-store-observed)
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7B observed matrix
- Implementation plan: [18-implementation-plan.md](../18-implementation-plan.md) — W02 gate

## Deferred to later P7 slices (not W02 gaps)

- Human-readable publish diff and eval-gated publish (P7F).
- Instance upgrade/rollback association UX beyond deprecate metadata (P7D/P7G).
- Full TODO narrative checkboxes for eval/diff remain open at phase level.

## Known limitations / follow-ups

- Stale-draft conflict reload UX polish (non-blocking).
- Optional: explicit built-in v1/v2 + durable v3 → next v4 collision API test.
- Hosted exact-SHA Synthetic workflow not yet recorded (branch ahead of `origin`; push required before GitHub Actions can run on `4a2bf99`).

## Hosted CI

Pending: no workflow URL on exact candidate SHA `4a2bf99` until pushed to `origin`. Local W02 combined browser gate passed on disposable SQLite (see verification table).
