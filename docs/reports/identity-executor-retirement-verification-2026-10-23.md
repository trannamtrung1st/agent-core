# Generic identity executor retirement — 2026-10-23

This follows `897efb4e` and advances the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). It qualifies this retirement module, not the full proposal or final milestone acceptance.

## Change and authority boundary

The model executor no longer falls back to Profile-based Memory consolidation/forgetting, Experience source/record/recent/consolidation or continuity search/get. Unowned calls are refused before global Instance lookup or argument parsing, even with a permitted Definition, caller-built Session/Run IDs, Managed Harness delegation and historical context. Canonical tenant dispatch remains first and continues through the existing owning services and exact approval/Run transactions. `experience.recent` has no generic execution or offer fallback.

The global `IdentityMaintenanceService` and `ContinuityService` and their DI registrations are removed. `IdentityMaintenanceContract` preserves only unchanged bounded source parsing and model guidance; `ContinuityRelevance` preserves only unchanged lexical ranking, shared by canonical Memory/history/Experience projections. The generic executor loses three direct dependencies: Experience, Continuity and IdentityMaintenance. No compatibility constructor or alternate effect owner is introduced. The unused old identity-maintenance meter is retired; canonical operation audit, receipts and Run outcomes retain inspection.

The Runtime no longer asks unowned helpers for Harness, selected Experience, Experience recall, continuity context or autonomous consolidation permission. Canonical private evidence still enters its guarded tenant projector. Capability loading now obtains consolidation eligibility from `ITenantRunContext.CurrentContinuitySettingsAsync`, matching initial projection instead of consulting the global service. This setting supplies eligibility context; it does not replace raw permission, source witnesses or exact consent.

Two global identity-maintenance tests and five Profile-based retrieval/execution cases are retired with their removed owners. Current canonical store/HTTP/model cases cover their applicable ownership, source, consent, refusal, projection and restart concerns. Pure source-bound checks remain. The Definition override used by the opt-in historical live fixture moves into its own helper; no live provider execution is claimed. Existing unowned management tests now construct their purported delegation explicitly instead of reading it through the removed public Harness callback.

## Verification

All runs use existing Synthetic adapters and disposable stores; no installed database, approval default, schema, UI or wire protocol changes.

| Focused group | Observed result and log |
| --- | --- |
| Registry/policy and bounded source contracts | **64 passed**, zero failed/skipped; `/private/tmp/identity-retirement-contracts-20261023.log`. Includes the eight newly refused identity tools under caller-built delegation/context. |
| Actual HTTP/model initial selection | 19 passed, one frozen-clock timeout. The grouped recheck supersedes its Experience maintenance results. The two learned-Memory receipt/next-Run recall and four continuity source-change/output/provider-boundary cases pass and remain unchanged; `/private/tmp/identity-retirement-runtime-20261023.log`. |
| Grouped actual HTTP/model/worker and management context | **48 passed**, zero failed/skipped in 2.66 min; `/private/tmp/identity-retirement-runtime-r2-20261023.log`. Covers approve/reject, scheduled and exceptional grants, opt-out/suppression/refusal, authorized/refused NoAction, 16 current Harness policy transitions and unowned management refusals. |
| Canonical both-store Memory, Experience and continuity boundaries | 86 passed; two stale downgrade/reopen fixtures failed before correction; `/private/tmp/identity-retirement-boundaries-20261023.log`. |
| Corrected downgrade/reopen fixtures | **2 passed**, zero failed/skipped in 29 s; `/private/tmp/identity-retirement-schema-recheck-20261023.log`. |
| Final serial solution build | **Passed**, zero warnings/errors in 65.31 s; `/private/tmp/identity-retirement-build-r5-20261023.log`. |
| Documentation and staging hygiene | New relative Markdown targets resolve; retired files have no live Markdown links; `git diff --check` passes. |

**206 distinct focused backend cases are qualified:** 64 Application, 54 API (48 grouped plus six unchanged learned-Memory/continuity cases from the initial selection), and 88 Infrastructure (86 initial plus two corrected schema cases). Overlapping Experience passes are counted once. The original admission deferral did not recur in the grouped recheck; its precise cause remains unconfirmed.

The store group exercises actual owning-store transactions, exact read/mutation consent, atomic Memory lineage, refusal/revocation, changed sources, causal User isolation, whole-record/history output bounds and SQLite upgrade/reopen. It exposed two old downgrade-refusal fixtures that queried current receipt columns after safe successor Down steps had already committed. Both now restore the complete current migration sequence and assert its applied history/model before reopening. Retained-source guards and request readback remain asserted; no product migration changes.

The first build found four fixture calls to the retired Harness callback. Their negative tests now construct caller context directly, while canonical context assertions remain. A later helper-signature compile error was corrected by retaining the shared signature and reading the injected clock. Passing grouped checks followed those corrections. The final build additionally includes dead telemetry retirement and the two schema fixtures.

The first runtime selection passed 19 of 20 cases. One Memory-store approved Experience case remained `WaitingToRetry`; its helper never advanced the frozen clock to the saved deadline. The helper now advances the actual injected FakeTimeProvider for bounded admission deferrals, fails explicitly on a recorded provider failure, and includes failure diagnostics. The original deferral's precise cause is unconfirmed; no product retry policy is changed.

```text
Application: ToolRegistryPolicyTests|IdentityMaintenanceToolContractTests
Initial API: Tenant_experience_Automation_maintenance_uses_exact_consent_current_settings_and_factual_successors|Tenant_learned_memory_actual_HTTP_model_and_receipts_use_owned_scopes_then_private_Evidence_on_the_next_Run|Tenant_continuity_actual_HTTP_model_source_change_blocks_captured_output_or_a_further_provider_call
Grouped API: Tenant_experience_Automation_maintenance_|Tenant_experience_source_review_can_finish_|Tenant_Core_procedures_use_claimed_policy_|SelfManagementOperationTests|SelfManagementCompletionTests
Infrastructure: Tenant_continuity_|Tenant_memory_operation_|Tenant_experience_
```

Each selector term is prefixed `FullyQualifiedName~`; commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. The grouped API run also uses `--logger 'console;verbosity=detailed'` for retained runtime diagnostics. Builds use `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.

## Remaining scope

The complete goal remains active. Global learned-Memory prompt/admission and other unowned credential, workspace/provider, configuration and lifecycle assumptions still need retirement/audit, together with retained preparation-schema consolidation and final proposal-wide integration, security, isolation and rendered UI acceptance. `ExperienceService` still has separate legacy internal callers; removing its generic model dispatch does not establish that every global service/store root is gone. This module changes no frontend and supplies no new UI/browser evidence. CI and live providers were not requested or run; existing user data and Real hosts are preserved.
