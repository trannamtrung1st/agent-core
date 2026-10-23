# Global Experience and Automation authority retirement — 2026-10-23

This follows `2a5cec8b` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It removes a complete obsolete Application dependency chain while preserving the existing canonical tenant owners. It is scoped retirement/integration evidence, not full proposal acceptance.

## Retired roots and shared owners

- Removed global `ExperienceService`: local-Profile source inspection, recall, recording, retrospective request/admission and pending-graph repair. Removed its DI registration and the unused `SessionManager` field/constructor dependency, including the shared fixture parameter.
- Removed `AdminAutomationAuthoringService` and its local-Profile create/update/delete/manual-admission path. Canonical `TenantAutomationService`/`TenantAutomationMutationService` continue to share `TenantAutomationAuthoring` and the existing exact approval/Run transactions.
- Removed generic preset eligibility/catalog DI and `UnifiedEventCatalog` with its unused descriptor records. Canonical Event reads retain their tenant authority owner. The three effective version-1 presets now live directly in `TenantAutomationPresetRules`; shared value records remain separate from authority.
- Rehomed the exact unchanged `StableSessionCheckpoint` body to `TenantExperienceSourceProjection`, its current owning source projector. It performs no store read or owner selection. Both store implementations and review paths use it there.
- Removed the service-owned Experience tool metadata and duplicate consolidation-schema construction. `TenantExperienceToolContract` now supplies registry and offered descriptions/schemas; registered metadata requires the same exact source/cursor as current tenant execution. Tool names, effects, offering authority and replay classification remain unchanged. Current tenant model descriptions/schema generation remain the same owner and content.

No new effect owner, scheduler, framework, permission, approval default, schema or migration is introduced. Raw historical stores and installed data are preserved; no old Profile data is adopted into tenant ownership.

## Preserved behavior and fixture migration

Three new immutable-preset cases compare identity/version/name/description/instruction hashes captured before retirement. All three match. Trigger forms and provenance rules remain in their incumbent owners; current prerequisite and authoring flows retain independent source permissions and disabled creation defaults. Static comparison also confirms the checkpoint body is exact.

The actual tenant Experience HTTP/model journey asserts all four retired authority/catalog types are absent from the built Application assembly, then inspects/records/recalls derived evidence, refuses stale management, suppresses/disables/resets it, preserves source history and keeps Memory separate. Absence assertions supplement this executed use case; they are not its sole evidence.

The old Email feed fixture depended on global authoring and an already-unregistered Core Event dispatcher. It is retired rather than restoring that authority. Its mixed-account coalescing and frozen-subscriber concerns move into the existing canonical tenant Email-watch matrix: neither/first/second/both watches revoked, subscription added after source commit, and removal of the original subscription. Actual tenant HTTP setup, source watcher, Event processor, exact consent, Run and public Activity assertions exercise both stores. Six physical watch-store cursor/fence checks and two raw Experience deleted-versus-late-completion parity cases are retained as limited storage contracts, not tenant-authority qualification.

**21 obsolete test method bodies across eight fixture paths** are retired with the removed authority; exclusively obsolete helper/live files are removed. Exact manifests are `/private/tmp/experience-automation-retirement-files-20261023.json`, `/private/tmp/experience-automation-retirement-tests-20261023.json` and `/private/tmp/experience-automation-retirement-methods-20261023.txt`. Removed cases are not counted as passing current tests.

| Retired concern | Current evidence / remaining boundary |
| --- | --- |
| Global Experience source/record/recall, cursor checks, settings, suppression and restart | Canonical owned HTTP/model/store, manual review, independent Automation and event source/coverage/recovery cases retain actual owner, raw reads, exact consent, immutable source and factual outcome boundaries. |
| Global preset/Automation authoring, destination selection and manual invocation | Current tenant presets, complete model create/edit/enable/disable flow and actual-human manual occurrence admission exercise the incumbent owners. Broader previously qualified destination/read/replay reports remain version-scoped evidence. |
| Shared function tool metadata | Canonical registry contracts and offline OpenAI-compatible named-tool mapping use the same current Experience schema owner. |
| Global busy-parent/report and background completion recovery fixtures | The two global-authoring test methods are retired. Final acceptance still requires canonical parity for their busy-parent/reopen behavior and twelve completion-recovery modes: complete, prose, wrong-tool, transport, semantic, incomplete-tool, authentication, reserve, oversized, oversized-twice, oversized-transport and oversized-transport-twice. Existing scoped completion reports do not by themselves prove every one of these combinations. Historical model/assertion bodies remain recoverable with `git show 2a5cec8b:tests/AgentCore.Api.Tests/ReliabilityJourneyTests.cs`. This gap is not waived or counted as current qualification. |

## Focused local verification

