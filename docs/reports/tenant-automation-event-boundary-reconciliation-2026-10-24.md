# Automation and event boundary reconciliation — 2026-10-24

Preimage: `52177338`. This completes the current source disposition for the **106-method Automation/event family**. No production code changed: the owning transactions already contain the required authority checks. Retained native data contracts remain distinct from reachable private authority. The original proposal's other method families and final I/O, worker and rendered UI/design acceptance remain open.

The delivery ledger now links the earlier human-authority/common-boundary reports in one compact table instead of repeating their successive census totals. Those reports retain their exact source versions, checks and limits.

## Current source dispositions

The [method index](tenant-store-method-source-index-2026-10-24.json) binds every method to its declarations, implementations, callers and the following eight review groups:

| Methods | Current boundary |
| --- | --- |
| 35 | Native registration/occurrence data: 33 uncalled slots; remaining calls belong to native adapters. Tenant-null selectors/global filters and exact existing identity prevent adoption of owned rows. An unowned record merely containing an owned Instance GUID does not acquire canonical Tenant/executor ownership or Runtime admission. |
| 23 | Native Core/external event facts, deliveries, buckets and coverage: 22 uncalled slots; only Core snapshot has a native self-call. Explicit owner guards, actual Instance/SQLite ownership constraints and tenant-null selectors/global filters exclude canonical private records. |
| 7 | Native Email-watch data: six uncalled slots; remaining delivery check belongs to native admission fencing. Raw credential/binding access, tenant-null watch/fact filters and database constraints remain separate from canonical monitoring authority. |
| 11 | Owned Automation lifecycle: exact Tenant/Instance/executor joins; human visibility before totals and original login/security after reads; original human and independent current permissions inside authoring transactions; fresh configuration/source/parent checks at admission; exact occurrence-only consent reservation and once-only atomic settlement. |
| 4 | Model Automation reads/mutations: actual retained Run and optional current generation; strict typed arguments, current executor/source IAM and policy; bounded actual admitted intent; exact observation/revisions and effect receipt; retained read checkpoints cannot waive current raw source rights. Preset provenance supplies no activation/tool grant. |
| 5 | Owned webhook sources/receipts: original-human management with security/source CAS; visibility before totals; active tenant/source/revision/token checks at purpose ingress; bounded normalized evidence, source-local exact dedupe and captured subscriptions. Ingress creates facts, never work or tenant role authority. |
| 9 | Owned Email-watch lifecycle: original-human configuration; exact technical lease/config/runtime/auth/credential witnesses; fresh callbacks around provider I/O; no historical baseline emission; current bound executor/source/policy fanout; atomic cursor and closed metadata source proof. Retained reads and unadmitted watch-off delivery have intentionally different eligibility. |
| 12 | Owned event delivery/activity/coverage: actual tenant/source/EmailAccount visibility before totals; original-human read fence; bounded worker enumeration; current executor/source/subscription/policy checks at match/coalesce; immutable captured subscriptions, CAS and bucket limits; exact factual skip/finish without execution. Only the occurrence transaction creates work. |

All **65 native slots have no Application, HTTP, model or hosted-worker call site**. Five call/reference sites across four slots belong to native adapter self-calls and subscription lookup; the other 61 slots are uncalled. These are retained native data/test mechanisms, not hidden tenant APIs. [Occurrence retirement](global-occurrence-authority-retirement-verification-2026-10-23.md) and [Email retirement](global-email-authority-retirement-verification-2026-10-23.md) retain the removed-root evidence. Canonical adapters bypass SQLite global filters only with actual tenant and owning-record restrictions.

Human authoring, independent approval and durable execution remain separate. Scheduled/event admission invents no human requester. A valid saved decision does not require the original requester to remain logged in. Current executor IAM, review policy, exact source/claim witnesses and reviewer qualification still apply at their respective boundaries. Token rotation or subscription edits do not retrospectively erase an admitted source; retained-source output still requires the exact admitted facts and current raw read authority.

The scheduler's production entry calls `TenantAutomationService` and `TenantEventProcessor`; Email monitoring calls `TenantEmailMonitoringService`. These entries supply no ambient tenant, human or grant. This source trace and the service/recovery scenarios below do not by themselves accept every hosted lifecycle/fault combination in the proposal-wide worker gate.

## Focused verification

**571 distinct cases pass, zero failures or skips.** All four processes are terminal with exit code 0, and the TRX case names have no overlap across the four selections.

