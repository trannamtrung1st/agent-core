# Tenant exact resource read and consent verification — 2026-10-19

## Scope

This successor to `319d10cf` connects the verified pinned resource source to the incumbent workspace reader, Office processor, durable Review, successor Run, consent receipt and checkpoint owners. It enables only exact admitted resource leaves for `workspace.read`, `office.document.inspect` and `office.spreadsheet.inspect`. It does not complete the full tenant IAM proposal.

Resource observations use primary `resources.read` on the actual Instance-owned child, with independent executor `sessions.read` and `agents.read`. Admitted and current capability, current resource binding/revision and inherited Definition/version remain required. HomeAccess and workspace grants are not substitutes for resource authority. No caller-supplied owner, child key or digest selects content. The source target reuses the existing immutable byte store and native no-follow safeguards.

The existing `PreparedWorkspaceRead` payload now has optional `ResourceKey` and `ResourceRevision` fields. Canonical arguments and the verified source observation bind the action hash. The payload contains no resource body, callback or authority snapshot. Resource consent remains Review-only: both executor and human reviewer retain independent current raw reads; no exceptional read grant is created. Current child ownership and compound reads filter inbox counts before paging, including removed bindings. Semantic root suppression covers rejected/expired/cancelled reads after policy relaxation. The existing local consent transaction and immutable receipt authorize only the saved continuation observation.

Text is bounded strict UTF-8 untrusted data. Office reads use the incumbent bounded package processor and pagination without mutation or publication. Returned resource observations carry source metadata and a fingerprint; checkpoint reuse repeats current source/authority checks. Existing home/working observation hashes and witness shapes are preserved.

## Executed checks

All final owning processes reached exit 0: **207 distinct focused passes, zero skips**. The eight-case follow-up repeats two existing negative-path cases, so those are counted once; the five resource HTTP cases are included in the 40-case API selection. Exploratory failures and earlier subset reruns are not added again.

| Check | Selection | Result |
| --- | --- | --- |
| Source/read/store regression | Infrastructure: resource/source/native/DI, workspace Review/search and Office reads | 136 passed, 3m3s |
| Inherited and literal-path follow-up | Infrastructure: inherited consent, literal/long Unicode filenames, malformed/unadmitted paths | 8 passed, 14s; 6 new distinct cases |
| Actual model/HTTP regression | API: resource consent plus incumbent workspace read/search and workspace Artifact source flows | 40 passed, 3m7s; includes 5 resource flows |
| Read contract/projection regression | Application: tenant workspace read and read-authorization contracts | 25 passed, 108ms |

Projects are `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`, `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` and `tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj`. Each uses `dotnet test <project> --filter '<selection>' --nologo --no-restore -m:1 -nr:false -p:UseSharedCompilation=false` with these exact selections:

```text
Infrastructure: FullyQualifiedName~Tenant_resource_|FullyQualifiedName~TenantResourceSourceContentTests|FullyQualifiedName~DefinitionResourceInfrastructureDiTests|FullyQualifiedName~Workspace_read_Review_|FullyQualifiedName~Tenant_Office_read_|FullyQualifiedName~Tenant_workspace_search_
Follow-up: FullyQualifiedName~Tenant_resource_literal_|FullyQualifiedName~Tenant_resource_inherited_read_Review_|FullyQualifiedName~Tenant_resource_source_rejects_an_unadmitted_path_
API: FullyQualifiedName~Tenant_resource_read_|FullyQualifiedName~Workspace_read_Review_model_HTTP_|FullyQualifiedName~Workspace_read_Review_pending_consent_|FullyQualifiedName~Tenant_workspace_search_Review_model_HTTP_|FullyQualifiedName~Tenant_workspace_search_pending_consent_|FullyQualifiedName~Tenant_model_workspace_Artifact
Application: FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests
``` The single-node/no-server form avoids a reproduced local MSBuild IPC wait. The initial owned stalled build was sampled, explicitly cancelled and confirmed terminal before the fallback; it is not a passing compilation result. The infrastructure fallback build passed with zero warnings/errors. EF generated `20261019071133_TenantResourceReadReview` without applying it to an installed database; the available EF tool version emitted a version warning, so actual disposable migration/model checks establish the result.

