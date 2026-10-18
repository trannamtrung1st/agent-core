# Tenant workspace human mutation verification — 2026-10-19

Human home and Session working files now accept independently authorized replacement/upload and nonrecursive deletion through the existing canonical filesystem. This checkpoint extends `601caafa`; the complete multi-tenant proposal remains active.

## Implemented boundary

- Both canonical workspace groups accept raw-byte PUT content and nonrecursive DELETE content with exact logical paths and current tenant security revision. The revision is an authorization/configuration witness, not a file version. Existing per-file/root limits and ordinary concurrent-writer semantics apply. Root replacement/deletion and traversal aliases are rejected. Path and authority are checked before body buffering; protected execution checks again.
- `TenantWorkspaceMutationService` requires the original Account/login/membership, independent parent read and the exact `workspaces.write` or `workspaces.delete` permission. Write permission does not grant body read or deletion. The actual Instance must be active, and a working Session must be retained, unarchived and nonterminal; paused human file editing remains allowed. No runtime templates or Sandbox preparation run under human file authority.
- The existing file adapter repeats actual ownership/lifecycle checks, stages bytes with the incumbent filesystem, and calls current-human/security/Instance/IAM validation immediately before rename/unlink. Private staging may create directories or temporary bytes; rejected commit preserves the destination and cleans the temporary file. No source-body/path audit, second filesystem, database schema, file version, mutation receipt or Agent grant engine is introduced.
- Both authorization audit stores independently validate the actual owner, original human, witnesses, current permissions and writable lifecycle before appending authorization-only evidence. Allowed evidence is not a completion claim or authority for a later call. Database evidence and filesystem effects are not one transaction.
- A known completed protected primitive returns body-free 204 even if access is lost immediately afterward; later reads reauthorize. Unexpected I/O is a conservative 409 `workspace_effect_unknown` with `mutationsMayHaveOccurred: true`, static safe copy and no automatic replay. CSRF retains the incumbent `400 invalid_csrf` contract. Download remains independently authorized.

Canonical owners: [architecture](../03-system-architecture.md#human-tenant-workspace-mutations), [ports](../04-backend-interfaces.md#human-tenant-workspace-mutation-boundary), [wire contract](../14-api-and-realtime-protocol.md#human-file-mutations), [persistence](../15-persistence-and-configuration.md#human-tenant-workspace-mutation-persistence), [testing](../16-testing-strategy.md#tenant-workspace-human-mutations) and [UX](../19-multi-tenant-iam-ux.md#human-file-changes).

## Execution evidence

Execution uses disposable Synthetic memory/SQLite owners, actual canonical filesystem bytes and authenticated HTTP. No installed/live database, Real host or user workspace root is reset, migrated or restarted. No provider spending, CI or full suite is requested or run.

The first four HTTP journeys pass: home and working with both stores. They exercise denied write without directory initialization, scoped write saving exact UTF-8 bytes with a body-free acknowledgement, independently denied reader/delete, scoped delete, stale witness retaining original bytes, nonempty directory preservation, empty-directory deletion and missing-CSRF rejection. The test originally expected 403 for missing CSRF; inspection confirmed the incumbent middleware intentionally returns `400 invalid_csrf`, and the corrected assertion passed. An initial rerun compiled but could not open the local test runner socket under the sandbox; execution succeeded with local socket permission. Neither initial failure is counted as a passing run.

The expanded boundary matrix exercises revocation, logout followed by a different login, Instance archival and unrelated tenant security changes after staging but before the protected write/delete. Working Session deletion is also injected at that boundary. Destinations retain exact original bytes, staging files are removed and no allowed authorization evidence is appended. Separate cases cover unavailable audit acknowledgement, paused versus archived/completed working state, malicious audit proofs/replay, caller-byte freezing before filesystem I/O, root/alias/foreign-owner rejection, cancellation and denied commit callbacks. After-effect revocation preserves a factual success; an injected after-effect I/O failure reports uncertainty once, preserves the actual changed bytes and exposes no internal path.

Intermediate test fixtures were corrected before final execution: canonical workspace initialization must precede trusted file seeding, and direct audit replay proofs must use the incumbent UTC millisecond precision used by the service and SQLite converter. The first expanded run after initialization correction passed 63 of 64; the corrected audit fixture then passed both stores. These fixture failures are not counted as product evidence or passing checks. The first new-test compile also caught nullable filename assertions, corrected without changing product behavior.

Final focused commands:

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_HTTP|FullyQualifiedName~Tenant_model_publication_returns_one_exact|FullyQualifiedName~Tenant_deleted_Artifact_cleanup_recovers|FullyQualifiedName~Tenant_Artifact_verification_model_uses_independent_read|FullyQualifiedName~Tenant_Artifact_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests --no-build --no-restore --filter 'FullyQualifiedName~Tenant_workspace_|FullyQualifiedName~Deleted_Artifact_cleanup|FullyQualifiedName~TenantSessionIntegrationTests|FullyQualifiedName~CanonicalWorkspaceFilesystemTests|FullyQualifiedName~AgentWorkspaceStoreTests|FullyQualifiedName~Execution_audit_|FullyQualifiedName~Execution_authorization_audit_|FullyQualifiedName~Actual_run_authorization_persists|FullyQualifiedName~Empty_permission_inventory_and_unavailable_executor|FullyQualifiedName~Missing_authorization_audit_acknowledgement' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The infrastructure binary was built by the corrected audit recheck; no infrastructure source or test edits occurred afterward. The final API build includes the upload-buffer improvement and additional anonymous/invalid-path HTTP cases. Both processes reached terminal exit 0, with no failures or skips:

| Focused check / machine-local log | Result |
| --- | --- |
| Initial mutation HTTP journey: `/private/tmp/agent-core-workspace-mutations-http.log` | 4 passed, both stores × home/working |
| Corrected direct audit replay: `/private/tmp/agent-core-workspace-mutations-audit-recheck.log` | 2 passed, both stores |
| Final HTTP regression: `/private/tmp/agent-core-workspace-mutations-final-api.log` | 23 passed; 8 workspace read/mutation cases and 15 affected Artifact publication, verification, cleanup/restart and HTTP cases |
| Final store/filesystem regression: `/private/tmp/agent-core-workspace-mutations-final-store.log` | 163 passed; current workspace read/mutation cases, canonical filesystem, workspace store, Session/Artifact cleanup and existing authorization-audit/preference boundaries |

HTTP additionally rejects anonymous uploads, protected-root replacement, traversal and wrong-root paths before owner-directory creation. Saved bytes and body-free responses match expected state, read remains independently denied to the writer, and fixture outbound provider attempts remain zero. All task-owned build/test processes are terminal; no source/test edits were made while any build/test process was live.

Relative paths, six new report owner anchors and balanced Markdown fences passed across twelve changed Markdown files (1,456 relative links). `git diff --check` passed. These are focused local results, not full suite, CI, browser or milestone acceptance.

## Remaining scope

No frontend component, shared layout or design token changed; no browser/UI acceptance is claimed for these human mutation routes. Tenant Workspace UI, Agent workspace read/write tools and exact file consent/grants, source publication adapters, other resource/transport/autonomous families and final obsolete-authority/schema cutover remain open. No full proposal or milestone acceptance is claimed. Earlier dated reports retain their original scope.
