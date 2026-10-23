# Embedded Run approval retirement — 2026-10-23

The [live protocol retirement](live-approval-protocol-retirement-verification-2026-10-23.md) removed the response-bound decision loop. This successor removes its remaining embedded Run state machine and expiry storage. Canonical tenant requests retain their incumbent transaction and continuation owners.

## Implemented boundary

- Removed `AgentRunApproval`, its decision enum, embedded `AgentRun.Approval`, Begin/Decide/Expire methods and commands, obsolete response DTOs and the generic coordinator's embedded-expiry path. A waiting Run requires a tenant and a nonempty first-class request reference. Claim, revision, cancellation, attempt and effect fences remain.
- Canonical direct/approved effects and typed waits use the reduced Run constructor/copy without carrying an empty parallel approval object. Runtime command generation comes from its current claim or trusted event, never an embedded approval generation.
- The generic SQLite/InMemory Run stores omit embedded expiry from runnable selection. Run payloads no longer serialize the member and reject unknown members. In-memory deletion/decommission counts use waiting Run state, matching the incumbent SQLite counting rule.
- `20261023160000_RetireEmbeddedRunApproval` removes the unused expiry column, removes only explicitly null old payload members and narrows Session execution uniqueness to Running/WaitingForSignal. It refuses any actual embedded approval, retained expiry metadata or unowned approval wait before conversion.
- The migration temporarily drops/restores the four exact frozen Run guards within its transaction and adds two guards against reintroducing the retired shape. Run revisions and immutable pins are preserved. Its target model reuses the frozen predecessor metadata instead of copying the entire generated model. Historical migrations remain unchanged. No user database or data root was opened or reset.

The seven old Automation/Drive bounds cases exercised only the deleted embedded model and were removed. Canonical prepared-operation validators and their bounds are unchanged. Retained effect-identity/restart and memory-preview assertions remain; the old generic expiry/decision fixtures are replaced by canonical request/continuation coverage. The obsolete global Automation destination approval case is removed with its command. This does not qualify the other historical global fixture routes as current tenant execution.

## Verification

**331 distinct backend cases qualified**, with no skipped cases. The initial focused storage selection is included in the larger Infrastructure selection and is not counted twice. All test commands used `dotnet test <project> --no-build --no-restore --nologo --filter <selection>`; multiple selectors use `FullyQualifiedName~…` joined by `|`.

| Selection | Result and evidence |
| --- | --- |
| Domain `AgentRunContractTests` | **38 passed**. Canonical wait releases the claim, wrong request/direct claim is refused, and resumption preserves the final allowed attempt. Unowned waiting is refused; prepared effect identity and existing cancellation/retry/uncertain-effect invariants remain. `/private/tmp/embedded-approval-retirement-domain-20261023.log`. |
| Application `AgentRunCoordinatorTests`, `ToolExecutionBudgetTests`, `ExecutionBudgetRuntimeTests`, `RunBudgetDiagnosticsTests`, `CheckpointBudgetMeasurementTests` | **34 passed**. `/private/tmp/embedded-approval-retirement-application-20261023.log`. |
| Infrastructure Run admission/store class, frozen metadata, canonical startup and selected canonical approval/effect/race cases | **212 qualified passes**: 211 passed in the grouped run; one stale historical trigger-count assertion was corrected and its full historical/current roundtrip passed. `/private/tmp/embedded-approval-retirement-infrastructure-20261023.log`, `/private/tmp/embedded-approval-retirement-metadata-recheck-20261023.log`. |
| API saved consent, model preference, Automation mutations/consent/recreation and configuration continuation | **43 passed** in 3 m 15 s. Actual Runtime requests survive independent work and store recreation, retain exact saved operations, settle consent and produce factual task outcomes. `/private/tmp/embedded-approval-retirement-api-20261023.log`. |
| In-memory/SQLite deletion and decommission blockers | **4 passed**. `/private/tmp/embedded-approval-retirement-decommission-20261023.log`. |
| Solution build | **Passed**, zero warnings/errors, 11.22 s. `/private/tmp/embedded-approval-retirement-build-20261023-r4.log`. Command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. |

The Infrastructure selection was `AgentRunAdmissionStoreTests`, `FrozenMigrationMetadataTests`, `CanonicalStartupMigrationTests`, `Durable_approval_retains_exact_action`, `Durable_decision_commits_current_human`, `Concurrent_same_root_preparation`, `Approval_schema_rejects_forged`, `Approved_persona_effect_commits_once`, `Durable_running_wait_requires`, `Durable_wait_releases_the_Session` and `Canonical_pending_request_survives_embedded`.

The API selection was `Model_preference_authorization_persists_exact_parameters_and_resumes_the_saved_effect`, `Original_conversation_receives_saved_wait_and_recovers_decision_after_independent_work`, `Tenant_model_Automation_exact_consent_resumes_once_or_truthfully_refuses`, `Tenant_model_Automation_mutations_create_inspect_edit_enable_disable`, `Tenant_model_manual_Automation_pending_consent_survives` and `Canonical_configuration_`. Decommission selection was `Decommission_keeps_reference_blockers` and `Referenced_instance_is_not_deleted_and_names_the_blockers`.

The first storage batch caught missing separators while restoring frozen trigger statements. After correcting that migration SQL, all **11 focused storage cases passed**, including empty-member conversion/reopen, retained record/wait/expiry refusal with unchanged rows/history/guards, canonical pending-request roundtrip and approved persona effect-once recovery. `/private/tmp/embedded-approval-retirement-storage-20261023-r1.log`, `/private/tmp/embedded-approval-retirement-storage-20261023-r2.log`. The later metadata correction replaces an obsolete count assumption with the actual predecessor count (**133**), current count (**135**) and byte-for-byte comparison of every predecessor trigger. Historical-to-current Down/Up, index preservation, model parity and foreign-key validation all passed. Only the metadata fixture changed after the grouped production checks; those passing cases were retained.

Source scanning found no remaining production references to the deleted embedded type/decision/command APIs outside historical migration metadata. The one test reference asserts the type is absent. Documentation links/fences and staged whitespace checks are included in delivery preflight. No frontend source changed; browser/build checks were not repeated for this backend/storage slice. CI, live providers and complete proposal acceptance were not run or claimed.

## Remaining scope

Primitive `ToolApprovalGrant`, old local preparation/executor/service APIs, generic profile/global store assumptions and broader dependency/schema consolidation remain. No approval default or permission catalog changes are made here. This slice does not establish complete legacy-authority retirement or proposal-wide security, isolation, functional and UI acceptance. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) retains the complete outstanding scope.
