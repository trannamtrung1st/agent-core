# Tenant resource Artifact publication verification — 2026-10-19

## Scope

This successor to `7dd3dca8` extends the existing tenant `artifacts.create_from_workspace` / `publish_workspace_artifact` contract to one exact admitted `/agent/resources/...` or `/agent/instance/resources/...` file. Home/working publication retains its existing contract. The full proposal remains active; other virtual resources, Attachment/Browser adapters, autonomous/transport families and final obsolete authority/schema removal remain open.

Core resolves the literal virtual path through the actual current scoped resource graph and admitted immutable source. No writable virtual filesystem, path alias, caller source key/hash or legacy fallback is introduced. Export capability is checked independently of read-tool capability. Current independent Session, Agent and exact child `resources.read`, raw source policy, actual owner/lifecycle, binding/revision and Run claim fences repeat around byte I/O. Resource paths may contain literal metacharacters; they are never glob-expanded or used as physical filenames. The resource content store enforces its existing size/media rules; publication copies complete raw bytes without interpreting Office packages.

The incumbent `PreparedArtifactSource` adds optional resource key/revision fields, omitted for home/working witnesses. Exact source metadata/hash/revision bind the action while semantic root correlation retains the publication intent. Source materialization carries frozen, verified bytes through the existing storage-only facet; it never reenters IAM or a database from inside publication locks/transactions. Both stores revalidate the current resource revision in the same transaction as consent or publication. SQLite reviewer/source-policy checks use ownership already resolved by that transaction and still evaluate current principal/permission/role state.

Publication uses the existing atomic Artifact/Run/receipt owner. Review and exceptional grants remain exact, with grants covering only primary creation. Consent cannot substitute for raw source reads or grant role, later discovery, another publication or recipient download access. Inbox filtering proves actual resource ownership and each human source read before counts. Scoped reviewers need approval authority for required source policy too. Current evidence is rechecked on decision and execution; refused publication remains suppressed in the same root. Exact duplicate calls return the saved receipt without recopying a changed source.

The shared source inspector retains its six body-free name/type/size/path/checksum/disclosure rows. No frontend component, CSS, theme or design sidecar changes are introduced; backend/HTTP evidence does not claim a fresh resource-specific browser presentation pass.

## Executed verification

The representative direct/export/consent integration selection passed eight cases on memory/SQLite (16s, zero skips): complete DOCX bytes with a literal metacharacter path, inherited source origin, one durable receipt, duplicate replay, exact Review and creation-only exceptional grant. Subset runs are not additional distinct acceptance cases.

All final owning processes reached exit 0: **492 distinct focused cases passed, zero skips**. The representative eight-case, initial 19-case HTTP and four-case correction runs are subsets, not additional distinct acceptance cases.

| Final focused check | Result |
| --- | --- |
| Source/export/read Infrastructure regressions | 348 passed, 8m20s, zero skips |
| Actual model/HTTP and host recreation | 72 passed, 5m40s, zero skips |
| Source/read contract/projection regressions | 72 passed, 115ms, zero skips |

The infrastructure build passed with zero warnings/errors. EF generated `20261019085021_TenantArtifactResourceSourceReview` without applying it to installed data. The known EF tool/runtime version warning is not correctness evidence; disposable migrated fixtures supply that proof.

The representative model/HTTP selection passed 19 cases (1m26s, zero skips), including direct publication, primary/source-policy Review, creation-only grants, missing raw reads, stale source, revoked authority, rejection and pending-consent SQLite application-host disposal/recreation with native resource bytes. Approved results downloaded as the exact original package; refused operations produced no Artifact. No provider attempts occurred.

The expanded Infrastructure selection passed 24 of 25 cases and caught a SQLite inbox candidate omission. After correction, the focused inbox/capability regression passed four cases (13s, zero skips): workspace access alone exposed no request, exact resource reads exposed it, source-policy approval required the matching child scope, revocation hid it again, and deletion hid it from Owner counts. Removing current workspace-read tool capability retained authorized export. Final selections include these scenarios and replace subset counts.

Initial failures also caught a test helper collision, invalid empty-owner key validation and a nested SQLite ownership transaction. Each correction was rerun through integrated scenarios before expanding checks; failed selections are not claimed as acceptance evidence.

Final Infrastructure outcomes matched the intended boundaries: each missing raw read prevented source byte I/O; binding changes and revocation before commit produced no Artifact or receipt; scoped source reviewers and source deletion filtered details/counts correctly; rejection stayed suppressed after policy relaxation. SQLite rejected six malformed/mixed resource-source shapes, refused lossy narrowing and reopened with the exact pending consent preserved. Existing workspace source integrity, claim/cancellation, atomic rollback and recovery cases remained green.

Application checks confirmed that resource paths are literal and carry an exact validated witness, existing home/working source fields remain omitted and action serialization remains unchanged. Existing inline and read/authorization projection contracts also passed.

Documentation validation passed for 11 changed Markdown files, 1,564 local links/fragments, balanced fences and 27 complete JSON examples. `git diff --check` passed. All owned build/test/EF processes reached terminal state before staging.

Exact final selections use the existing Infrastructure/API/Application test projects with `dotnet test <project> --filter '<selection>' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false`:

```text
Infrastructure: FullyQualifiedName~Tenant_resource_|FullyQualifiedName~Tenant_knowledge_|FullyQualifiedName~Tenant_Artifact_source_|FullyQualifiedName~Tenant_Artifact_sources_|FullyQualifiedName~Tenant_workspace_Artifact_|FullyQualifiedName~TenantResourceSourceContentTests|FullyQualifiedName~DefinitionResourceInfrastructureDiTests|FullyQualifiedName~WorkspaceArtifactSourceTests|FullyQualifiedName~Workspace_read_Review_|FullyQualifiedName~Tenant_Office_read_|FullyQualifiedName~Tenant_workspace_search_|FullyQualifiedName~PublicationKnowledgeRetrieveTests
API: FullyQualifiedName~Tenant_model_resource_Artifact|FullyQualifiedName~Tenant_knowledge_|FullyQualifiedName~Tenant_resource_namespace_|FullyQualifiedName~Tenant_resource_read_|FullyQualifiedName~Workspace_read_Review_model_HTTP_|FullyQualifiedName~Workspace_read_Review_pending_consent_|FullyQualifiedName~Tenant_workspace_search_Review_model_HTTP_|FullyQualifiedName~Tenant_workspace_search_pending_consent_|FullyQualifiedName~Tenant_model_workspace_Artifact
Application: FullyQualifiedName~TenantWorkspaceArtifactContractTests|FullyQualifiedName~TenantArtifactToolContractTests|FullyQualifiedName~TenantKnowledgeReadContractTests|FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests
```

## Limits

No installed-data migration/reset, physical process kill, Windows, production multi-instance, live provider, full-suite or hosted CI acceptance is claimed. Default checks use Synthetic adapters and disposable stores. This checkpoint does not establish full proposal or milestone acceptance.
