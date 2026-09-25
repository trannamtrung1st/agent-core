# P7C — Harness resources and workspace boundaries

**Status:** pending gate review (W03 slice gate; review-0016)

**Baseline:** `4a2bf999c553d362c828c617f0218516ab0baeb4` (post-P7B)

**Approved HEAD:** _(recorded only after reviewer PASS in a descendant evidence-only commit; do not embed the pending candidate SHA in the same commit as the gate tree.)_

**Review ranges (incremental):** resource HTTP `d920874`; runtime `/agent` + managed session `1cfca67`; Admin Resources UI `c090e98`; managed chat journey `3731f44`; Capabilities + tool registry `feeb300`; reopen parity `0bc5193`; gate closure on `0bc5193` baseline (review-0016 rounds 2–3)

## Scope delivered (observed)

- Durable definition resource metadata, immutable content blobs, draft bindings, and publication bindings (`20260925084907_P7DefinitionResources`) with InMemory/SQLite parity.
- Owner Admin APIs: draft resource list/upload/bind/remove/content-preview; publication resource list; bounded size/type/path/secret validation; tool registry list; managed instance create.
- `DefinitionPublicationResourceReader` + `FileSessionWorkspace`: read-only `/agent/resources/...`; template resources seed `/workspace/working` once per session policy; `/agent` writes denied.
- Minimal managed-instance path: `POST /api/v2/admin/agent-instances`, v2 session create with `agentInstanceId`, v1 rejection of instance id.
- Admin draft UI: Instructions, Capabilities (typed `RoleEnvironment`), Resources; publication resource inspection; managed publication chat entry.

## W03 verification (slice gate)

| Check | Command | Result |
| --- | --- | --- |
| Resource store + workspace | `dotnet test tests/AgentCore.Infrastructure.Tests --filter "FullyQualifiedName~AgentDefinitionResourceAdminStoreContractTests\|FullyQualifiedName~DefinitionPublicationResourceWorkspaceTests"` | Pass (17) |
| Admin API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` | Pass (28) |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass (17) |
| W03 combined browser gate | `cd web && rm -f ../data/playwright/w03-gate.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w03-gate.db Persistence__AttachmentRoot=../data/playwright/w03-gate-attachments Persistence__WorkspaceRoot=../data/playwright/w03-gate-workspaces Persistence__ArtifactRoot=../data/playwright/w03-gate-artifacts CI=1 pnpm exec playwright test e2e/admin-shell.spec.ts e2e/z-admin-resource-journey.spec.ts --project=synthetic` | Pass (3) |
| Strict frontend build | `cd web && pnpm run build` | Pass (gate closure) |

## Acceptance mapping (P7C)

| ID | Evidence |
| --- | --- |
| AC-P7C-01 | Admin Instructions + Capabilities tabs; draft save merges `environment` |
| AC-P7C-02 | Resources tab upload/bind/remove; Admin APIs |
| AC-P7C-03 | `Publish_binds_immutable_resource_snapshots_per_version` |
| AC-P7C-04 | Same contract test v1 vs v2 hashes |
| AC-P7C-05 | Built-in file seeds unchanged; composite built-in fallback |
| AC-P7C-06 | `Admin_definition_draft_resource_upload_bind_and_publish_snapshot` + managed chat `/agent` read |
| AC-P7C-07 | Publication version pinning in workspace/runtime tests |
| AC-P7C-08 | `FileSessionWorkspaceTests` denies `/agent` writes |
| AC-P7C-09 | Session `/workspace` mutable (existing workspace suite) |
| AC-P7C-10 | `Workspace_mutation_does_not_write_back_to_publication_resources` (user-modified seeded path survives re-`Ensure`; publication bytes unchanged); no write-back in publication reader |
| AC-P7C-11 | `Rejects_traversal_and_forbidden_logical_paths` (traversal, `agents/`, memory, triggers, workitems, approvals, `.agents`); secret scan on textual uploads |
| AC-P7C-12 | No instance filesystem introduced |

## Canonical documentation (updated with this slice)

- Decisions: [10-technology-decisions.md](../10-technology-decisions.md#p7c-harness-resources-observed)
- Architecture: [03-system-architecture.md](../03-system-architecture.md#p7c-harness-resources-and-workspace-boundary-observed)
- Interfaces: [04-backend-interfaces.md](../04-backend-interfaces.md#p7c-definition-resources-observed)
- Backend: [12-backend-implementation-spec.md](../12-backend-implementation-spec.md#p7c-harness-resources-observed)
- Protocol: [14-api-and-realtime-protocol.md](../14-api-and-realtime-protocol.md) — Admin resource routes and v2 `agentInstanceId` create
- Persistence: [15-persistence-and-configuration.md](../15-persistence-and-configuration.md#p7c-definition-resource-store-observed)
- Testing: [16-testing-strategy.md](../16-testing-strategy.md) — P7C observed matrix
- Implementation plan: [18-implementation-plan.md](../18-implementation-plan.md) — W03 gate

## Deferred to later P7 slices (not W03 gaps)

- Full instance management, persona revision UX, archive (P7D).
- Eval-gated publish and human-readable diff (P7F).
- Append-only Admin history (P7G).

## Hosted CI

Pending: no workflow URL on exact gate candidate SHA until pushed to `origin`.
