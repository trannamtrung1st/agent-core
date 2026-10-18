# Tenant workspace read verification — 2026-10-19

Human home and Session working reads now use canonical tenant ownership and independent scoped IAM through the existing file adapter. This checkpoint extends `ad0b19ff`; the full multi-tenant proposal remains active.

## Implemented boundaries

- Resource type `workspace` accepts exact `home:{instanceId:D}` and `working:{sessionId:D}` addresses. Both IAM stores resolve actual current owners before granting or committing assignments. A string or GUID cannot claim a foreign/unowned workspace. Existing assignment schema represents the addresses; no migration, file entity, per-file IAM hierarchy or second filesystem was added.
- `TenantWorkspaceReadService` requires current original Account/authentication session/membership, independent `workspaces.read`, and `agents.read` for home or `sessions.read` for working. Human reads do not borrow the Agent's permissions or offer Agent tools. Security, actual owner and owning revisions are rechecked after I/O and projection; a fresh login cannot replace a revoked original session reference.
- `FileAgentInstanceWorkspaceStore` implements the scoped read port over the incumbent canonical filesystem. It separately validates actual Instance/Session/executor ownership before deriving a root. Listing/searching an uninitialized owner returns empty without creating owner directories, running templates or provisioning Sandbox. Existing shared layout validation remains startup behavior.
- List/search, metadata, exact original downloads and DOCX/XLSX preview have authenticated tenant routes. Native workspace wire records and the common bounded Office processor are reused. Metadata-only reads return no body. Downloads use attachment disposition, safe names, private/no-store caching and `nosniff`. Paths and cursors are logical bounded observations, not stable file versions or CAS tokens.
- Internal safe links remain readable through the canonical resolver. List/search preserve only targets within the bound virtual workspace; outside physical target strings are withheld, and escaping reads remain denied. The added resolved-stat method preserves the existing ordinary stat behavior. Source observations are compared around content/projection; changed metadata withholds the result, without claiming a mutable filesystem transaction.
- Canonical Session deletion atomically retires its working-workspace assignments with its other direct grants. Both owning stores reject a bypass transaction retaining that scope. Home and sibling scopes remain independent; subsequent IAM writes still succeed. Old unscoped file adapters remain excluded from actual owned Instances.

The [architecture](../03-system-architecture.md#tenant-workspace-read-ownership), [ports](../04-backend-interfaces.md#tenant-workspace-read-boundary), [wire contract](../14-api-and-realtime-protocol.md#tenant-workspace-read-contract), [persistence](../15-persistence-and-configuration.md#tenant-workspace-read-scopes) and [UX contract](../19-multi-tenant-iam-ux.md#tenant-workspace-reading) own their respective facts.

## Focused execution

Execution used Synthetic adapters, actual canonical filesystem bytes and disposable memory/SQLite owners. Provider HTTP attempts remained zero. No installed/live database, Real host or user file root was reset, migrated or restarted. No CI or full suite was requested or run.

| Check / machine-local log | Observed result |
| --- | --- |
| First integrated home/working boundary (`/private/tmp/agent-core-workspace-reads-first.log`) | Two pass, memory and SQLite |
| HTTP file and Office journey (`/private/tmp/agent-core-workspace-reads-http-final.log`) | Four pass: home and working with both stores |
| Expanded ownership/authority matrix (`/private/tmp/agent-core-workspace-reads-boundaries-fixed.log`) | 28 pass |
| Final store/filesystem/Session regression (`/private/tmp/agent-core-workspace-reads-final-store.log`) | 75 pass: 28 workspace cases and 47 affected existing filesystem, workspace-store, Session and Artifact cleanup cases |
| Final integrated API regression (`/private/tmp/agent-core-workspace-reads-final-api.log`) | 19 pass: four workspace cases, seven publication/deletion/restart cases, six metadata verification cases and two original Artifact HTTP cases |

The workspace matrix ran `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`. The final API command was `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_HTTP|FullyQualifiedName~Tenant_model_publication_returns_one_exact|FullyQualifiedName~Tenant_deleted_Artifact_cleanup_recovers|FullyQualifiedName~Tenant_Artifact_verification_model_uses_independent_read|FullyQualifiedName~Tenant_Artifact_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`.

The final store command was `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_|FullyQualifiedName~Deleted_Artifact_cleanup|FullyQualifiedName~TenantSessionIntegrationTests|FullyQualifiedName~CanonicalWorkspaceFilesystemTests|FullyQualifiedName~AgentWorkspaceStoreTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`. Final runs passed with zero skips, and all task-owned test processes reached terminal completion. No source/test edits were made while build/test processes were live.

Actual service/file cases exercised empty roots without initialization, independent home/working and parent grants, exact UTF-8 bytes, metadata without body, search, original-session logout despite a new login, read revocation, Session deletion during read, changed files, and revocation after projection processing. Same-Account Alpha/Beta ownership, wrong trusted references, nonmember access, sibling Sessions and direct IAM scope injection all failed closed. Working-grant retirement rejected bypass deletion, retained home access and allowed later IAM writes. Safe relative links returned their exact bytes; outside link targets were neither disclosed nor followed.

Authenticated HTTP exercised empty → seeded real files → denied reader → exact workspace grant → paged list/search/metadata/download/Office preview → revoke → denied metadata/content/preview. Anonymous reads returned 401. UTF-8 bytes, cache/disposition headers, DOCX checksum/nonempty-body inspection and XLSX checksum/cell values matched the saved originals. Repeated/bounded query checks and wrong-root requests were rejected. Fixture byte seeding used the trusted canonical filesystem; this is human read/HTTP evidence, not model workspace-write or publication evidence.

Intermediate fixture failures were corrected and rechecked: a missing Synthetic-model import, an assertion assuming inline DOCX text rather than the incumbent checksum/empty-body response, and a same-Account setup selecting a single global principal after registering a second Account. The latter now selects the explicit owning Account principal. These failures are not counted as passes or evidence of product defects.

## Scope and remaining work

No frontend component, layout or token changed; no browser or visual acceptance is claimed for the new read routes. The tenant Workspace UI, write/delete and model/tool paths, exact file-operation consent/publication adapters, other resource/transport/autonomous families and final obsolete-authority/schema cutover remain open. No full proposal or milestone acceptance is claimed. Existing design-context drift was not changed by this backend checkpoint.

Relative paths and balanced Markdown fences passed across twelve changed documentation files (1,451 relative links); all five new owner anchors resolved. `git diff --check` passed. Existing dated reports remain historical evidence for their original scope.
