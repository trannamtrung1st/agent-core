# Owner credential service, contract and storage retirement — 2026-10-21

Status: complete source module with focused local qualification. Original full proposal goal remains active; no full milestone/proposal acceptance is claimed.

## Removed authority and model

- Remove Application `IOwnerCapabilityStore`, `IOwnerCapabilityService`, `IssuedOwnerCapability`, `OwnerCapabilityService`; remove both Infrastructure owner stores and their three registrations.
- Remove Contracts `OwnerCapabilityHeaders`, `OwnerCapabilityResponse` and old unscoped `AttachPayload`, including its `ownerCapability` property. Canonical tenant DTOs remain.
- Remove `OwnerCapabilityRecord`, DbSet and current EF mapping/snapshot entity. Reviewed migration `20261021102840_RetireOwnerCapabilityStore` contains exactly one Up operation: drop `OwnerCapabilities`. All other current tables and tenancy guards remain. Down creates only an empty historical table; dropped hashes are not reconstructed and no issuer becomes available.
- Remove the issuing `TestOwnerCapability` fixture and unused provider-binding `TestInstances`. Canonical negative tests now use `RetiredOwnerHeader`: a fixed literal header/token with no service lookup, issue, validation, cache or compatibility path. Native-only/health fixtures do not need an owner credential.
- Fix a regression discovered while reviewing the previous registry extraction: capacity had copied the initial limit. It now reads the current options value for each reservation. Lowering a limit retains occupied slots while refusing new ones; raising it admits only the newly available capacity. The regression case would fail with the frozen constructor copy.

## Verification

| Check | Result / actual evidence |
| --- | --- |
| Solution build | Pass, 0 warnings/errors, 54.65s with direct compiler execution. The fixture correction build also passes, 0 warnings/errors, 8.69s. |
| Exact migration integration | 1 pass in 4s: stage the preceding canonical schema with an obsolete hash and retained fixture row; migrate; only the old table disappears; retained data/revision stays exact; current model has no drift. Down yields an empty old table, re-upgrade removes it, foreign keys remain valid. |
| Affected persistence/IAM | 44 pass in 33s: canonical startup and nondestructive reset refusal, Account/IAM integration, identity consolidation and Automation conversation migration roundtrip. |
| Actual host preservation and capacity | 2 pass in 10s: create/authenticate an Account, resolve its tenant principal, admit and complete real Synthetic text; stage an old hash in the preceding canonical schema; startup refuses without deleting it; explicitly migrate; recreate the host and observe the same Account, tenant/principal and complete history. Old header cannot authenticate and issuance route is absent. Capacity lower/raise case passes. |
| Affected API/runtime | 26 pass in 48s: cookie/CSRF/multi-tenancy and restart, retired HTTP route inventory, actual JSON/MessagePack Hub refusal/execution in both stores, canonical text admission, shared registry/capacity, active cancellation, persona approval and runtime shutdown. This overlaps the one capacity case above; 27 distinct API/runtime cases, 72 distinct focused cases overall. No skips. |
| Final static checks | No active owner issuer/store/model/contract type remains; actual assembly/model checks corroborate removal. Historical migrations, negative fixtures, browser key purge and dated documentation are intentionally classified separately. Local Markdown targets and standalone staged diff check pass. |

Commands use `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false` and focused `dotnet test <project> --no-build --no-restore --nologo --filter ...` against the Infrastructure and Api test projects. Filters/logs are retained at `/private/tmp/owner-capability-retirement/`:

- `migration-first.log`: `OwnerCredentialRetirementMigrationTests`.
- `affected-persistence.log`: `CanonicalStartupMigrationTests|TenantAutomationConversationMigrationTests|IdentityConsolidationContractTests|IamIdentityIntegrationTests`.
- `runtime-corrected.log`: `Owner_credential_retirement_preserves|Updated_capacity_limits`.
- `affected-api.log`: `IdentityAndTenantAccessTests|IdentitySqliteRestartTests|Tenant_HTTP_cutover|Tenant_Hub_|Canonical_Session_HTTP|Canonical_tenant_worker|TenantRuntimeRegistryTests|Active_tenant_HTTP_cancellation|Model_prepares_durable_persona_approval|Tenant_runtime_shutdown_retires`.

Each filter term uses `FullyQualifiedName~`. These are affected local checks, not the full CI suite. Backend runtime evidence crosses the actual Synthetic host, authorization, Runtime and SQLite boundaries. No frontend implementation changed, so no new frontend/browser/design review is claimed; the prior browser purge qualification remains a dated checkpoint. Physical audio, hosted providers, production/multi-instance operation and optional CI were not run.

The first host-preservation scenario assumed startup would apply an incomplete schema. The existing startup guard correctly refused before mutation. The corrected scenario explicitly verifies that refusal and retained hash before applying the migration offline; the guard was not loosened and no reset was introduced. The first migration integration already passed. EF tooling 10.0.5 emitted its runtime-version notice (10.0.12) and expected data-loss warning for dropping the obsolete table; the generated diff was reviewed and current-model/roundtrip tests pass. No installed/user database or application data root was selected or reset.

## Retired owner-only test declarations — parity remains required

The following 11 declarations use removed owner-only State/Runtime HTTP or unowned host contracts. Parameterized declarations represented additional old runtime cases. They are retired, not skipped; their removal does not qualify equivalent canonical State/Runtime behavior. Preserve the behaviors below as requirements in the remaining parity audit.

### `tests/AgentCore.Api.Tests/RuntimeStateApiTests.cs`

- `A_noncooperative_provider_is_bounded_by_the_injected_clock_without_erasing_successful_counts`

### `tests/AgentCore.Api.Tests/StateIsolationConfigurationTests.cs`

- `Preset_rejects_an_owner_change_between_reading_its_recipe_and_preparing_the_exact_preview`
- `Persistent_requests_an_explicit_recipe_and_definition_defaults_restore_inheritance`
- `Preview_explains_retained_scratch_and_explicit_terminal_cleanup_without_applying_either`
- `Busy_browser_preview_discloses_pending_transition_without_starting_native_work`

### `tests/AgentCore.Api.Tests/StateIsolationContextTests.cs`

- `Request_isolation_waits_for_child_and_completion_accounting_before_terminalizing`
- `Live_profile_and_memory_restrictions_remove_the_prior_projection_before_the_next_model_call`
- `Session_isolation_keeps_own_followup_but_excludes_other_customer_profile_and_learned_memory`
- `State_refresh_replaces_one_trusted_runtime_section_while_tool_receipt_stays_evidence`
- `State_tool_rejects_forged_ids_limits_refresh_and_rechecks_live_visibility`

### `tests/AgentCore.Api.Tests/StateIsolationDefinitionInheritanceTests.cs`

- `All_definition_policy_fields_survive_publication_and_adoption_while_activation_remains_instance_owned`

## Remaining full-goal work

Internal Runtime live-approval/default authority branches and native/unowned service/store/schema consolidation remain. Canonical rich Skill/reference and locale/model authoring, draft/Drive preview, coordinated State/Runtime/Harness/Experience/maintenance parity, remaining platform guardrails and the exhaustive proposal requirement/route/store/effect/security/design audit remain mandatory. This credential retirement closes one required module and does not redefine the goal around removed or already-tested behavior.
