# Workspace source Artifact consent backend verification — 2026-10-19

This checkpoint implements **exact backend source consent** through the existing Artifact publication, approval, root correlation, successor Run, primary grant, immutable receipt and atomic local effect owners. It extends the earlier [captured source](tenant-workspace-artifact-source-verification-2026-10-19.md) and [internal direct transaction](tenant-workspace-artifact-publication-store-verification-2026-10-19.md) checkpoints. Model schema/direct export offering, the shared source inspector, whole product journeys and full application-host recreation remain pending. The complete proposal remains active; this is not milestone acceptance.

## Implemented behavior

`PrepareWorkspaceArtifactAsync` captures the exact owned native source and combines current source-read policies with the primary `artifacts.create` decision on the actual parent Session. `publish_workspace_artifact` parses only exact `artifact={path,displayName,contentType?}` intent and matches the source publication variant; caller identities, scopes, bytes, source hashes and grants are refused. Preparation/checkpoint routing uses the incumbent authorization adapter. The intent is not yet advertised in the model schema, and the direct legacy workspace export effect remains unavailable.

Raw executor Session/workspace reads and home Agent read where applicable remain mandatory for ordinary Review and exceptional creation grants. Current requester policy is evaluated on each source scope. Qualified humans require independent source reads, underlying creation authority and primary approval/grant authority, plus approval/independence on source scopes whose current policy requires Review. Human detail reads and inbox counts/paging filter source access before projection. Working source inspection does not borrow or require a home Agent read.

Pending inspection, decisions and approved execution repeat saved source/configuration/ownership checks. Inspection returns exact saved path, output name/type, byte size/checksum and publication disclosure as plain data; no source body, physical details or private native observation hash is projected in the review. Changed sources disable decisions while authorized cancellation remains available. An approved operation whose source or authority changed records a denied factual result and revokes an exceptional grant, without adapting arguments or replaying a restored source.

Actual pending authorization checkpoints create an incumbent successor Run that inherits the trusted root, owner, Session and exact operation hash. Only that actual executing Run owns the published Artifact and receipt. Native source validation remains filesystem-only inside store locks/transactions. Materialization rechecks current source, frozen copy integrity, claim, expiry and cancellation; SQLite also checks after private byte staging. Output metadata/Run/receipt/grant/security/audit commit atomically. Failed transactions can leave unreachable private bytes under existing semantics; they confer no read visibility.

Migration `20261019053112_TenantArtifactWorkspaceSourceReview` extends the current approval CHECK, preserving prior inline rows and hashes. It binds the source to the actual parent Session, typed home/working path, bounded arguments and source facts, with no mixed operation or inline byte content. Application rebuild additionally enforces closed canonical intent/hash. Downgrade refuses retained source consent. No new table, effect engine, receipt ledger, role or serialized byte/callback checkpoint is introduced.

## Executed local checks

