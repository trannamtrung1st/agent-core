# Agent Instance workspace — final verification

> Historical verification. Identity/workspace compatibility statements are superseded by the unified migration described in [current architecture](../03-system-architecture.md) and [migration verification](agent-workspace-full-migration-verification.md). Original evidence remains unchanged.

Status: closed/frozen on behavior candidate `b21484d4740e2395c6b0b362e64a9e8fb7d5b0cb`, with [hosted Synthetic/Compose run 37474445339](https://github.com/trannamtrung1st/agent-core/actions/runs/37474445339) green on 2026-10-06. This is a bounded post-P9.10 enhancement; historical P9.8/P9.9/P9.10 freeze SHAs remain unchanged and P10/P11 remain unopened.

## Candidate and scope

The implementation reconciled the proposal baseline `3c784a4d` with subsequent Artifact closure documentation and Admin test headroom changes on current main. Published behavior SHA: `b21484d4740e2395c6b0b362e64a9e8fb7d5b0cb`. The exact-SHA hosted workflow passed all five jobs: backend, frontend, primary browser, acceptance browser and Compose. Later closure documentation does not move this behavior freeze.

A managed Agent Instance owns bounded durable `/home` working material. Session `/workspace` remains scratch. Explicit retain copies exact bytes into home; explicit checkout creates an independent session copy. Revision/hash compare-and-swap prevents silent replacement. Compatibility instances have no home; archive preserves inspection and disables mutation. Permitted hard deletion removes the owning instance's metadata and bytes. No Definition, memory, Artifact or Session ownership is changed.

SQLite owns dedicated metadata and Infrastructure owns opaque local immutable blobs. New bytes are flushed and renamed before metadata commit; failed commits remove new blobs and preserve established metadata. Replacement removes superseded blobs; first owner access after reopen sweeps bounded unreachable opaque/partial blobs. Limits are 50 MiB/file, 250 MiB/instance, 4096 items, 256 entries/page, eight path segments and 512 path characters. Existing bounded workspace search/output limits remain authoritative.

`general-assistant` v13 adds `workspace.retain` and `workspace.checkout` without modifying immutable prior versions. Tool registration, normal allowlist/effect policy and trusted Session ownership remain separate fences. Detached execution gains no Session scratch privileges. Prompts carry only a compact home capability hint; no inventory or file contents are inserted automatically.

## Canonical observed journey

The deterministic browser/API journey created managed identities A and B, wrote known UTF-8/CRLF bytes in A1 scratch, verified that home stayed empty, explicitly retained them, then ended and durably deleted A1. A2 searched home through normal Synthetic Chat tools, read the exact source, checked it out, edited the separate scratch copy, retained revision 2 with the original revision/hash, and received conflict for a stale replacement. Normal Artifact publication produced a fresh session-owned card whose browser download matched revised bytes exactly. Admin home download matched those same bytes; B listed an empty home and could not retrieve A's item. Archive retained download and disabled Delete.

Separate SQLite API-host reopen and Compose container recreation verified metadata/hash/exact-byte survival after source-session deletion. The reopened API host also checked out the retained item and verified permitted instance hard-delete cleanup. Artifact endpoints rejected durable item IDs.

Direct Playwright MCP additionally exercised visible Workspace navigation, exact-byte download, Delete cancellation followed by confirmed deletion and the resulting empty state. Workspace requests completed with 200/204 and no related console errors. At 1440, 768 and 390px, the document did not overflow; wide rows scroll within the reused Admin table. Screenshots were inspected at desktop and mobile widths. The UI reuses Ant Design v6, existing Admin panel/toolbar/table/pagination/confirmation patterns and shared spacing/action rules. Unit coverage verifies loading, empty/error/retry, long-path search, archived read-only treatment and delayed old-owner response rejection.

## Acceptance evidence

| Criterion | Evidence |
| --- | --- |
| AC1 isolated durable ownership | Both-profile store isolation, API wrong-instance 404 and browser identity B empty home |
| AC2 explicit persistence | API/browser scratch write leaves home empty; edits after checkout preserve the established durable bytes |
| AC3 exact retain | Binary store fixtures and canonical source size/hash/byte assertions |
| AC4 cross-session retrieval | Deleted A1 followed by A2 list/read and normal Synthetic workspace.search |
| AC5 source deletion survival | API/browser durable Session deletion and exact retained read; no provenance cascade |
| AC6 exact checkout | API/browser source/destination hash and byte equality; existing destination conflicts |
| AC7 safe update | Stable item ID, incremented revision, missing/stale token conflicts and one concurrent winner in both store profiles |
| AC8 compatibility exclusion | Compatibility home returns normal unavailable validation and effective tool projection excludes boundary actions |
| AC9 quota | Per-file/projected aggregate rejection in both profiles; established item/blob remains; existing resource-limit diagnostics reused |
| AC10 path safety | Traversal, raw host path, reserved/secret/hidden names, Unicode/path/depth bounds, portable case/file-directory collisions and escaping owner symlink tests |
| AC11 restart durability | SQLite store reopen, API-host reopen and Compose SQLite-volume survival |
| AC12 archive | Archived home remains readable/downloadable; owner deletion/retain/checkout remain rejected by Application lifecycle checks |
| AC13 hard delete | InMemory/API and SQLite reopened-host lifecycle cleanup plus idempotent store deletion; other owner remains |
| AC14 Artifact separation | Home item is rejected by Artifact route; existing delivery/history regressions retained |
| AC15 republication | A2 creates a fresh session Artifact and Chat downloads exact revised bytes |
| AC16 prompt bounds | Compact availability flag/hint; no inventory prompt load; tool list/search/read/output bounds retained |
| AC17 Admin inspection | Visible MCP and repeatable browser list/download/cancel/confirm-delete; UI state/recovery tests |
| AC18 design/responsive | Reused Admin components/tokens, viewport overflow checks and inspected 1440/390px captures |
| AC19 scratch preserved | Existing workspace/attachment/sandbox/session cleanup regression coverage; exclusive checkout adds no home mount |
| AC20 phase boundary | One local managed home, no shared/task/application store, remote storage, tenancy, scheduler or sandbox redesign |

## Verification gates

Raw local logs and viewport captures are under ignored `local/agent-workspace-evidence/`; executable fixtures are committed. Hosted results are attached to the exact-SHA run above.

| Local gate | Observed result |
| --- | --- |
| Domain suite (`dotnet test …Domain.Tests.csproj`) | 145 passed |
| Application suite (`dotnet test …Application.Tests.csproj --blame-hang --blame-hang-timeout 5m`) | 1234 passed, 1 explicit live-provider skip |
| Infrastructure suite (`dotnet test …Infrastructure.Tests.csproj`) | 747 passed, 15 explicit opt-in/live/Docker skips; new store contracts included |
| API suite (`dotnet test …Api.Tests.csproj`) | 376 passed, 2 explicit live-provider skips; canonical and SQLite reopen journeys included |
| Order-event adapter suite (solution run) | 4 passed |
| Workspace focused coverage | Path rules 15 passed; store contracts 6 cases included in full Infrastructure; API journeys 3 passed; UI 5 passed |
| Primary Synthetic/browser Playwright | Initial local run 97/98; failed pre-existing publication-toast scenario subsequently passed on fresh host, alongside final canonical workspace journey (2/2). Exact-SHA hosted primary gate: 98/98 passed |
| P9.8/P9.9 continuity Playwright | 2/2 passed, including Experience/Thought and responsive owner controls |
| P9.10 identity-maintenance Playwright | 1/1 passed, including consolidation, lineage, opt-out, explicit forget and reload |
| Node 22 frontend serial unit gate | Final candidate run: 702/702 passed across 94 files; exact-SHA hosted frontend gate also 702/702 |
| Production `pnpm run build` | Passed; existing large-bundle warning remains |
| `COMPOSE_PROJECT_NAME=agent-workspace-verify ./scripts/compose-sqlite-volume.sh` | Passed twice, including final orphan-recovery implementation; retained home/hash/exact bytes survived container recreation after source deletion |
| `git diff --check`; local documentation links/fences | Passed; 11 affected docs checked |

Hosted backend results match local counts: Domain 145, Application 1234, Infrastructure 747 and API 376 passed, with the same 18 explicit opt-in/live/Docker skips. Hosted acceptance passed Manual-A 1, Admin lifecycle 1, P7.6 Admin 1, P9.7 harness 7, P9.8/P9.9 continuity 2, P9.10 maintenance 1 and Secretary 4. Hosted Compose verified the retained home item after container recreation/source deletion. No provider credentials were required or used by the Synthetic gates.

The first broad local run used excessive concurrent workers and an unexpected Node 26 default, yielding unrelated Admin/jsdom timeouts and unavailable localStorage. The subsequent Node 22 concurrent run also timed out. These are failed setup/regression attempts, not passing evidence. The final frontend gate uses the existing hosted command with `--maxWorkers=1` and Node 22. A backend broad run exposed an existing response-terminal race; a later API run timed out starting a disposable Kestrel process while other gates competed for resources. The primary browser run passed 97/98, with an existing draft-publication toast check failing; that scenario is rerun against a fresh host. No important assertion or policy gate was weakened to close the enhancement.

## Stop boundary

The exact behavior candidate passed hosted Synthetic/Compose, so this bounded slice is closed. Application/task/shared workspaces, agent sharing/delegation, cloud sync, remote storage, generalized host filesystem, P10 and P11 require separate concrete requirements. Later authorized enhancements must preserve this report as historical evidence of this slice.
