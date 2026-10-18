# Tenant Artifact cleanup verification — 2026-10-19

Committed canonical Session deletion now drives tenant-owned Artifact byte cleanup through the existing supervised cleanup worker. This checkpoint extends `c91b7703`; the full multi-tenant proposal remains active.

## Ownership and recovery

`ITenantArtifactCleanupStore` enumerates at most 100 actual retained deletion witnesses with an exclusive Session cursor. Cleanup repeats the exact tenant/Session/Instance/executor/creator owner, deletion timestamp and quiescent-Run checks. A live, archived, merely terminal, foreign or altered owner cannot authorize removal. There is no new human/model endpoint or caller-selected filesystem path.

The immutable deletion tombstone is both authorization and retry source. `TenantArtifactCleanupService` serializes bounded scans, isolates each failed target and retries on scan wrap or restart. `WorkspaceCleanupHostedService` runs this scan independently at startup and its existing five-minute interval. It uses the existing safe diagnostic logger; private exception messages, filenames, physical paths and bodies are not emitted.

SQLite purges only the derived `tenants/{tenant:N}/{session:N}` directory through `CanonicalWorkspaceFilesystem.PurgeOwner`, reusing descriptor-relative no-follow traversal. This removes committed bytes and unreachable final/partial staging files without following nested links. Missing paths converge; a substituted owner, tenant or blob root fails closed. InMemory releases matching byte buffers under the actual Session admission lock.

Immutable metadata, producing ownership, operation receipts and tombstones remain. Retention blocks Artifact identity/grant reuse and permits retry after interruption or a late failed publisher's private staging write; no permanent filesystem-completion marker is inferred. No table, migration, second deletion ledger or broad directory sweep was added. Active-Session orphan sweeping is not claimed.

Canonical Session deletion also retires its exact Artifact grants with Session and Run grants in the existing security/audit transaction. Both owning stores reject a direct deletion transaction that retains an actual child Artifact grant. Sibling grants remain. This fixes a related lifecycle gap found during the cleanup review and prevents stale resource assignments from obstructing later IAM writes.

## Focused execution

All destructive execution used dedicated temporary fixture roots. Synthetic mode made zero outbound provider attempts. Existing installed data, Real hosts, user directories and databases were not reset, migrated or restarted. No CI or full suite was requested or run.

| Check / machine-local log | Observed result |
| --- | --- |
| First integrated owner cleanup (`/private/tmp/agent-core-artifact-cleanup-first.log`) | Two pass, memory and SQLite |
| Expanded boundaries (`/private/tmp/agent-core-artifact-cleanup-boundaries.log`) | 12 pass |
| Final owning-store/Session checks (`/private/tmp/agent-core-artifact-cleanup-final-store.log`) | 46 pass: 14 new cleanup cases and 32 existing affected Artifact/Session cases |
| Actual model/HTTP deletion and host recovery (`/private/tmp/agent-core-artifact-cleanup-http.log`) | Seven pass, including actual SQLite application host disposal/recreation |
| Final integrated regressions (`/private/tmp/agent-core-artifact-cleanup-final-api.log`) | 25 pass: the seven publication/deletion/restart cases, eight exact publication Review/grant cases, four reviewed rich-reference successors and six exact metadata verification cases |

The final store command was `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Deleted_Artifact_cleanup|FullyQualifiedName~Owned_Artifact_|FullyQualifiedName~TenantSessionIntegrationTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`. The integrated command was `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_model_publication_returns_one_exact|FullyQualifiedName~Tenant_deleted_Artifact_cleanup_recovers' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`.

The final regression command was `dotnet test tests/AgentCore.Api.Tests --no-build --filter 'FullyQualifiedName~Tenant_model_publication_returns_one_exact|FullyQualifiedName~Tenant_deleted_Artifact_cleanup_recovers|FullyQualifiedName~Artifact_model_HTTP_exact_review_or_grant|FullyQualifiedName~Reviewed_Artifact_successor|FullyQualifiedName~Tenant_Artifact_verification_model_uses_independent_read' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`. All checks passed with zero skips; no failing check was omitted or counted as a pass.

Store scenarios exercised live-owner refusal, exact deletion/owner mismatch, pre-cancellation without removal, archive/terminal retention, direct child-grant bypass rejection, current grant retirement and subsequent IAM writes, metadata retention, same-Account Alpha/Beta tenants, a live sibling Session, bounded paging and a per-target I/O failure. The failed target retained its bytes while its neighbor was cleaned; the next pass converged without touching the live sibling. SQLite fixtures separately exercised unknown staged final/partial files, nested links, three substituted roots, safe recovery, missing paths and a newly constructed store/service.

The actual model loop published and downloaded exact UTF-8 content under Allowed policy. An authenticated HTTP Session DELETE retired its exact Artifact grant and made download return 403. Explicit worker execution then removed the byte directory while the exact immutable publication receipt remained. In the restart case, the original application host was disposed with deletion already committed and files still present. A new host over the same disposable database/root removed those files through its startup worker without a manual recovery call; fresh authenticated download stayed denied and the exact receipt survived. This is actual application-host recovery evidence, distinct from store reopen.

## Scope and remaining work

No frontend behavior, control, layout or design token changed; no browser or visual-polish acceptance is claimed for this backend worker. Existing approval/delivery UI remains governed by its prior focused reports. No source/test edits were made while build/test processes were live; all task-owned processes are accounted for before delivery.

Relative paths and Markdown fences passed across eleven changed documentation files (1,388 links), both new owner anchors resolved, and `git diff --check` passed. SQLite fixtures confirmed no pending EF model changes. No new deployment configuration or destructive setup command was introduced.

This closes Artifact byte cleanup after committed canonical Session deletion. It does not complete attachment/workspace/Browser/Sandbox cleanup, tenant decommission, Browser/Workspace/Office publication adapters, other resource/transport/autonomous families or final obsolete-authority/schema cutover. Retained historical verification reports keep their original scope. The full proposal remains active.