Commands from the repository root:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_Artifact_source_consent_' --nologo --no-restore
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~TenantWorkspaceArtifactContractTests|FullyQualifiedName~TenantArtifactToolContractTests|FullyQualifiedName~TenantApprovalToolContractTests' --nologo --no-restore
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Artifact_publication_|FullyQualifiedName~Exact_Artifact_Review|FullyQualifiedName~Artifact_terminal_approval|FullyQualifiedName~Artifact_approval_|FullyQualifiedName~Tenant_workspace_Artifact' --nologo --no-build --no-restore
dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj --startup-project src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj --no-build
```

Final outcomes: **56 source-consent Infrastructure**, **43 Application contract**, and **78 existing Artifact transaction/consent regression** cases passed, each command terminal exit 0 with no skips. Durations: 1 minute 28 seconds, 144 ms and 1 minute 36 seconds respectively. Total: **177 distinct passing cases**, not counting subset reruns again. The first two commands compiled their affected projects. EF reports no changes since the last migration. Its existing 10.0.5 tool versus 10.0.12 runtime advisory remains; the tool was not upgraded. Migration generation used the isolated design-time context; tests apply migrations only to disposable fixture databases.

| Source-consent behavior exercised in memory and SQLite | Distinct cases | Expected and observed outcome |
| --- | ---: | --- |
| Exact Office package Review and primary creation grant | 4 | Complete real DOCX bytes/type/checksum, one receipt, no output read/role grant; exceptional grant consumes once and factual replay returns the same result. |
| Missing raw source reads | 6 | Missing Session, home Agent or workspace read prevents preparation even when a creation exception could otherwise be requested; no approval or Artifact is created. |
| Compound policies | 6 | Each source-read Always policy requires source Review despite primary creation being Allowed. |
| Human visibility/counts and scoped approval | 4 | Home reads require both workspace and Agent access before count/paging; Session approval does not substitute for required home workspace approval. Working inspection requires its independent Session/workspace reads and permits a reader without home Agent read. |
| Independent source reviewers | 2 | Source independence applies even with primary policy Allowed; the original requester cannot approve while a qualified other human can. |
| Changed source before decision or effect | 8 | Details disable decisions and retain cancellation; POST approval conflicts on the changed source. Approved changes yield denied receipts, revoke grants and never replay after original bytes are restored. |
| Executor/grantor revocation | 4 | Current source read loss or approving human authority loss withholds output, records a factual denial and revokes the primary grant. |
| Actual pending checkpoint successor | 4 | Typed authorization parsing/preparation, durable wait/decision, real continuation lineage, actual successor-owned output/receipt and closed tool result; no second signal/effect replay. |
| Root refusal correlation | 6 | Rejected, cancelled and expired source intents stay suppressed after source bytes change and policy relaxes, through both direct publication and renewed preparation. |
| SQLite upgrade/reopen/shape/downgrade | 1 | Prior inline consent survives downgrade/upgrade; exact source consent reopens. Nine forged Session/path/hash/size/mixed/content/null-type/resource/scalar mutations fail CHECK. Retained source consent blocks lossy downgrade and remains intact. |
| Source-specific receipt-save rollback | 1 | Metadata, actual Run, receipt, grant consume, security revision and publication audit all roll back on save fault; an authorized retry commits exactly the saved bytes once. |
| Late materialization boundaries | 6 | Claim expiry/cancellation leaves no receipt/output or grant consumption; expiry under a live longer claim produces `grant_expired`, no output and a revoked grant. |
| I/O-time preparation revocation | 2 | Losing raw reads during native capture prevents approval creation. |
| Original requester policy | 2 | Source policy WhenRequesterLacksPermission uses the actual low-privilege requester, requiring Review even when creation is independently Allowed. |

The 43 Application cases cover the new closed source intent plus prior source/inline canonical arguments, exact action versus semantic intent and existing authorization compatibility. The 78 Infrastructure regressions cover incumbent direct source and inline publication, raw authority, quotas, race/claim/cancellation, existing inline Review/grants/refusal/visibility, SQLite shape/reopen and receipt rollback.

Failed attempts remain distinct from passing evidence. The first representative test build used the wrong review-return type/property and lacked a command namespace; the corrected in-memory flow passed before parity/coverage expansion. A later test compile used a nonexistent relaxed-policy enum and was corrected. The first 44-case persistence selection passed 43 but its upgrade fixture correctly failed capability validation by preparing inline content with General Assistant. The fixture now uses Customer Support for prior inline consent and a separate capability-qualified General Assistant for source consent; its focused upgrade check and final full 56-case source selection pass. Production capability guards were retained.

Documentation verification passed for all seven changed Markdown files: 671 local links/fragments resolve, fences are balanced and 26 complete JSON examples parse. `git diff --check` passes. All owned test/build/EF handles are terminal before commit.

## Remaining scope

These checks execute Application/native stores and actual durable authorization successor mechanics. They do not establish advertised model source export, HTTP model-to-download flows, the shared browser inspector, full application-host recreation or visual acceptance. No frontend/design token changed, application host started, live provider/CI/full suite/Docker run, or installed-data migration/reset was performed. Native fences retain ordinary concurrent filesystem semantics rather than a cross-process snapshot/lock. Model offering, shared UI and full product qualification remain the next source-publication work; other proposal families and final authority/schema cutover remain open.

Canonical owners: [source interface](../04-backend-interfaces.md#tenant-workspace-artifact-source-boundary), [architecture](../03-system-architecture.md#workspace-source-evidence-for-artifact-publication), [persistence](../15-persistence-and-configuration.md#workspace-source-publication-transaction-boundary), [protocol](../14-api-and-realtime-protocol.md#workspace-source-publication-consent-backend), [testing](../16-testing-strategy.md#tenant-workspace-artifact-source-capture-checks).
