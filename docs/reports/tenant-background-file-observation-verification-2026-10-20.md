# Tenant immediate-background file observation — 2026-10-20

This checkpoint extends the existing tenant Runtime, exact read-consent and completion owners for immediate-background Workspace/Office/resource/knowledge reads and Artifact metadata. The full [IAM proposal](../../local/proposals/agent-core-multitenant-iam-approval-final-proposal.md) remains active. File mutation/publication, credentials and native Browser/Sandbox background adapters, creation Review, inbox/wait tools, autonomous origins, live-input arbitration, remaining transport/System/audit families and final authority/schema consolidation remain open. Positive bound-Attachment background qualification is separately pending as described below; no milestone or whole-proposal acceptance is claimed.

## Implemented boundaries

`TenantFileObservationContract` supplies only eligible execution shape and closed tool discovery. Actual saved lineage, current original-human communication authority on the stored root conversation, independent current executor Session/file/resource reads, admitted/current capability and HomeAccess, exact source/window witnesses and live claim/generation remain with the existing owning services and stores. Completion-report Runs cannot generate protected work. Background request_authorization exposes only Workspace/Office/knowledge read consent, bound Attachment read consent where capability permits and Artifact metadata verification. A forged mutation/publication intent is refused server-side even if it bypasses the model schema.

The existing durable approval request, independently qualified reviewer, same-root suppression, local read-consent receipt, immutable successor Run and retained-checkpoint revalidation are reused. Consent creates no mutation, exceptional grant or later read/download authority. A background successor retains BackgroundOutcome, original task, input ownership, Session and inherited root. Its signal and Core prompt retain background semantics, and it must finish with work.complete rather than producing conversational output or another child.

`TenantBackgroundLineage` follows exact persisted approval handoffs, retaining the initial child and validating each successor's owner, Session, task, response, source inputs and root. Only the terminal non-Continued task Run creates the existing completion receipt. The receipt and one factual parent report identify that terminal successor; the immutable Session origin and initial Run remain unchanged. No new table/migration or competing mutable owner is added. Existing original-human creation/communication and pending-parent-input fences still apply to report admission.

Background Activity resolves only this saved same-task chain. Each successor needs its own current human Run read before its facts can be shown; lacking a successor read withholds completion accounting instead of claiming Pending. The immutable `session.initialRunId` remains the original task identity, while `initialRun` is the latest independently readable Run in that task's approval chain. Later unrelated Chat turns never replace it. Parent target references still require independent parent read.

## Functional qualification

The representative use case uses actual authenticated HTTP, production tenant workers/Runtime and controlled Synthetic model gates against disposable InMemory and SQLite application hosts. The model calls background.start, the child reads its actual owned source, and work.complete produces one durable factual report in the original parent. The consent variant pauses durably, accepts an exact human decision, resumes through the same task's successor and reports only its terminal result. Activity retains original identity and shows the permitted successor result/delivery. No provider, public debug endpoint or user data/reset was used.

The hosted family matrix covers home/working files, bounded lists/search, admitted resource files/namespaces, exact knowledge identity, DOCX/XLSX inspection and Artifact metadata. Artifact setup uses the trusted storage port to seed bytes under an actual claimed child; this proves metadata observation, not model publication. Rejection/cancellation and changed-source/refused reads cannot substitute original or newer bytes. Repeated refused proposals retain the same root and one approval. Revocation after admission blocks the file read without borrowing the requesting Owner's role. Actual SQLite host recreation with consent pending retains exact stored source and task lineage without repeating child admission or consent.

The first representative run exposed fixture assertions about escaped JSON and a helper that already follows successor Runs; correcting those assertions revealed the real approval-prompt gate. Background successors previously retained BackgroundOutcome but lost Core background task semantics. Preserving the trigger/prompt fixed that gate. The larger matrix also corrected expected normalized Artifact display names and configured durable resource bytes in the disposable SQLite test host. Historical failed passes are not relabeled as green.

## Attachment boundary

A held live background Session can be explicitly foregrounded and receive a pending raw upload. Current canonical human-message admission still refuses binding a new file during that active execution with `session_in_use`. That live-input arbitration family was already open and is not bypassed here. The focused cases verify that the rejected message leaves the upload Pending, supplies no file body/name as model evidence and creates no false read-consent request. These cases stop at General Assistant v32's missing admitted Attachment capability; raw IAM cannot supply it. They do not independently qualify a capable bound-source read or the empty namespace path.

