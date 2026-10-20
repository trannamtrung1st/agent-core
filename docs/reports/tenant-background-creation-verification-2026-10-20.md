# Tenant background creation consent verification — 2026-10-20

This successor qualifies exact immediate-background creation consent for human conversational tasks and their conversational approval successors. It supersedes child-creation-Review-pending notes in the [background runtime](tenant-background-runtime-verification-2026-10-20.md) and [file effects](tenant-background-file-effects-verification-2026-10-20.md) checkpoints at that boundary only. The full proposal and original goal remain active. Report-Run creation Review, native background effects, inbox/wait tools, autonomous origins, Interrupt/Steer/voice arbitration, remaining System/audit families and final authority/schema consolidation remain open. No milestone or full proposal acceptance is claimed.

## Implementation

The existing authorization tool accepts the closed `start_background_work` task intent. Core freezes actual parent ownership, objective/title/report preference, child Session/Entry/Activation/Run/Response identities, model and effective configuration witness. The model supplies no authority, child identity or grant. Background children and completion reports cannot fan out. A conversational approval successor can create a child under the original human/root; the child points to the actual executing successor. Root budgets include successive approval activations.

`sessions.create` and `agent-runs.create` are independent requirements at actual Tenant scope. Core selects one missing permission as primary when the other is held; a one-time grant replaces only the primary permission. Missing both refuses. Reviewers need both raw permissions plus current applicable decision and independent-review authority. Request reads require parent read and both raw creation rights; filtering precedes totals and pagination. Current requester root communication, executor capability/authority, configuration and reviewer qualification are repeated at transaction boundaries.

The incumbent local-operation transaction commits one child graph with the source approved effect, receipt, exact grant disposition, security revision and audit. Receipt replay returns saved facts without a second child or revision change. Same-root refusal comparison excludes generated IDs/model/configuration witnesses. SQLite's legacy `Fingerprint` column remains the exact grant/receipt foreign-key witness; semantic task comparison uses the saved task fields rather than that column.

Completion delivery creates a new Run in an existing parent Session. It therefore requires current raw `agent-runs.create`, rather than another `sessions.create`. A child-creation grant cannot authorize a report Run. Granting only the child Run creation operation can create and complete the child while report delivery records `Skipped / permission_denied`. Report-creation Review is still unconverted and policy-required delivery remains skipped.

Migration `20261020110000_TenantBackgroundCreationReview` extends the existing approval payload constraint without a new table, queue or executor. Its explicit SQLite rebuild preserves all six incumbent Browser/file approval and receipt guards, adds immutable exact-creation payload protection and refuses downgrade while creation records are retained. Empty downgrade/upgrade restores the current guards. Schema/model agreement, old guard enforcement and reopen are verified with disposable databases only.

The shared organization/Chat inspector uses incumbent AntD Descriptions, Alerts, confirmation and responsive footer. Exact saved values remain inert, including an explicit false report preference. Missing/duplicate/malformed previews disable both decisions while retaining authorized cancellation. Creation receipts say **Background work created** and distinguish creation from execution, completion, report delivery and child read access.

## Exercised workflows

| Scenario | Expected and observed outcome |
| --- | --- |
| Actual model requests Review under compound creation policy | No child while pending. Approving commits the exact frozen child and canonical receipt; terminal child execution and a separately authorized factual report complete. |
| Primary Run or Session creation permission missing | A one-time grant substitutes only for that primary permission. The independently held secondary permission remains required. Child identities match the saved action and the grant is consumed once. |
| Allowed policy, missing both permissions, reject, cancel | Allowed uses the incumbent direct path. Missing authority and negative decisions produce no child or creation receipt. |
| Model repeats an exact rejected request | Both stores suppress it under the same root; no second pending request or child appears. |
| Pending executor authority revoked or configuration changed | Decision becomes unavailable and can be cancelled; no child is created. |
| Direct store commit after secondary/reviewer revocation, with fresh security revision | Both stores refuse without partial child, source effect, receipt or revision changes. |
| Direct store receives a substituted graph | Both stores reject the exact operation mismatch before publication. Configuration change records a denied receipt without a child. |
| Repeated completed local transaction | Both stores return the same receipt, retain exactly one child, and leave the security/source Run revisions at their first committed values. |
| Reader has only one creation permission | Request filtering happens before totals/pages; decision authority stays independently qualified. |
| Actual SQLite application host recreated during pending Review | The exact action survives; approval resumes through existing owners and commits its frozen child once. |
| Synthetic browser Review and one-time authorization | Actual Chat requests open the shared inspector; deliberate confirmations and refreshed receipts create two children in total. |
| Browser configuration change before third decision | Approve/Reject disable, cancellation succeeds and the child count remains two. |

## Verification and corrections

Focused .NET execution used local sandbox escalation for IPC/loopback. Commands were `dotnet test <project> --filter '<selection>' --logger 'console;verbosity=minimal'`, with existing API regressions using `--no-build`; build used `dotnet build AgentCore.sln --no-restore`. No CI/full CI suite, Real host, live provider, deployment, installed-data migration/reset or external account operation was requested or used. API fixtures observed zero outbound provider HTTP attempts.

