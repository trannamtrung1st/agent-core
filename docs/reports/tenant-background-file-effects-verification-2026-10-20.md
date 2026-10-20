# Tenant background file effects verification — 2026-10-20

This successor qualifies protected file mutations and publications in human-attributed immediate-background tasks and their same-task approval successors. It supersedes the background mutation/publication gap in the earlier [read-consent](tenant-background-file-observation-verification-2026-10-20.md) and [live-input](tenant-live-input-queue-verification-2026-10-20.md) reports only for this file family. The full multi-tenant/IAM/approval proposal and original goal remain active. Native Browser/Sandbox effects, Credentials/settings, child fanout, creation Review, inbox/wait tools, autonomous origins, Interrupt/Steer/voice arbitration, remaining UI/System/audit families and final authority/schema cutover remain open. No milestone or full proposal acceptance is claimed.

## Implementation

`TenantFileExecutionContract` replaces the observation-only shape/discovery helper. It admits conversational human tasks, immediate-background tasks and their exact approval successors, while completion reports and unconverted origins remain closed. Background authorization uses a file-only schema. Actual saved lineage, current admitted/configured capability, state isolation, independent IAM, source witnesses, claims and operation policies remain with the existing file/approval/transaction owners. Raw roles cannot add an absent definition capability. Explicit inline publishers require the ordinary selected capability and role-tool projection; the builtin General Assistant is unchanged.

Workspace and Office mutations use the incumbent external-effect fence and factual receipt. Artifact inline publication and exact Workspace/Office/Resource/bound-Attachment export use the incumbent atomic Artifact/Run/receipt/security/audit transaction. Every successive operation requires its own decision. One-time grants substitute only for the exact primary permission, with source reads and secondary batch operations independently required. Core policy floors still force Review for destructive batches and requester-lacks-primary cases. `applied: false` is a decision/refusal, never proof of an effect. A lost acknowledgement leaves one physical dispatch and an uncertain outcome; it cannot trigger replay or dependent publication.

`TenantExecutionLineage.RequireRequesterAsync` resolves the actual saved root and repeats the original human's current root-Session read and Agent chat. The facade and both stores' direct/approved transaction owners use this check. The requester does not need or inherit private child read access; the Agent and reviewer still need their own current actual-child/source authority. Factual settlement and recovery retain existing permission-independent evidence recording.

Artifact request preparation now uses the immutable context root rather than the current successor activation ID. Multiple approvals therefore preserve the same task, Session, original requester, root and BackgroundOutcome. Only the terminal non-Continued successor reports once to the original parent. Files belong to the actual child Session and producing successor Run. Reporting, creation and receipts grant no recipient read/download.

No table, migration, second effect executor, queue, permission snapshot or additional approval authority was introduced. Frontend source was unchanged; this report makes no new browser interaction or presentation claim.

## Exercised workflows

| Scenario | Expected and observed outcome |
| --- | --- |
| Actual background model performs a Workspace mutation then exports its exact saved file | Allowed and separately reviewed operations complete through the existing owners. Export bytes equal the actual saved source; the terminal successor alone reports. |
| Review and creation-only exceptional grant for two successive operations | Each exact request has its own decision/receipt. The later publication preserves the original root and task; no blanket grant follows the mutation. |
| Patch, ordered mkdir/copy/move/delete batch, DOCX, XLSX | Actual owned file operations and semantic Office inspection succeed. Export preserves exact saved package bytes. Direct destructive batch refuses under the existing minimum, while exact Review succeeds. |
| Inline UTF-8 publication, admitted Resource and live-bound Attachment export | Only explicitly capable definitions offer inline publication. Export copies the authorized exact source. Pending/missing capability remains outside that positive path. |
| Reject/repeat, missing primary permission, changed source, revoked source read and disabled HomeAccess after admission | Refusal produces no dependent publication. Repeated rejection keeps canonical root suppression; changed/revoked pending actions cannot be approved and can be cancelled. |
| Acknowledgement lost after an actual Workspace write | Exactly one native write occurs. The task records uncertainty and does not replay or export an assumed result. |
| Original human has root conversation read/chat and independent primary intent rights, without private child read | Background effects and Review can complete; actual HTTP child conversation/Artifact reads remain denied. |
| Original root read revoked before direct store call with a fresh security witness | Both stores refuse Workspace dispatch/Artifact publication without partial Run, Artifact, security or audit changes. Restoring only root read admits the otherwise valid operation; private child read stays absent. |
| SQLite application host recreated while second Review is pending | Saved mutation receipt and exact publication source survive for text, DOCX and Resource cases. The mutation is not repeated; only the final task successor reports. |

All API cases use the actual Synthetic tenant host, model tool loop, HTTP/file/approval surfaces and disposable Memory/SQLite stores. Outbound provider HTTP remains zero. Office packages are generated and inspected by the incumbent processor; desktop Office-client fidelity is outside this report.

## Checks and corrections

Commands use `dotnet test <project> --no-restore --filter '<selection>' --logger 'console;verbosity=minimal'`; the existing API regression additionally uses `--no-build`. Local .NET IPC/loopback execution required sandbox escalation. One sandboxed command failed before tests with MSBuild pipe permission denied; that failed process was stopped and the same family selection ran with the required local access. No hosted CI, full CI suite, Real host, live provider, installed-data migration/reset or deployment was requested or used.

