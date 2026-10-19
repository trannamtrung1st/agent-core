# Tenant workspace Artifact source capture verification — 2026-10-19

This is the source-storage prerequisite for the remaining tenant Workspace/Office publication conversion. It is not publication, full proposal completion or milestone acceptance. The existing inline publication and Office mutation checkpoints retain their own evidence; this change introduces no UI, model export, endpoint, approval variant, schema or alternate effect owner.

## Implemented boundary

The existing `FileAgentInstanceWorkspaceStore` implements `ITenantWorkspaceArtifactSourceStore`; DI resolves that facet from the same canonical adapter. Capture independently resolves the actual tenant Agent and, for working files, its actual Session/executor relationship before and after native I/O. Only exact concrete owned home/working file paths are accepted. Reads do not create owner directories.

Capture reads complete raw bytes within the smaller configured file bound, workspace ceiling and Artifact ceiling (both existing global ceilings are 50 MiB). It does not apply the Office processor's 10 MiB processing bound or interpret/export package contents. It returns a source-owned capsule with frozen copy access and logical owner/path, inferred MIME, exact size/checksum and a private observation hash. Caller mutation of a returned byte copy cannot alter the capsule.

The canonical native filesystem binds the ordinary file's descriptor/stat identity, size, permissions, owner, timestamps and streamed SHA-256. Source evidence also binds owner-root/parent identity and access metadata, resolved logical route, and traversed link identities/targets. A fresh root/parent check refuses detached old paths after directory substitution. Unrelated sibling changes do not invalidate the source solely through directory timestamps. External symlinks and hard-link aliases remain refused under the incumbent canonical rules. Missing/changed/unsafe sources at repeat validation return `workspace_source_changed`; cancellation is preserved.

`RequireCurrentAsync` performs only native filesystem validation. It must not reenter IAM/database owners while a future publication transaction holds their locks. This evidence grants no permission, approval, live Run claim or read access; the Application/publication owner must independently repeat current IAM, actual owner, capability, HomeAccess, exact consent, claim/expiry and recipient checks. Native fences are observations under ordinary concurrent filesystem semantics, not a cross-process snapshot or lock. Existing Artifact session-total quotas and atomic receipt/grant owners still belong to publication.

## Executed local verification

Command from the repository root:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~WorkspaceArtifactSourceTests|FullyQualifiedName~Tenant_Artifact_source' --nologo --no-restore
```

Final result: **31 passed, 0 failed, 0 skipped**, terminal exit 0; test duration 20 seconds. The command compiled the affected backend and test projects. These are 31 distinct cases; earlier reruns are not counted again.

| Actual exercised boundary | Distinct cases | Expected and observed outcome |
| --- | ---: | --- |
| Memory/SQLite × home/working binary capture | 4 | Complete non-UTF-8 bytes, exact size/checksum, independent copies; same-size timestamp-restored replacement fails current validation; original frozen copy stays intact. Artifact collection stays empty and Run revision/status/claim stay unchanged. |
| Memory/SQLite × home/working × DOCX/XLSX | 8 | Incumbent Office processor produces actual packages; capture preserves every byte and expected MIME without text conversion or interpretation. |
| Memory/SQLite actual owner/path refusal | 2 | Same-Account cross-tenant Agent IDs, mismatched Session/Agent, empty identities, relative/foreign/root/glob paths and missing files return no captured source; owner roots remain absent. |
| Memory/SQLite configured/global limits and repeat validation | 2 | Narrow configured bound and a sparse file above 50 MiB are rejected even with a wider configured bound; deleted source returns the safe conflict code and cancelled validation preserves cancellation. |
| Native filesystem source cases | 15 | Empty/missing/directory/foreign paths; exact 50 MiB capture plus bounded hash-only validation; oversized/invalid bounds; internal link identity/route changes, external links; stat-to-open file/link substitution; parent/root replacement; internal versus external hard-link ownership; sibling stability and cancellation all match their asserted outcomes. |

The first representative four-case attempt failed only because the test compared the safe reason code to the exception's generic display message; after correcting the assertion, all four passed. The first expanded command had a test compile error from an empty collection supplied to `ReadOnlyMemory<byte>`; no tests ran. Its corrected run passed 29 cases but failed two SQLite assertions that used shallow whole-record equality on deserialized Runs. The final assertions check durable Run revision/status/claim, and all 31 cases pass. These failed attempts are not reported as passing evidence.

Documentation verification checked the five changed Markdown files: 499 local links/fragments resolve and fences are balanced; they contain no complete JSON example fences. `git diff --check` passes. All build/test handles are terminal before commit; no application host was started for this storage-only change.

## Remaining qualification

The tenant model still does not publish Workspace/Office source bytes through `artifacts.create_from_workspace`. Direct and exact Review publication must be wired through the incumbent Artifact transaction, root correlation, Run/checkpoint, consent and primary grant owners, with independent source read policy and current source checks. No approval/transaction/receipt rollback, source-publication host recreation, browser export journey or recipient download is claimed here. No frontend files or design tokens changed. No live provider, installed-data migration/reset, Docker, CI or full suite was run for this prerequisite.

Contracts: [source boundary](../04-backend-interfaces.md#tenant-workspace-artifact-source-boundary), [source ownership](../03-system-architecture.md#workspace-source-evidence-for-artifact-publication). The [Office mutation report](tenant-office-mutation-verification-2026-10-19.md) remains the separate current conversational Office-write qualification.
