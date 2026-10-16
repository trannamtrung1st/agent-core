# Approved shipped-template fork verification — 2026-10-16

This checkpoint continues the full tenant/IAM/approval replacement after `3329ed35`. It implements canonical backend discovery/fork of exact shipped templates, including approved content and immutable draft provenance. The full tenant editor/runtime/resource/approval cutover remains in progress.

## Implemented boundary

`GET /api/v2/tenants/{tenantId}/definition-templates` and `POST /api/v2/tenants/{tenantId}/agent-definitions/from-template` require current tenant Definition-create authority. They derive the actor from the Account cookie and current membership; POST requires CSRF and security revision. Instance-create authority is separate. A creator receives only its new draft projection; creating a Definition does not grant subsequent read/update authority or agent roles.

The source port resolves only an exact shipped definition ID/version and approved bytes. No tenant or global durable publication lookup is a fallback. Approved knowledge comes from the trusted agent directory's `knowledge/{identity}.md`; its declared resource path is the binding destination, never a source-file selector. Workspace assets come only from the configured template root and selected template ID, with the same top-level knowledge/harness exclusions as first provisioning. Skills may reference these approved bindings; arbitrary agent-directory files are not an implicit catalog.

File-source reads/listing reuse the descriptor-relative filesystem with explicit no-link operations. Ordinary workspace link semantics retain their existing behavior. Existing filename/path/media/secret policies, 8 MiB per file, 64 resources / 64 MiB aggregate, and a 256-directory bound apply. Missing/unapproved dependencies, linked or invalid content reject the fork before an owned graph exists. Source files are read only.

Fork generates a new canonical Definition ID, draft and resource IDs. The actual root/draft/initial bindings/security CAS/audit commit atomically; every physical byte binding is verified before commit. `AgentDefinitionDrafts.SourceDefinitionId` retains the original template ID with existing source kind/version. Subsequent canonical updates cannot change provenance/creation time. Publication freezes the captured bindings and bytes, so later changes to shipped files do not alter the owned version. Shared content-addressed bytes may deduplicate; binding identities/authority remain independent. A lost CAS can leave private unbound staged content under the existing conservative retention policy.

Migration `20261016141917_TenantDefinitionTemplateProvenance` adds the nullable source ID (max 64). It was generated and tested only on fresh temporary SQLite databases. Final schema consolidation remains pending; this checkpoint did not migrate/reset user data.

## Executed use cases

The first runnable check forked actual shipped Support v3 on both stores, read its exact approved knowledge through the tenant draft, edited/published it, downloaded the same bytes from the owned publication, created an independent second fork and reopened SQLite to verify provenance/bindings. The original shipped identity/instructions stayed unchanged. **2/2 passed** before coverage expansion.

Additional both-store checks verified approved workspace/Skill dependencies, exclusion of non-workspace harness content, independent copied bytes after changing the source file, rejected provenance edits, missing/secret/file-link/directory-link/knowledge-link/oversized/unapproved-Skill rollback, and initial manifest failure after one valid binding without partial root/draft/resource/audit state. Stale security, global Account suspension and cancellation while source preparation was paused prevented commit. The suspension fixture was corrected to select the explicitly authorized owner's global Account principal rather than assume there was only one Account.

The Synthetic HTTP flow used a custom role containing only `agent-definitions.create`. Template discovery/fork worked while the separate Instance-template API denied. Missing CSRF and nonexistent exact versions failed without revision changes; a valid fork returned its new owned identity/source provenance. Subsequent private draft reads denied without read permission. The Owner read/published/downloaded identical copied knowledge, stale retries left one root, another tenant's durable publication could not serve as a shipped template, and an Instance could be created from the resulting exact active owned publication. **1/1 passed** before final regressions.

The shared filesystem regression rejected an asset swapped to a symlink immediately before open, and rejected symlink file/directory traversal for approved reads while verifying normal workspace link reads still work.

## Final focused checks

```bash
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~TenantDefinitionIntegrationTests|FullyQualifiedName~IamIdentityIntegrationTests|FullyQualifiedName~AgentDefinitionAdminStoreContractTests|FullyQualifiedName~CanonicalWorkspaceFilesystemTests|FullyQualifiedName~DefinitionResourceInfrastructureDiTests' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~TenantDefinitionAccessTests|FullyQualifiedName~TenantAgentAccessTests|FullyQualifiedName~IdentityAndTenantAccessTests|FullyQualifiedName~IdentitySqliteRestartTests' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal
dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build
git diff --check
```

Results: **105/105 Infrastructure**, **14/14 API**, zero skips; EF reported no pending model changes; whitespace validation passed. The 10.0.5 EF tool printed its advisory against runtime 10.0.12; generation/model checks succeeded. Commands used approved local execution for VSTest loopback, Synthetic adapters and owned disposable storage. All nine changed Markdown files passed 461 local link/fragment checks and balanced-fence validation.

## Remaining full-goal boundaries

Complete tenant Definition/editor/child/deletion UI, standalone resource IAM/previews and tenant-aware runtime resolution still require implementation. So do the other owning stores and Session/Run/Hub/tool/background effects, trusted execution lineage, durable exact approval continuation, obsolete authority removal, final schema consolidation and full responsive browser acceptance. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) preserves the full objective. No browser acceptance, CI, provider requests, user-data reset, deployment or user-host restart is claimed here.