The converted bound-Attachment read/consent guards and shared foreground source regressions do not establish a positive public bound-Attachment background journey. That acceptance remains unverified until the owning live-input/source-admission path supports it. No fixture mutates a bound record or bypasses the Session mailbox to invent that workflow.

## Focused checks

The final selections cover **203 distinct cases**, all passed: 35 hosted module, 81 application, 77 infrastructure and ten existing API/wire cases. Preparatory runs and overlapping reruns are not counted again. CI and the full CI suite remain unrequested and unrun. No frontend presentation changed; hosted backend integration, rather than screenshots, is this checkpoint's functional evidence.

| Terminal log | Result |
| --- | --- |
| `/private/tmp/tenant-background-files-shipping.log` | 35 passed: Workspace/Office/resource/knowledge/Artifact observations, exact Review/refusal, same-root repeated refusal, current revocation, three actual SQLite host recreations, independent successor visibility and four pending-Attachment refusal cases. |
| `/private/tmp/tenant-background-files-application-shipping.log` | 81 passed: closed background schemas, admitted capabilities, Workspace/read-consent contracts and affected task/prompt semantics. |
| `/private/tmp/tenant-background-files-store-regression.log` | 37 passed: existing both-store background admission, current root authority, outcomes/receipts/report/recovery and Activity scope. |
| `/private/tmp/tenant-background-files-source-regression.log` | 40 passed: Workspace/Attachment actual sources, independent scope, changed bytes, I/O revocation and retained-observation revalidation. These include shared foreground read owners, not a positive bound-Attachment background journey. |
| `/private/tmp/tenant-background-files-api-regression.log` | Ten passed: existing actual model/background/refusal, SQLite child recovery, real MessagePack delivery/received-prefix and active/quiescent explicit continuation. |
| `/private/tmp/tenant-background-files-build2.log` | API build passed with zero warnings/errors; subsequent module/regression builds compiled the final source. |

Commands use `dotnet test <project> --no-restore --filter '<selection>' --nologo`; final module/application and existing API/source regressions also use `--no-build`. Exact selections:

```text
API module:
FullyQualifiedName~Tenant_background_file_|FullyQualifiedName~Tenant_background_pending_Attachments

Application:
FullyQualifiedName~TenantBackgroundFileObservationTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~TenantWorkspaceReadContractTests|FullyQualifiedName~TenantWorkspaceReadAuthorizationContractTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~PromptContextBuilderTests.Automation_history_is_evidence|FullyQualifiedName~PromptContextBuilderTests.Resumed_operation_outcome

Infrastructure background/Activity:
FullyQualifiedName~Tenant_background_|FullyQualifiedName~Tenant_activity_

Infrastructure shared sources:
FullyQualifiedName~Tenant_workspace_model_|FullyQualifiedName~Workspace_read_Review_rejects_changed|FullyQualifiedName~Tenant_attachment_model_read_

API/wire regression:
FullyQualifiedName~Tenant_background_model_|FullyQualifiedName~Tenant_background_completed_child_survives|FullyQualifiedName~Tenant_background_completion_crosses|FullyQualifiedName~Tenant_active_background_continues_through_its_mailbox|FullyQualifiedName~Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation
```

The first larger family pass had 27 passed/four failed (normalized Artifact names and the disposable resource-byte fixture). Its successor had 31 passed/four failed positive Attachment-binding attempts. Those attempts exposed the actual session_in_use boundary and were not relabeled as positive success: positive acceptance remains open, and four explicitly named refusal tests now cover the reachable behavior. The final assembled 35-case selection passed. The application selection also exposed a stale closed-list expectation omitting already converted background.start and knowledge.retrieve; the final 81-case selection includes the canonical tools and passed after that correction.

A sandboxed .NET build could not make progress through local IPC; its exact owned process was terminated before the normal local build. No test process was restarted on an observation timeout. All checks are terminal. Relative Markdown links and `git diff --check` passed. No user-data reset, Real-host restart, deployment or external provider call occurred.