| Selection and local log suffix under `/private/tmp/tenant-background-creation-` | Final evidence |
| --- | --- |
| Solution build; `final-build.log` | Passed, zero warnings/errors. |
| Actual model/HTTP creation matrix and pending-consent host recreation; `module.log`, `refusal-fixed.log` | 21 distinct passed: 19 grouped plus the two added repeated-refusal cases. |
| Direct transaction, schema and incumbent background stores; `store.log`, `visibility-migration.log`, `replay.log` | 16 distinct passed: 11 grouped, three visibility/empty migration and two replay cases. |
| Preserved SQLite Browser/file negative guards; `preserved-guards.log` | Three passed. |
| Application creation/background/approval/projection contracts; `application.log`, `contract-corrective2.log` | 111 distinct passed: 109 grouped successes plus two corrected projection fixtures. |
| Existing background model/report/recovery and reviewed file flows; `api-regression.log` | 11 passed. |
| Scripted authorization fixtures; `synthetic.log` | Eight passed, including saved creation-result continuation and direct Allowed-policy offering. |
| Shared inspector unit file; `ui-unit.log`, `ui-corrective.log` | 66 distinct passed: 64 grouped successes plus two corrected asynchronous confirmation assertions. |
| Production TypeScript/Vite build; `ui-build.log` | Passed; existing large-chunk advisory remains. |

These are **236 distinct passing local cases** across grouped and corrective selections. Corrective reruns overlap parent selections and are not counted twice. The grouped Application and UI processes initially failed two new fixtures each; those complete only through the named corrective evidence. Passing groups were not repeatedly rerun after test-only corrections.

Initial integrated execution found and corrected SQLite trigger restoration order, dependent receipt-trigger references during rebuild, indexed Guid casing versus JSON casing, and value equality for the saved model pin. The migration now performs its frozen table rebuild before restoring the guards. The model witness uses a value record across persisted reads. Integrated grant tests also exposed the overbroad report Session-creation requirement described above.

The added SQLite repeated-refusal case initially stalled in a second WaitingForApproval state. Diagnostic evidence identified an exact-action versus semantic-fingerprint lookup mismatch. Comparing the saved semantic task fields corrected it; both repeated-refusal cases pass afterward. New projection fixtures now supply genuine trusted lineage and loaded capability; UI confirmation assertions wait for settled visibility without changing product gates.

The live browser process predated a Synthetic-fixture correction: approval continuations replaced the original fixture marker, causing unrelated default Scripted Chat prose in the dimmed backdrop. Child creation, receipt and report facts were correct. The helper now recognizes the saved creation authorization result before checking the original marker; its focused Scripted tests pass. Browser captures retain the observed old-process backdrop and are not presented as final model-copy qualification. The shared inspector source was unchanged between capture and finish review.

Useful focused selectors:

```text
API creation:
FullyQualifiedName~Tenant_background_creation_

Store creation:
FullyQualifiedName~Tenant_background_creation_

New Application contracts:
FullyQualifiedName~TenantBackgroundCreationContractTests

Synthetic fixtures:
FullyQualifiedName~SyntheticAuthorizationAuditTests

API regression:
FullyQualifiedName~Tenant_background_model_|FullyQualifiedName~Tenant_background_completion_crosses|FullyQualifiedName~Tenant_background_completed_child_survives|FullyQualifiedName~Tenant_background_file_read_Review|FullyQualifiedName~Tenant_background_file_effects_mutate_publish_and_report&DisplayName~review

Frontend:
cd web && npx vitest run src/features/access/TenantApprovals.test.tsx
cd web && npm run build
```

## Browser and presentation evidence

The disposable Synthetic API on 7148 and Vite on 7149 exercised actual login, Chat input, approval links, protected confirmation, refreshed canonical detail and Activity child counts. Browser fixture setup and configuration changes used canonical same-origin HTTP routes. No app state injection or fake receipt was used. Console warning/error counts were zero. Captured network evidence contains successful reads/decisions and observer-change request aborts; no unexpected HTTP failure was observed for the exercised path. Only the owned disposable processes are stopped at completion.

Captures are retained under `.impeccable/review/tenant-background-creation/`: `review-1440.png`, `review-390-top.png`, `review-390-bottom.png`, `confirm-1440.png`, `confirm-390.png`, `created-1440.png`, `created-390.png`, `grant-390.png`, `grant-confirm-390.png`, `unavailable-1440.png`, `unavailable-390.png`, `cancelled-1440.png`. Every capture was opened and inspected. The 1440×1100 inspector measured 736px wide; the 390×1000 inspector filled the viewport with independently scrolling content and a wrapped reachable footer. Neither viewport had page horizontal overflow. Finite animations settled before captures; scroll-position pairs are intentional.

The Impeccable detector ran once on the two changed product owners and returned `[]`. A fresh shipped finish reviewer returned **ship**, all five contract sections present, with no material fixes at this bounded incumbent-extension scope. The shipped documenter confirmed incumbent system agreement, updated only the shared surface brief, and preserved PRODUCT, DESIGN and sidecar files. Pre-existing DESIGN/sidecar staleness remains outside this implementation. `git diff --check`, balanced Markdown fences and all 1,694 relative file links in changed documents pass. Both owned disposable host processes exited at completion.

This is a local creation-consent checkpoint. Report-Run creation consent and the remaining proposal work continue under the original active goal.