| Group | Passed | Observed scope | Duration |
| --- | --- | --- | --- |
| First representative API group | 122 | Actual both-store ownership/visibility, fresh occurrence execution, webhook purpose authentication and receipt dedupe, Email baseline/current binding/watch-off fences | 7m36s |
| Complete API lifecycle/model group | 318 | Authoring/edit/reopen, forged-progress rollback, pinned fresh configuration, exact admission consent, existing conversation, completion destinations, events/buckets, SQLite host recreation, model reads/mutations/manual invocation, presets and activation defaults | 21m26s |
| Native storage/recovery/authorization group | 53 | Watch claims/cursor/reopen/revocation, bounded tied cursors and buckets, durable filter CAS, controlled classifier transport/failure outcomes | 1m09s |
| Authoring/admission/default contracts | 78 | Strict typed mutation/admission shapes, shared authoring transitions, preset provenance and disabled Event activation defaults | 306ms |

The first API group qualified before the broader selections began. Creation/admission observes the actual tenant/Instance/executor; forged owner fields, foreign IDs, current permission/source loss and stale witnesses cannot create work. Purpose ingress retains authenticated facts without executing them. The broader workflows exercise actual model tools and worker service passes, saved independent consent, exact once-only execution, private read-source withholding, completion facts and fresh SQLite-host recovery. Pure contract/native checks remain separately identified above.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>' --logger 'trx;LogFileName=tenant-automation-reconciliation-<group>-20261024.trx'`. The core API selection joins these three exact names with `FullyQualifiedName~` and `|`: `Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes`, `Tenant_webhook_management_and_authenticated_receipts_repeat_current_source_authority_without_creating_work`, and `Tenant_email_watch_baselines_then_commits_owned_metadata_and_fences_unadmitted_watch_off`.

The lifecycle API selection contains all 35 public test methods in `TenantCredentialAccessTests.Automation{Authoring,Configuration,Recovery,Admission,AdmissionRecovery,Conversation,Completion}.cs`, `TenantCredentialAccessTests.{Events,EventRecovery,WebhookRecovery}.cs` and `TenantAgentAccessTests.Automation.{Activation,Invocations,Mutations,Presets,Read}.cs`. Every selected method has executed TRX results. The exact filter is retained at `/private/tmp/tenant-automation-reconciliation-lifecycle-filter-20261024.txt` (SHA-256 `f9bdf83701d65196716c7628977cb6fe089543ac5487e04b9be27593031bc85a`); its executed wrapper is `/private/tmp/tenant-automation-reconciliation-lifecycle-20261024.sh`.

The Infrastructure selection joins `EmailWatchStoreTests`, `CoreEventBucketTests`, `EventRecoveryOperationsTests` and `AutomationAuthorizationProviderTests`; the Application selection joins `TenantAutomationMutationContractTests`, `TenantAutomationAuthoringTests`, `TenantEventAdmissionContractTests`, `TenantAutomationPresetCatalogTests` and `AutomationActivationDefaultTests`. Each uses `FullyQualifiedName~` and `|`.

All commands use the existing clean solution build with `--no-build --no-restore`. Its production-source hash remains **`a86155bd9b9b79f28630fcd9852b959740e24b65fb0711f92b8e667d459f233c`**, recomputed before execution and again when generating the index: 1,712 Application/Infrastructure/Api C# files, excluding `bin`/`obj`. No production file changed after the [zero-warning/error build](tenant-approval-boundary-reconciliation-2026-10-24.md#focused-verification); repeating it would add no evidence for this source/docs-only review.

Logs are `/private/tmp/tenant-automation-reconciliation-{core,lifecycle,native,contracts}-20261024.log`; matching TRX files are in each project's `TestResults`. No full local or hosted CI was run. The checks use isolated Synthetic hosts and deterministic adapters; they do not establish live provider/customer outcomes or final rendered UI/design acceptance. No installed-data reset, Real-host restart, push, PR or `.agents` edit occurred.

## Remaining closure

The index still contains **92 interfaces, 544 callable methods, 547 syntax bodies (three explicit inherited defaults) and 1,544 call/reference sites**. It now records **287 reviewed / 257 pending methods**, with **110 of 180 uncalled slots reviewed / 70 pending**. The remaining families are Session/Runtime (68), Profile/Memory/Experience (57), files/Artifacts (56), Sandbox (41) and Credential/account effects (35). Nine expected generated-regex diagnostics belong only to the read-only Roslyn compilation, not the passing real build.

Binding and disposition are source evidence, not universal runtime proof. The [acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) retains the remaining method/effect queue, all final I/O/worker reconciliation and rendered keyboard, read-only, error, responsive and design confirmation obligations. This checkpoint does not complete the original proposal.
