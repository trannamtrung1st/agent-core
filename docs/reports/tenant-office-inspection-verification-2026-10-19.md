# Tenant Office model inspection — 2026-10-19

This extends the conversational workspace-read conversion at `0cda238b` to the existing DOCX/XLSX inspect tools. The full multitenant proposal remains active. Office create/edit, source publication, virtual resources, autonomous origins and other resource/transport families still require conversion and final authority/schema cutover.

## Implemented boundary

`TenantWorkspaceReadContract` and its Office-specific closed parser bind exact owned home/working file paths and either DOCX offset/limit or XLSX sheet/range/opaque cursor. Unknown, duplicate and caller authority fields are refused; range and cursor cannot be mixed. Existing admitted/current capability projection, independent current parent/workspace IAM, HomeAccess, requester policy, live claim, semantic root refusal, consent visibility and saved-source/checkpoint gates remain authoritative. No legacy workspace dispatch is used.

`TenantWorkspaceRuntimeReadService` bounds metadata before allocating at most the existing 10 MiB Office package ceiling, privately fingerprints full actual source bytes and invokes the incumbent guarded Office processor and whole-element/cell budget formatter. Current owner/authority/claim fences repeat after processing. Results retain exact read witnesses and untrusted semantic output, with no calculation, file mutation, publication or recipient download authority. Safe cursor/package/limit errors disclose no source body.

Policy-required inspection uses the existing `PreparedWorkspaceRead`, approval decision, read-consent receipt, successor and checkpoint owners. The shared Organization/Chat inspector describes complete supplied Word excerpt or Excel selection scope through its existing saved-read fields; it shows no current document/cell body or private consent witness. `TenantWorkspaceOfficeReadReview` extends the existing SQLite check constraint to these two inspect tools, without a new table or effect engine; rollback refuses with retained Office consent.

## Executed local checks

The first actual model → saved consent → HTTP review/approval → successor → bounded Office observation passed **2/2**: real DOCX bytes with InMemory and XLSX bytes with SQLite (`/private/tmp/agent-core-office-model-first.log`). The initial EF generation exposed one wrong `IsOffice` argument type; it was corrected before qualification, then generation succeeded. The existing tool/runtime version advisory was retained; no environment tools were upgraded.

The expanded model/HTTP matrix passed **26/26** (`/private/tmp/agent-core-office-model-matrix.log`), including both store profiles, Allowed/Review, rejection, current Agent-read loss, stale source cancellation, real pending-consent SQLite host recreation, complete ranges, two-sheet sparse continuation, invalid/changed-source cursors, corrupt packages and the package byte ceiling. Tests inspect actual canonical native bytes, exact returned excerpts/values, private previews, no exceptional read grants, unchanged source bytes and zero provider HTTP attempts.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_Office_model_reads' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Closed-schema/canonical intent, admitted capability and shared authorization checks passed **45/45** (`/private/tmp/agent-core-office-contracts.log`). They include exact DOCX and opaque XLSX cursor consent round trips, changed-path correlation, unknown/duplicate/mixed argument refusal, no-tool/no-capability/wrong-Session/autonomous projection denial and existing read/search/authorization regressions.

```sh
dotnet test tests/AgentCore.Application.Tests --no-restore --filter 'FullyQualifiedName~TenantOfficeReadContractTests|FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests|FullyQualifiedName~TenantApprovalToolContractTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

Post-I/O authority, whole-element/cell output refusal, SQLite predecessor upgrade/prior-consent preservation/forged shape/reopen/guarded rollback and affected canonical text/list/search Review checks passed **31/31** (`/private/tmp/agent-core-office-boundaries-final.log`). The first boundary attempt passed five cases but rejected four XLSX seed fixtures at the old text fixture's 4 KiB ceiling. The fixture now explicitly requests the real Office package ceiling; default text fixtures and product limits were unchanged. Those initial failures are not passing evidence.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_Office_read|FullyQualifiedName~Tenant_Office_reads|FullyQualifiedName~Tenant_workspace_model_reads_and_lists|FullyQualifiedName~Workspace_read_Review_returns|FullyQualifiedName~Tenant_workspace_search_Review' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

## Browser and finish evidence

Playwright MCP exercised a disposable Synthetic Kestrel host through the task-owned Vite frontend on 5179. The initial previous-fixture browser cookie correctly expired; visible sign-in recovered. The current-journey monitor began after sign-in and initial detail readiness. It recorded no page errors, console warnings/errors or API HTTP failures; eight query requests were cancelled with `net::ERR_ABORTED` while navigating/closing details. Historical console entries were excluded from current acceptance.

The browser inspected the complete multilingual DOCX path and Word offset 1/limit 2, opened the original Chat and its same Review details inspector, exercised Keep reviewing with observed focus restoration, then confirmed Approve. The saved decision appeared before the separate result. Refresh displayed Read authorized in both the inspector and Chat; the fixture verified exact actual Office elements and unchanged native bytes.

The second real request exposed the full XLSX path, multilingual sheet `Quarterly results 試験 thử` and range A1:B2 without document/cell contents or private source hash. After the fixture replaced actual source bytes, Refresh disabled Approve/Reject and explained fresh-action recovery while retaining Cancel request. Browser cancellation and confirmation completed the successor with `readAuthorized:false` and null observation; current changed bytes remained untouched.

The browser-backed focused run finished **44/44**, terminal exit 0, duration 5m49s (`/private/tmp/agent-core-office-browser-proof.log`). It repeats the 26 Office cases and adds eight existing workspace read Review plus ten direct model-read regressions. A final no-sheet explicit-range case passed **1/1** (`/private/tmp/agent-core-office-first-worksheet.log`), confirming the preview says first worksheet and the result returns that worksheet. Across final runs there are **121 distinct selected cases**: 45 API, 45 Application and 31 Infrastructure; repeats and failed attempts are excluded.

```sh
AGENTCORE_OFFICE_READ_UI_PROOF=1 dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_Office_model_reads|FullyQualifiedName~Workspace_read_Review_model_HTTP|FullyQualifiedName~Tenant_workspace_model_HTTP_journey' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The final edge case used `dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_Office_model_reads&DisplayName~range-first' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'`.

