# P7C — Harness resources and workspace boundaries

**Status:** in progress (W03 slice; cumulative evidence through capabilities approval)

**Baseline:** `4a2bf999c553d362c828c617f0218516ab0baeb4` (post-P7B)

**Last approved W03 batch HEAD:** `feeb3000aa990e0900a4fb44448d7e19abca24cf` (managed chat + Admin Resources UI + Capabilities tab; review 0039 PASS on `3731f44..feeb300`)

**Review ranges (incremental):** resource HTTP `d920874`; runtime `/agent` + managed session `1cfca67`; Admin Resources UI `c090e98`; managed chat journey `3731f44`; Capabilities + tool registry `feeb300`

## Scope delivered (observed)

- Durable definition resource metadata, immutable content blobs, draft bindings, and publication bindings (`20260925084907_P7DefinitionResources`) with InMemory/SQLite parity.
- Owner Admin APIs: draft resource list/upload/bind/remove; publication resource list; bounded size/type/path/secret validation.
- `DefinitionPublicationResourceReader` + `FileSessionWorkspace`: read-only `/agent/resources/...`; template resources seed `/workspace/working` once per session policy.
- Minimal managed-instance path: `POST /api/v2/admin/agent-instances`, v2 session create with `agentInstanceId`, v1 rejection of instance id.
- Admin draft UI: Instructions, Capabilities (typed `RoleEnvironment`), Resources; publication resource inspection; managed publication chat entry.
- `GET /api/v2/admin/tools` for registry-backed tool allowlist editing.

## W03 verification (observed to date)

| Check | Command | Result |
| --- | --- | --- |
| Resource store contracts | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~AgentDefinitionResourceAdminStoreContractTests` | Pass (InMemory + SQLite; v1/v2 bytes, traversal, secrets) |
| Publication workspace | `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~DefinitionPublicationResourceWorkspaceTests` | Pass |
| Admin resource API | `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~AdminApiTests` (resource + runtime cases) | Pass |
| Admin UI unit | `cd web && pnpm exec vitest run src/features/admin` | Pass |
| Managed resource journey | `cd web && rm -f ../data/playwright/w03-resource-journey.db && PLAYWRIGHT_SQLITE_PATH=../data/playwright/w03-resource-journey.db Persistence__AttachmentRoot=../data/playwright/w03-attachments Persistence__WorkspaceRoot=../data/playwright/w03-workspaces Persistence__ArtifactRoot=../data/playwright/w03-artifacts CI=1 pnpm exec playwright test e2e/z-admin-resource-journey.spec.ts --project=synthetic` | Pass (disposable SQLite + isolated attachment/workspace/artifact roots; `CI=1` disables server reuse) |
| Strict frontend build | `cd web && pnpm run build` | Pass |

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
| AC-P7C-08 | `/agent` read-only in `FileSessionWorkspace` (existing workspace suite) |
| AC-P7C-09 | Session `/workspace` mutable (existing P1–P3 workspace tests) |
| AC-P7C-10 | No write-back path in publication resource reader |
| AC-P7C-11 | Traversal/forbidden path rejection; secret scan on textual uploads |
| AC-P7C-12 | No instance filesystem introduced |

## Remaining before W03 slice gate

- Canonical doc cross-refs (persistence, protocol, testing matrix) for P7C observed blocks.
- Full W03 combined regression command and hosted exact-SHA workflow status on gate candidate.
- Explicit `/agent` write-denial and workspace isolation matrix tests beyond existing workspace suite (if not already mapped in gate run).

## Hosted CI

Pending: branch ahead of `origin`; no workflow URL on exact gate candidate SHA until push.