| Selection / local log | Result |
| --- | --- |
| Solution build; `/private/tmp/tenant-background-file-effects-build-final.log` | Passed, 0 warnings/errors. |
| Application file/schema/capability contracts; `/private/tmp/tenant-background-file-effects-contracts2.log` | 137 passed. |
| Application durability, persistence receipts and completion; `/private/tmp/tenant-background-file-effects-runtime-regression.log` | 66 passed. |
| New actual-root store-boundary cases; `/private/tmp/tenant-background-file-effects-root-store4.log` | 4 passed. |
| New host/file-effect journeys; `/private/tmp/tenant-background-file-effects-module2.log`, `/private/tmp/tenant-background-file-effects-final-corrective.log` | 42 distinct passing cases across grouped and corrective selections: 38 grouped successes plus inline publication and current-HomeAccess cases. |
| Exact family-source-byte assertions; `/private/tmp/tenant-background-file-effects-family-bytes2.log` | 13 passed; overlaps the family cases above, with added exact-byte assertions. |
| Shared authorization class; `/private/tmp/tenant-background-file-effects-authority-regression.log`, `/private/tmp/tenant-background-file-effects-fixture-corrections.log` | 875 distinct cases passed across final selections: 856 grouped successes plus the 19 failed fixtures covered by the 21-case corrective selection. The original grouped process ended with those 19 fixture failures; no full-class rerun is claimed. |
| Corrected queue-order/schema fixtures; `/private/tmp/tenant-background-file-effects-fixture-corrections.log` | 21 passed. |
| Existing background read/Review/Continue/report/MessagePack regression; `/private/tmp/tenant-background-file-effects-api-regression.log`, `/private/tmp/tenant-background-file-effects-api-corrective.log` | 47 distinct cases passed across final selections: 37 grouped successes plus 10 corrected base-model cases. |

Initial module checks exposed incorrect fixture assumptions about policy floors, missing declared inline capability/projection, fresh security witnesses and requester creation authority. Fixtures now retain the actual minimum policy and separately supply raw authority where required. A contract case exposed unsupported-origin authorization offering, corrected by the shared eligible-task guard. The first positive two-approval journey exposed the current-activation versus immutable-root bug described above.

The existing API regression found that its baseline background model still asserted mutation offering was forbidden. Its assertions now preserve native/configuration/fanout closure while accepting the converted file family. The affected base-model journeys are rerun separately from the passing file-read cases.

The shared authorization regression also exposed older fixtures that submitted a second same-Session input and attempted to claim it ahead of the queued setup Run. Those fixtures now explicitly finish the setup execution before claiming the later input; FIFO product enforcement is retained. A schema test intentionally attempts a forbidden downgrade; later migrations can roll back before that guard refuses. It now restores the current schema before testing reopen/recovery. Corrective checks rerun affected fixtures without repeating the already-passing class cases.

**1,171 distinct local cases have passing evidence across the final selections.** The 13 family-byte checks, 21 corrected authorization fixtures and 10 corrected API fixtures overlap the parent selections and are not counted twice. All selected processes are terminal. Solution build has no warnings/errors; `git diff --check`, balanced Markdown fences and 1,477 relative file links pass. Two stale inventory links now point to the already-converted System Browser privacy owner.

Exact selections:

```text
Application contracts:
FullyQualifiedName~TenantBackgroundFileObservationTests|FullyQualifiedName~TenantWorkspaceMutation|FullyQualifiedName~TenantOfficeMutation|FullyQualifiedName~TenantArtifactToolContractTests|FullyQualifiedName~TenantWorkspaceArtifactContractTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~CapabilityProjectionTests

Application runtime:
FullyQualifiedName~AgentRunDurabilityTests|FullyQualifiedName~PersistenceReceiptTests|FullyQualifiedName~SessionRuntimeCompletionTests

New root-store cases:
FullyQualifiedName~Background_file_effect_transactions_

New API cases:
FullyQualifiedName~Tenant_background_file_effect|FullyQualifiedName~Tenant_background_inline_publication|FullyQualifiedName~Tenant_background_file_current_HomeAccess

Exact family bytes:
FullyQualifiedName~Tenant_background_file_effect_families_

Shared authorization:
FullyQualifiedName~TenantOperationAuthorizationTests

Corrected fixtures:
FullyQualifiedName~Artifact_reference_preflight_requires_actual_source|FullyQualifiedName~Tenant_Attachment_export_|FullyQualifiedName~Tenant_workspace_effects_SQLite_shape

API corrected base-model fixtures:
FullyQualifiedName~Tenant_background_model_starts_|FullyQualifiedName~Tenant_background_completed_child_survives|FullyQualifiedName~Tenant_background_completion_crosses|FullyQualifiedName~Tenant_active_background_continues|FullyQualifiedName~Tenant_rejected_foreground|FullyQualifiedName~Tenant_foreground_visibility

Existing API regression:
FullyQualifiedName~Tenant_background_file_read_|FullyQualifiedName~Tenant_background_file_families_|FullyQualifiedName~Tenant_background_file_refusal_|FullyQualifiedName~Tenant_background_file_consent_|FullyQualifiedName~Tenant_background_pending_Attachments|FullyQualifiedName~Tenant_background_model_|FullyQualifiedName~Tenant_background_completed_child_survives|FullyQualifiedName~Tenant_background_completion_crosses|FullyQualifiedName~Tenant_active_background_continues|FullyQualifiedName~Tenant_rejected_foreground|FullyQualifiedName~Tenant_foreground_visibility
```

This checkpoint is backend file-effect qualification. It does not establish final schema consolidation, all autonomous/native adapters, full UI/runtime acceptance or completion of the original proposal. Historical reports keep their original observations and are superseded only at the explicit boundaries above.
