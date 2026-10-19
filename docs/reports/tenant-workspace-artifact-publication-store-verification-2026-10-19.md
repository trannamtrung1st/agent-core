# Workspace source Artifact transaction verification — 2026-10-19

This checkpoint connects captured workspace sources to the incumbent **internal direct Artifact transaction under current Allowed authority**. It does not enable tenant model export or source approval. Exact source Review/grants, schema/shared inspector conversion, model/HTTP journeys, other proposal families and the final clean authority/schema cutover remain open. The full proposal remains active; no milestone acceptance is claimed.

## Implemented ownership and authority

`TenantArtifactOperationService.ExecuteWorkspaceAsync` uses the existing operation authorizer, Artifact transaction, actual Run marker, immutable receipt, security revision and audit owners. Its closed arguments accept only an exact owned absolute home/working path, sanitized output name and optional valid MIME. No caller identity, scope, source checksum, byte body or grant is accepted. The earlier [source-capture prerequisite](tenant-workspace-artifact-source-verification-2026-10-19.md) remains the native byte owner.

`TenantArtifactSourceTarget` repeats actual claimed human-message Run/Session/Agent ownership, admitted/current export capability, raw independent parent/workspace reads, current operation policy and HomeAccess before and after source I/O. Both stores independently repeat the relevant authority and source checks; a trusted caller cannot bypass them by supplying a capture directly. Current Instance overrides are applied to the store's published baseline before checking HomeAccess. ReadOnly home access permits copying out; Disabled blocks it.

The Application-only commit carries a frozen source capsule and private typed witness. Materialization checks owner, exact witness, complete copy length/checksum and native source currency. Its asynchronous validation performs filesystem work only while the incumbent InMemory locks or SQLite transaction are held; it must not reenter IAM/database owners. The stores recheck the live claim after materialization; SQLite also revalidates the native source after private byte staging. Known receipt replay does not read/publish the source again. Failed transactions can leave unreachable private staged bytes under existing cleanup semantics, without metadata or read visibility.

Output metadata/bytes belong to the actual executing Session/Run. Source workspace/Attachment provenance stays absent from tenant Artifact rows. Export preserves uninterpreted binary/Office bytes and uses the existing 50 MiB file/Artifact ceilings rather than the Office processor's 10 MiB processing ceiling. Publication grants neither Agent read authority nor recipient download access.

The optional `PreparedArtifactPublication.Source` witness is omitted when null. Prior inline action JSON and root fingerprints remain identical. Source exact actions bind current source facts, while semantic correlation binds the canonical source intent independently of refreshed bytes/witnesses. This checkpoint introduces no new table, migration, receipt ledger, provider or effect engine. Source consent is not persisted/offered through the current inline-only approval adapter/schema. The tenant legacy export route remains unavailable; there is no model fallback.

## Executed focused local checks

Commands from the repository root:

```sh
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~TenantWorkspaceArtifactContractTests|FullyQualifiedName~TenantArtifactToolContractTests|FullyQualifiedName~TenantApprovalToolContractTests' --nologo --no-restore
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Tenant_workspace_Artifact|FullyQualifiedName~Artifact_publication_' --nologo --no-restore
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~Exact_Artifact_Review_or_grant_commits_only_the_saved_bytes|FullyQualifiedName~Artifact_terminal_approval_cannot_be_bypassed' --nologo --no-build --no-restore
```

Final results: **42 Application**, **59 Infrastructure publication/store**, and **10 Infrastructure inline Review/grant/refusal** cases passed, each command terminal exit 0, with no skipped cases. Durations were 155 ms, 1 minute 14 seconds and 16 seconds respectively. Total: **111 distinct passing cases**; prior subset reruns are not counted again. The first two commands compiled their affected projects.

| Exercised behavior | Distinct cases | Expected and observed result |
| --- | ---: | --- |
| Exact DOCX publication and changed native source | 4 | Both stores save complete actual Office bytes/type/checksum with one Run receipt. Original-human readback matches; a source-only reader lacks output access. Replay after original source change returns the same factual receipt. A change before commit leaves no Artifact/receipt and retains concurrent source bytes. SQLite receipt replay also survives store recreation without a configured source materializer. |
| Raw source reads and policy at the store boundary | 12 | Missing Session, home Agent or workspace read is independently denied; each corresponding Always policy refuses direct publication even with raw read authority. Owner/requester authority is not borrowed. |
| Current home access after capture | 4 | The owning transaction rejects Disabled home access and accepts ReadOnly source copying, using current overrides independently of the admitted baseline. |
| Late expiry/cancellation, copy integrity and source staging | 7 | Both stores leave no Artifact/receipt on late claim loss or cancellation. A corrupted byte copy fails checksum checks. SQLite rejects a source changed after private byte staging before metadata commit. |
| I/O-time read revocation and opaque source size | 4 | Both stores withhold publication when executor read authority is revoked during capture. Complete opaque bytes above the Office processing ceiling publish without Office parsing; explicit MIME is retained. |
| Source capsule identity and model boundary | 6 | Both stores reject another actual owned Session's source capsule or a substituted witness. The normal tenant tool boundary still refuses the unconverted legacy workspace export route, leaving no Artifact. |
| Source-specific SQLite receipt-save fault | 1 | Faulted save rolls back Artifact metadata, actual Run revision, receipt, tenant security revision and publication audit. A later authorized retry commits one exact output. |
| Existing inline direct publication/storage | 21 | Existing authority, requester Review, race, lease/cancellation, rollback, read and quota cases continue to pass through the shared transaction. |
| Closed source contract and prior contract regression | 42 | 25 new source cases cover canonical metadata/exact paths, UTF-8/native path bounds, ambiguity, caller authority/evidence refusal, exact source action versus semantic intent and prior inline JSON/fingerprint compatibility. 17 existing Artifact/authorization contract cases also pass. |
| Existing inline durable consent | 10 | Both-store ordinary Review and exceptional grants apply only the saved bytes with one receipt and no read/role grant. Rejected, cancelled or expired operations remain suppressed after policy relaxation. |

The initial representative build failed on a missing contract namespace; it was corrected before the four representative use cases passed. A preliminary 48-case store selection passed. The expanded selection then passed 54 cases but four new fixture cases hit the canonical-layout reset guard because the fixture wrote owner files before initializing its disposable root. The fixture now initializes the canonical layout first; no production reset guard or installed data was changed. The final 59-case command, including the added source-specific rollback/reopen qualification, passes completely. Failed attempts are not passing evidence.

Documentation checks passed for all six changed Markdown files: 588 local links/fragments resolve, fences are balanced and five complete JSON examples parse. `git diff --check` passes. All owned build/test handles are terminal before commit; no application host was started.

## Limits and remaining work

This is application/native store integration, including original-human content service reads, not HTTP/browser/model source export acceptance. No full application-host restart, exact source approval/grant, source inbox visibility/count, shared source inspector, or model-to-download journey is claimed. The source capture and commit observations retain ordinary concurrent filesystem semantics; they are not a cross-process snapshot/lock. No frontend/design token change, live provider, CI, full suite, Docker or installed-data migration/reset was performed.

Contracts: [source interface](../04-backend-interfaces.md#tenant-workspace-artifact-source-boundary), [ownership](../03-system-architecture.md#workspace-source-evidence-for-artifact-publication), [transaction persistence](../15-persistence-and-configuration.md#workspace-source-publication-transaction-boundary), [focused testing](../16-testing-strategy.md#tenant-workspace-artifact-source-capture-checks).