| Group | Observed result |
| --- | --- |
| Application metadata/authority, immutable presets, canonical construction and Session lifecycle | **114 distinct cases qualified**: 113 initially pass; the complete 11-case continuity contract class passes after correcting one stale assertion. |
| First actual tenant Experience HTTP/model/retention journey | **2 passed**, zero failed/skipped, 17 s, both stores. |
| Canonical Experience stores, source/consent/restart and provider contracts | **48 passed**, zero failed/skipped, 2 m 19 s. |
| Initial grouped API run | 81 passed, 13 failed: 12 obsolete Email cases lacked the Profile fixture row; one controlled event-retry case was rejected by the frozen-clock helper. The Email cases were subsequently retired; the current retry group passes below. |
| Canonical Event Experience retry/consent/restart with deterministic admission deferral | **20 passed**, zero failed/skipped, 1 m 47 s. Includes the previously failed case; overlaps the initial API group. Together these qualify **82 distinct current API cases**. |
| Expanded canonical tenant Email-watch matrix | **40 distinct cases qualified**: initial run 38 passed / 2 failed; exact two all-revoked cases passed after correcting their historical-delivery expectation, 12 s. No Run or approval is admitted; public Activity and the flushed bucket prove policy-skipped outcome. |
| Physical Email watch cursor/fence stores | **6 passed**, zero failed/skipped, 15 s. |
| Final serial solution build | Passed, zero warnings/errors, 13.87 s. |

**290 distinct focused cases qualified: 114 Application + 122 API + 54 Infrastructure.** Repeated first-path, contract and Event rechecks are not added twice; obsolete failures are not counted as passing or replaced one-for-one. These grouped checks do not establish full-suite or proposal acceptance.

Commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`:

```text
Application: FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~TenantContinuityContractTests|FullyQualifiedName~TenantAutomationPresetCatalogTests|FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~SessionCatalogTests|FullyQualifiedName~AgentInstanceTests
Contract correction: FullyQualifiedName~TenantContinuityContractTests
First Runtime: FullyQualifiedName~Tenant_experience_actual_HTTP_model_and_human_retention_remain_separate
API: FullyQualifiedName~Tenant_experience|FullyQualifiedName~Tenant_event_Experience|FullyQualifiedName~Tenant_automation_preset|FullyQualifiedName~Tenant_model_Automation_mutations_create_inspect_edit_enable_disable|FullyQualifiedName~Tenant_model_manual_Automation_invocation_commits_one_occurrence|FullyQualifiedName~EmailMonitoringReliabilityTests|FullyQualifiedName~Deleted_experience_wins_over_completion
Infrastructure: FullyQualifiedName~Tenant_experience|FullyQualifiedName~Tenant_continuity_historical_projection|FullyQualifiedName~Experience_record_contract_round_trips_as_a_semantic_capability|FullyQualifiedName~ToolCatalog_wire_names_are_openai_compatible
Historical recovery/setup recheck: FullyQualifiedName~EmailMonitoringReliabilityTests|FullyQualifiedName~Tenant_event_Experience
Final deterministic Event recovery: FullyQualifiedName~Tenant_event_Experience
Canonical Email watch: FullyQualifiedName~Tenant_email_watch_baselines_then_commits_owned_metadata_and_fences_unadmitted_watch_off
Exact all-revoked repair: FullyQualifiedName~Tenant_email_watch_baselines_then_commits_owned_metadata_and_fences_unadmitted_watch_off&DisplayName~coalesce-all
Physical watch stores: FullyQualifiedName~EmailWatchStoreTests
```

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs use `/private/tmp/experience-automation-retirement-` with `build-qualified-20261023.log`, `contracts-20261023.log`, `continuity-recheck-20261023.log`, `runtime-first-20261023.log`, `api-20261023.log`, `stores-20261023.log`, `recovery-recheck-20261023.log`, `event-deferral-20261023.log`, `tenant-email-watch-20261023.log`, `watch-all-revoked-20261023.log` and `watch-store-20261023.log`. The historical recovery recheck adds `--logger 'console;verbosity=detailed'` to retain admission-deferral metadata; it passed all 20 Event cases and failed all 12 obsolete Email cases before their retirement.

The first build corrected only raw-fixture constructor argument casing. The initial contract failure expected every authorization tool to disappear when continuity was disabled. Independent private Skill-read consent is still eligible. The corrected test requires continuity tools and their exact consent branch to disappear while preserving that separate Skill branch. No product policy is weakened. Passing unaffected identities are retained rather than rerunning their full group after this fixture-only change.

An intermediate raw Email setup explicitly persisted the historical Profile fixture row formerly created by the removed helper. That exposed its dependency on the already-retired global dispatcher. The intermediate setup was removed with the obsolete fixture; no production guard or registration was restored. Ten new cases extend the existing 30-case tenant watch matrix to 40. The two initial all-revoked failures confused the immutable raw Coalesced match with the current policy-skipped bucket outcome. Corrected assertions preserve the raw match and require zero Runs/approvals, policy-skipped public Activity, historical source counts and a flushed bucket with a factual completion code.

For controlled Event retry, `DeferUnacceptedApprovalDispatch` preserves the prior failure and sets `AwaitingRuntimeAdmission`. The clock helper now accepts only that marker on the exact Run with byte-equal already-observed failure data after its original retry deadline, still bounded to eight deferrals. The Event tests deterministically claim the actual due retry, issue the existing admission-deferral command, verify the saved marker/failure, then let the ordinary dispatcher finish. This exercises the guarded helper branch in the retry/restart modes; unexpected provider failures remain errors and the original early-dispatch/no-effect assertions remain. This is a strict fixture correction, not an added production retry or a claim about the cause of the older unexplained stall.

The complete goal remains active. Canonical completion-recovery parity above, other global/internal/lifecycle roots, historical fixtures, final store/tool authority inventory, safe retained preparation/schema consolidation and proposal-wide security, isolation, function and rendered UI acceptance remain required. This backend module changes no frontend and claims no new browser acceptance. All executed state is disposable offline Synthetic. No CI, hosted provider, user-data reset, Real restart, push or deployment was performed.
