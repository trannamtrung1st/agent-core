# Agent Workspace refinement verification

> Historical verification. Identity/workspace compatibility statements are superseded by the unified migration described in [current architecture](../03-system-architecture.md) and [migration verification](agent-workspace-full-migration-verification.md). Original evidence remains unchanged.

The requested enhancement evolves the completed workspace baseline `181580c96d57761463b8525328660be3903005df`. It does not reopen a historical phase or start P10/P11. The enhancement is accepted on published snapshot `0cadbe57a1d1c2b90b95c774e15eb2343601767f`, containing behavior commit `7bc2789d68508bb64988183a2ada2db2b507557e`. [Hosted Synthetic run 37506942347](https://github.com/trannamtrung1st/agent-core/actions/runs/37506942347) completed successfully in all five jobs. The hosted receipt was finalized after that run. The subsequent review below records a separate bounded approval-preview correction.

## Architecture and compatibility

- Explicit `environment.workspace.semantics: "agentWorkspaceV2"` selects the new managed contract. Null/missing policy preserves the published legacy contract; no numeric version comparison grants authority.
- `/home` is durable Agent Instance working material, `/working` is current Session scratch, and Artifacts remain independent immutable Session downloads. Trusted Session metadata selects the owner.
- General Assistant v15 removes retain/checkout and adds one get/set cwd capability, reducing its allowlist from 32 to 31. Existing v12/v13/v14 files and behavior are unchanged. Retain/checkout remain registered for old definitions. Public compatibility/default creation uses the latest legacy definition; explicit new mode requires a managed instance.
- Domain owns the policy and canonical allowlist bound. Application owns logical resolution, mailbox cwd effects, lifecycle-authorized file operations and bounded transfer preflight. Infrastructure retains separate filesystem and metadata/blob adapters. No provider DTO crosses these boundaries.

## Paths, cwd and lifecycle

New managed scratch is provisioned beneath `WorkspaceRoot/agent-<instanceN>/sessions/session-<sessionN>/workspace/working`. The private `workspace` segment preserves the adapter/sandbox layout. Home keeps existing SQLite metadata/directory rows and immutable opaque blobs beneath AgentWorkspaceRoot. Historical materialized scratch stays at its existing location without migration. Neither mapping nor physical identifiers are exposed as logical paths.

Cwd starts `/home`, belongs to the live Session Runtime mailbox and is committed only after response/epoch-fenced tool execution. It survives further turns and reconnect while that runtime lives. New Sessions and runtime reconstruction start `/home`; cwd is intentionally transient, with no new SQLite schema/file. Set validates an existing authorized directory in home or scratch. Move/delete/batch affecting cwd or an ancestor fails Conflict until cwd changes. Relative model paths resolve from this snapshot; HTTP paths use stable `/home` defaults independently of mutable runtime cwd.

Session deletion removes only its scratch; home survives. Archive permits home inspection and rejects mutation. Existing hard-delete preconditions require all Sessions to be removed before instance metadata/blob purge. Empty owner container directories may remain but contain no workspace data. Compose now puts scratch on the persistent `/data` volume as well as home, preserving current Session files across recreation.

## Durable edits and copy

Home creation is ordinary workspace.write. Existing files require current expectedRevision and/or expectedSha256; supplied tokens on absent files fail. Home patches require current SHA-256, strict UTF-8 and one occurrence per oldText edit. Same-scope home restructuring retains whole-tree CAS. Scratch keeps existing lighter Session serialization.

Individual cross-root copy exports a bounded exact source snapshot and imports through the destination gate. It preserves binary bytes, complete trees and empty directories, creates required parents and preflights portable path conflicts and destination quotas. Copy never merges or overwrites by default. A single existing durable file can be replaced with matching destination CAS; directories cannot. Cross-scope move is Forbidden. Both owners are derived from one trusted execution; callers cannot choose another Session/instance.

Transfers reuse the shared logical tree planner. Scratch stages bytes before exclusive install; home tree import commits metadata together and cleans newly written blobs if commit fails. This is not a cross-store transaction. Structural batches remain single-scope, at most 16 operations/16 KiB, with complete ordered virtual preflight, explicit recursive deletion, normal exact approval and honest partial-execution reporting.

Sandbox remains bounded and scratch-only; new public sandbox paths resolve from `/working` even when Session cwd is home. Its internal mount contract remains unchanged. Home/scratch Artifacts are newly published exact bytes, with logical provenance.

## Admin and validation

`GET /api/v2/admin/tools` returns toolNames and maxToolAllowlistEntries derived from `AgentDefinitionValidator.MaxToolAllowlistEntries` (32). React does not hardcode the bound. Registry size and per-Definition selection size remain distinct.

The selector displays selected/max count; at 32 it disables only unselected choices and keeps selected choices removable. It never silently removes a capability. A loaded 33-tool candidate remains visible with an inline error and blocked Form Save. Registry errors remain retryable and block Form Save. Advanced JSON still reaches authoritative server validation. Workspace policy round-trips through both editors.

Validation distinguishes tool_limit_exceeded, duplicate_tool, invalid_tool_name, unregistered_tool and unconfigured_tool. Findings identify environment.toolAllowlist. HTTP Problem Details preserves generic ValidationError and adds field/validationCode plus precise detail. Existing publication/configuration gates remain intact.

The owner Workspace tab says Agent Workspace, `/home`, and Durable across sessions. Folder inspection stays first-class; directory downloads remain disabled. Existing Ant Design components/layouts are reused.

## Runtime and regression evidence

| Gate | Command / observed outcome | Result |
| --- | --- | --- |
| Backend full suites | `dotnet test AgentCore.sln --no-restore --nologo -m:1 -p:UseSharedCompilation=false` | Domain 145; Application 1271 + 1 opt-in skip; Infrastructure 765 + 15 environment/opt-in skips; API 386 + 2 opt-in skips; OrderEvents 4 passed |
| Managed + historical API workspace journeys | `dotnet test tests/AgentCore.Api.Tests --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~AgentWorkspaceJourneyTests` | 10 passed, including new-mode SQLite reopen, nested scratch cleanup and historical move/retain behavior |
| Managed policy and existing tool contracts | Focused Application workspace Artifact/structure filter | 22 passed; explicit policy enables native move independently of unrelated grants, legacy descriptor stays narrow |
| Transfer parity/quotas/CAS | Focused Infrastructure cross-store tree copy and definition skill round-trip filter | 3 passed; binary equality, empty folders, no mutation on quota failure, guarded replacement and SQLite reopen |
| Frontend unit suite | CI Node 22 executable runs `node_modules/vitest/vitest.mjs --run --maxWorkers=1` | 94 files / 705 tests passed |
| Frontend build | `pnpm run build` | Passed; existing Rollup annotation/chunk warnings |
| New and compatibility browser workspace gates | Node 22 Playwright; `agent-workspace-v2.spec.ts`, `agent-workspace.spec.ts`, `workspace-filesystem.spec.ts`, project synthetic, task ports 5086/5176 | 4 passed; exact downloaded bytes, same-Session cwd, fresh default, durable home after source deletion, folder controls, structural approvals |
| Compose SQLite survival | `COMPOSE_PROJECT_NAME=agent-core-workspace-refinement ./scripts/compose-sqlite-volume.sh` | Passed; new-mode binary home and scratch survive recreation; unguarded overwrite rejected; deleting source/new Session keeps home and isolates scratch; existing Admin/continuity/Artifact boundaries pass |
| Actual Docker sandbox | Existing DockerSandboxExecutorTests after fetching busybox:1.36 | 8 passed, no skips; actual mount/isolation, cleanup, cancellation and Artifact export |
| Hosted Synthetic | [Run 37506942347](https://github.com/trannamtrung1st/agent-core/actions/runs/37506942347), exact published snapshot `0cadbe57` | All five jobs green: backend, frontend, Playwright core, Playwright acceptance, Compose |

Playwright MCP also exercised the actual running SQLite Synthetic app: new managed instance, home→scratch→home model tool loop, downloadable result.md, subsequent-turn cwd `/working`, and owner folder/file inspection at 1440/768/390 without page overflow. Admin v15 fork visibly showed 31/32; adding a tool reached 32/32, another unselected option was disabled, and toggling a selected option removed it back to 31. Current journey console had no errors; affected API requests and Artifact download succeeded. A historical polling request during the intentional host restart failed once, independently of the completed journey.

Early checks caught and repaired fixture assumptions about latest versions, a missing Synthetic Artifact marker, a disposable restart-root override, a fast textarea initialization race (fixtures now assert complete controlled input/readiness), and Compose scratch placement outside the volume. Local pnpm selected Node 26 despite shell Node 22; the initial unit run had unrelated Web Storage failures. The final unit run explicitly uses the CI Node 22 executable. These failed attempts are not passing evidence.

Logs are task-local `/tmp/refinement-backend-final.log`, `/tmp/refinement-api-allworkspace2.log`, `/tmp/refinement-transfer-tests.log`, `/tmp/refinement-web-node22.log`, `/tmp/refinement-web-build-final.log`, `/tmp/refinement-e2e-final22.log`, `/tmp/refinement-compose-volume.log` and `/tmp/refinement-sandbox-actual.log`. The hosted backend passed Domain 145, Infrastructure 765 (15 environment/opt-in skips), Application 1272 (1 opt-in skip), and API 386 (2 opt-in skips). Hosted frontend passed 94 files / 705 tests plus build. Core Playwright passed 101 tests. The seven separate acceptance invocations passed 17 tests in total (1 faithful conversation, 1 lifecycle, 1 authoring, 7 harness, 2 continuity, 1 maintenance, 4 secretary). Hosted Compose passed. The actual Docker sandbox checks also passed locally with no skips. Hosted logs are saved at `/tmp/refinement-hosted-final.log`.

## Canonical documentation and deferrals

README and docs 03/04/10/12/13/14/15/16/18 now own the new model, policy, concurrency, paths, storage/lifecycle, tools, API/editor validation and acceptance matrix. Historical verification reports are preserved.

Explicitly deferred: cross-store structural batches/transactions; richer home sandbox mounts; dynamic model-tool projection from a larger authorized capability set; scratch approval tree fingerprints; physical migration of old scratch; cwd persistence across runtime reconstruction. No shell expansion, shared workspace, cloud storage, new UI kit or multi-agent runtime was added.

## Consistency review follow-up (2026-10-07)

The requested review found that new-mode destructive approval details still described relative paths as legacy scratch paths. Delete/batch previews now display concrete public paths resolved from the trusted Session cwd, including nested home and scratch paths. Exact action hashes and executable arguments remain unchanged; compatibility previews retain their historical scope. README now attributes the original workspace freeze explicitly, and the completed bounded filesystem decision is marked observed. Published v12/v13/v14 definitions remain unchanged from the refinement baseline.

Fresh verification:

- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~AgentWorkspaceJourneyTests`: 10 passed. The integrated managed journey checks resolved delete/batch previews, unchanged hashes/arguments, stale CAS rejection, binary tree copies, owner isolation and Session cleanup; SQLite reopen remains covered.
- `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Workspace|FullyQualifiedName~ToolWorkflow'`: 70 passed, including the added legacy approval regression and runtime workflow boundaries.
- Node 22 Playwright CLI, `e2e/agent-workspace-v2.spec.ts e2e/agent-workspace.spec.ts e2e/workspace-filesystem.spec.ts --project=synthetic`, task ports 5086/5176: 4 passed. Managed/legacy durable workspace and scratch/home exact approval rejection/acceptance journeys passed.
- Playwright MCP against a separate disposable SQLite Synthetic host: created a v15 managed Session, sent the setup command through Chat, observed home→scratch→home operations and Artifact publication, downloaded `result.md` and verified its exact 22 UTF-8 bytes, then sent the cwd command and observed `/working`. Current navigation console: zero errors. An initial MCP attempt coincided with CLI-owned host shutdown; it was repeated successfully on the dedicated host.
- `git diff --check`: passed. The original exact-SHA hosted results above belong to `0cadbe57`; they are not evidence for this later correction. The focused checks above verify the correction locally.

Task-local logs: `/tmp/workspace-review-api.log`, `/tmp/workspace-review-application.log`, `/tmp/workspace-review-browser.log`. Full frontend/backend and Compose gates were already green for the accepted enhancement; the bounded preview correction did not change storage, packaging, frontend source or provider contracts.