Eighteen settled native captures cover 1440px desktop, 768/767px descriptions breakpoint, 390/320px mobile, exact scope supplements, shared Chat, confirmation, separate read results and stale/cancelled recovery. Each was opened and validated. Document overflow measured zero at all five pending widths; the Drawer stayed inside the viewport. Sparse/cursor and range-without-sheet backend functionality is tested, but those UI variants were not captured and do not inherit visual acceptance.

The fresh finish review returned **ship**, inspecting all eighteen captures and finding no material issues in the bounded Word excerpt/XLSX exact-range presentation. The single detector pass on the existing two shared TSX owners returned `[]`. No frontend source, CSS, token, kit or shipping asset changed; no additional frontend build or unit rerun was required for this data-projection extension. Fresh design comparison and retained assets are recorded below.

## Scope limits

No CI/full suite, live/paid provider, installed/user-data reset/migration, Real host restart or deployment was performed. All schema/host/file exercises use disposable fixture-owned data. This is bounded Office inspection qualification, not complete Office tools or full proposal/milestone acceptance.

## Retained handoffs and cleanup

The independent [finish review](assets/tenant-office-inspection-2026-10-19/finish-review.md) returned **ship** for all eighteen supplied captures. The fresh [documentation comparison](assets/tenant-office-inspection-2026-10-19/documentation-review.md) found no scoped normative contradiction or system change. DESIGN.md and design.json hashes remained unchanged; historical stale sidecar metadata/narrative drift is recorded without repair. The browser evidence placeholder observed by the documenter was finalized above after the complete test process reached terminal success. Follow-up reviews confirmed the first-worksheet clarification matches canonical behavior and leaves the captured Word/explicit-sheet XLSX presentation unchanged. No material correction was requested after the captures.

These native PNGs are verification evidence, not shipping product raster assets:

- Organization pending: [pending-1440](assets/tenant-office-inspection-2026-10-19/pending-1440.png), [pending-768](assets/tenant-office-inspection-2026-10-19/pending-768.png), [pending-767](assets/tenant-office-inspection-2026-10-19/pending-767.png), [pending-390](assets/tenant-office-inspection-2026-10-19/pending-390.png), [pending-320](assets/tenant-office-inspection-2026-10-19/pending-320.png), [scope-320](assets/tenant-office-inspection-2026-10-19/scope-320.png).
- Shared Chat and confirmation: [chat-pending-1440](assets/tenant-office-inspection-2026-10-19/chat-pending-1440.png), [chat-pending-320](assets/tenant-office-inspection-2026-10-19/chat-pending-320.png), [confirmation-320](assets/tenant-office-inspection-2026-10-19/confirmation-320.png).
- Separate read result: [result-1440](assets/tenant-office-inspection-2026-10-19/result-1440.png), [result-320](assets/tenant-office-inspection-2026-10-19/result-320.png), [chat-result-320](assets/tenant-office-inspection-2026-10-19/chat-result-320.png).
- Exact XLSX scope: [xlsx-1440](assets/tenant-office-inspection-2026-10-19/xlsx-1440.png), [xlsx-320](assets/tenant-office-inspection-2026-10-19/xlsx-320.png), [xlsx-scope-320](assets/tenant-office-inspection-2026-10-19/xlsx-scope-320.png).
- Changed source and cancellation: [stale-1440](assets/tenant-office-inspection-2026-10-19/stale-1440.png), [stale-320](assets/tenant-office-inspection-2026-10-19/stale-320.png), [cancelled-320](assets/tenant-office-inspection-2026-10-19/cancelled-320.png).

The single [detector result](assets/tenant-office-inspection-2026-10-19/detector.json) contains no findings. The proof was released, its exact dotnet session reached exit 0, and the task-owned Vite session exited 0 after Ctrl-C. The browser was navigated away. Neither task port 5179 nor 49284 remained listening. No user/Real host was stopped.