The five resource API cases were rechecked on the final literal/Unicode source guard: all five passed in 29s; they remain a subset of the 40 API cases, not additional distinct passes. Documentation validation passed for 11 changed Markdown files, 1,540 local links/fragments, balanced fences and 26 complete JSON examples. `git diff --check` passed. Every final build/test/EF process was confirmed terminal before staging.

## Exercised behavior

- Created a resource through authenticated tenant configuration, admitted and claimed its actual Run, and read it through `SessionToolExecutor` without a legacy filesystem fallback or workspace grant.
- Required Review separately on Session, Agent and Resource reads. Both stores saved one exact resource observation, freed the waiting Run claim, exposed a body-free path/page review, accepted a qualified decision, dispatched one successor and returned exact UTF-8 content through its saved authorization checkpoint. Duplicate resume preserved the checkpoint/revision and no operation grant appeared. Revocation prevented retained observation reuse.
- Exercised DOCX/XLSX source consent through the existing processor and successor. Source and native qualification also cover current inherited/Instance origin, wrong owner/claim, missing independent reads, source-I/O changes, late revocation/expiry, cancellation, immutable length/hash, symbolic links, hard links and allocation bounds.
- Filtered approvals for a human with parent reads and no resource read before counts; an exact resource-child role revealed only the qualified request, and revocation hid it again. Removing the binding also hid it from tenant-wide Owner counts. Changed or disabled bindings prevented a saved decision.
- Rejected a resource observation, relaxed its policy, and attempted the same operation in the successor root. The semantic refusal remains authoritative; a changed policy is not fresh consent. Direct retained observations repeat both policy and binding checks.
- SQLite rejected forged resource keys, working-root flags, collection tools, negative revisions, mixed payloads, substituted scope/path and exceptional grants. Reopen retained the exact request; narrowing the CHECK with retained resource consent failed without losing the row.
- Actual Synthetic model/HTTP flows created an Instance resource, admitted its Run, inspected the body-free approval from Organization and conversation endpoints, approved/rejected through HTTP and observed exact authorized content or no body in the successor. Pending consent survived orderly application-host disposal/recreation with native durable resource bytes and SQLite metadata. No outbound provider attempts occurred.

## Findings corrected during implementation

The new resource variant initially missed SQLite's workspace-specific semantic query; the refusal test caught the discrepancy with memory. Lookup now uses the operation's actual permission/type and owning Instance while preserving canonical Session/tool/arguments correlation. Current child catalog filtering also closes removed-binding count discrepancies. The final source guard matches admitted names literally, including pattern characters and long Unicode components accepted by configuration; it never expands a wildcard. Host filesystem segment limits apply to the physical digest path rather than the virtual display name. Native byte-store guards remain unchanged.

The shared SQLite API fixture replaced metadata persistence while leaving resource bytes transient. The recreated host correctly refused absent content. The fixture now explicitly uses the same file-backed content/SQLite resource adapters as production under its disposable resource root. An initial HTTP fixture used an unsupported string enum; it now follows the existing numeric enum contract. A refusal fixture omitted its saved authorization call and was corrected to exercise a real successor. These exploratory failures are excluded from accepted counts.

## Limits and remaining proposal work

This change adds no frontend component, styling, theme or new review composition. HTTP evidence verifies the shared approval data contract; a fresh resource-specific browser presentation/interaction pass was not run. Existing presentation evidence remains scoped to its original report/commit. Native source safety is qualified on the current supported Unix host, not Windows or production multi-instance infrastructure.

Resource namespace listing/search, other virtual resource families and publication adapters, autonomous origins/transport families, final legacy owner/approval authority removal and canonical schema cutover remain open. No live provider, installed-data reset, physical application process-kill recovery, full suite or hosted CI was run. The full goal remains active.
