# Canonical schema and cross-family boundary qualification — 2026-10-24

Preimage: `5e30b28e`. **135 distinct focused cases pass**: one complete physical/model schema check, 84 owned-store integration cases and 50 actual Synthetic HTTP/Runtime cases. This advances the [source inventory](tenant-store-effect-inventory-2026-10-24.md) into measured database and boundary evidence. It does not accept the complete proposal or every method/effect combination.

## Change and schema result

Added [CanonicalSchemaAcceptanceTests](../../tests/AgentCore.Infrastructure.Tests/CanonicalSchemaAcceptanceTests.cs). A disposable SQLite file enters the existing `SqliteMemoryStore.EnsureCreatedAsync` startup path, using the production `SqlitePragmaInterceptor` and an ordinary connection string without a test-only Foreign Keys option. It then reopens through a new context and repeats startup. No application, migration, SQL resource, serializer, wire, permission/default or frontend source changed.

| Check | Observed result |
| --- | --- |
| Discovered vs applied migration history | All **95** migration IDs apply; none pending |
| EF model vs snapshot | `HasPendingModelChanges` is false before and after reopen |
| Physical table set vs current relational model | Exact **81-table** match, excluding EF framework tables; OwnerCapabilities and AgentWorkspaceItems absent |
| Every modeled column | Exact **830-column** name/type/nullability match |
| Every modeled foreign-key relationship | Exact **131-key** target/source/principal-column and composite ordering match |
| Production connection settings | Foreign keys enabled, WAL mode and configured 5,000 ms busy timeout before/after reopen |
| Physical consistency | Empty foreign-key check and integrity result `ok` |
| Repeated startup/reopen | Every table/index/trigger definition remains byte-for-byte identical |

This is stronger than a DbSet count or an empty migration list. The model comparison checks the stated column/relationship metadata; it does not assert that every SQL trigger, permission path or foreign-key delete action has independently been attacked. Executed owned workflows below provide separate negative/effect evidence.

The earlier source count of 96 is clarified: it counted non-Designer source files in the migration directory. There are **95 implementation files/IDs and one metadata helper**, `20261021214545_TenantExperienceConsent.Metadata.cs`. That helper reads frozen model SQL and is not another migration. The JSON index now distinguishes those sets. Historical Up/Down SQL and installed-data history are unchanged.

## Executed boundary scenarios

| Area | Actions and expected/observed result |
| --- | --- |
| Identity/System/tenants | One Account obtains distinct tenant Users/authority; System principal cannot read private tenant data; Agent cannot become human Owner. Real resource grants cannot invent/reparent targets. Concurrent last-owner removal commits only the eligible change. Login/password/invite/revocation cases preserve current authority and audit. |
| Definitions/Instance configuration | Real owned publication, version adoption and Skill/Resource dependency changes retain exact sources through reopen; foreign/global paths and stale/security-invalid transactions are refused. |
| Credentials/bindings | Real create/replace/disable/delete and same-tenant binding retain protected bytes and independent use permission; global adapters cannot read/write owned values or bind owned Instances. Reopen preserves secret protection and current ownership. |
| Session/Run | Current chat and Session-read authority remain independent. Accepted Run keeps actual requester rather than Session creator; other-tenant requests/revoked roles cannot read or submit. |
| Workspace/Artifact | Same-Account foreign tenant/Agent/Session substitution and unowned backing scope cannot select private source bytes or create roots. Exact deletion cleanup preserves neighbor bytes/metadata, validates committed owner/time proofs and makes bounded progress across tenants despite one failure. |
| Approval/Memory/Experience | Ordinary Review rechecks underlying human permission and independence; expiry resumes once and only a new root may request again. Forget/record use exact Review or eligible primary-only grant; unrelated source/operation authority is not borrowed. |
| Native lifecycle/HTTP | Browser ownership cannot be promoted from an unscoped namespace or read/reset/released through unscoped entry points. Stranded Sandbox approval and uncertain Agent cleanup recover without native replay/foreign-owner cleanup. Completed HTTP receipt survives reopen without another network effect; fresh source loss blocks its body. |
| Audit/governance | Forged lineage and stale witnesses cannot mutate actual Run facts; unknown/malformed model input stays out of authorization audit values. Governance reads are bounded/coherent/independent and fail after revocation. |
| Actual model Automation | Real Runtime creates, inspects, edits, enables/disables and finishes under current ownership. Semantic refusal creates no effect; exact decisions resume once or truthfully refuse. Lists filter before totals and later source loss blocks output. Human/manual and no-human source modes retain fresh occurrence roots and actual worker outcomes. |
| Startup/retired routes | Unknown/untracked schema startup refuses without changing selected database bytes. All 228 retired HTTP routes remain refused even with old tokens or a Platform Operator. |

The supported InMemory/SQLite variants execute where declared by each fixture; this is not 135 unique production features. Browser ownership cases use their native ownership fixture without claiming live website/business-effect acceptance. Account/HTTP/native fixtures remain offline/deterministic; no hosted provider call was made.

## Focused verification

| Group | Result |
| --- | --- |
| Initial schema use case | 1 passed, 8 s; later strengthened to production interceptor settings and repeated, counted once |
| Store boundary selection | **84 passed**, zero failed/skipped; 3 m 54 s |
| Synthetic HTTP/Runtime selection | **50 passed**, zero failed/skipped; 3 m 13 s |
| Final schema use case | **1 passed**, zero failed/skipped; 8 s |
| Serial solution builds | Passed twice, zero warnings/errors; 55.65 s then 10.47 s |

Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Tests use `dotnet test <project> --no-build --no-restore --nologo --filter <selection>`. The schema selection is `FullyQualifiedName~CanonicalSchemaAcceptanceTests`. Exact selected test methods/case counts are listed below; each can be reproduced with `FullyQualifiedName=<method>` joined by `|`.

Logs: `/private/tmp/tenant-schema-acceptance-build-20261024.log`, `/private/tmp/tenant-schema-acceptance-build-final-20261024.log`, `/private/tmp/tenant-schema-acceptance-first-20261024.log`, `/private/tmp/tenant-schema-acceptance-final-20261024.log`, `/private/tmp/tenant-store-boundaries-20261024.log`, `/private/tmp/tenant-store-api-20261024.log`. Machine results: `/private/tmp/tenant-schema-acceptance-results-20261024/tenant-store-boundaries-20261024.trx` and `tenant-store-api-20261024.trx`. All handles are terminal; fixture databases/native temporary state are disposed.

No broad backend/frontend/CI suite, browser UI journey, live OAuth/provider, production deployment, user-data reset, Real-host restart, push or PR was part of this module. The passing boundary groups use unchanged application assemblies; only the schema fixture was strengthened/rebuilt afterward, then rerun.

## Exact integration selection

### Infrastructure — 84 cases

| Selected method | Cases |
| --- | ---: |
| `AgentCore.Infrastructure.Tests.CanonicalStartupMigrationTests.Fresh_baseline_reopens_without_file_rows_or_changing_independent_profile_revision` | 1 |
| `AgentCore.Infrastructure.Tests.CanonicalStartupMigrationTests.Legacy_untracked_and_unknown_schemas_require_reset_without_mutating_the_selected_database` | 5 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Actual_instances_have_atomic_identity_scoped_inventory_and_archive_revocation` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Bootstrap_is_one_use_invites_are_account_bound_expire_and_cannot_be_replayed` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Concurrent_owner_removal_commits_only_one_and_stale_changes_leave_audit_unchanged` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Instance_identity_transactions_recheck_account_status_and_lose_stale_races_without_orphans` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Legacy_credential_binding_paths_exclude_owned_instances_and_preserve_their_rows` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Password_rotation_revokes_all_sessions_and_platform_suspension_preserves_last_active_owner` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Resource_grants_resolve_real_ownership_and_cannot_fabricate_or_reparent_targets` | 2 |
| `AgentCore.Infrastructure.Tests.IamIdentityIntegrationTests.Same_account_has_distinct_tenant_authority_and_no_platform_or_agent_shortcut` | 2 |
| `AgentCore.Infrastructure.Tests.TenantAgentDecommissionTests.Committed_decommission_recovers_uncertain_cleanup_after_logout_and_store_reopen` | 2 |
| `AgentCore.Infrastructure.Tests.TenantBrowserOwnershipTests.Tenant_binding_is_immutable_and_unscoped_entry_points_cannot_adopt_read_reset_or_release_it` | 1 |
| `AgentCore.Infrastructure.Tests.TenantBrowserOwnershipTests.Unscoped_binding_or_command_reserves_its_namespace_and_cannot_be_promoted_to_tenant_ownership` | 1 |
| `AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Owned_bindings_share_only_same_tenant_credentials_preserve_archive_and_reopen_and_never_grant_use` | 2 |
| `AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Owned_credential_lifecycle_protects_values_and_never_reopens_global_authority` | 2 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Owned_definitions_publish_immutable_versions_and_reject_foreign_scopes_and_global_paths` | 2 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Tenant_Sandbox_worker_reopens_and_retires_a_stranded_approved_command_without_replay` | 2 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Tenant_experience_record_requires_exact_manage_Review_or_primary_only_grant` | 4 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Tenant_instance_children_preserve_dependencies_choices_bytes_and_reopen_without_global_access` | 2 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Tenant_instance_configuration_resolves_owned_resources_and_adopts_exact_versions_across_reopen` | 2 |
| `AgentCore.Infrastructure.Tests.TenantDefinitionIntegrationTests.Tenant_memory_operation_forget_requires_exact_Review_or_primary_only_grant_and_cannot_relearn_in_its_successor` | 4 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Approval_expiry_persists_a_terminal_result_resumes_once_and_only_a_new_root_can_request_again` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Deleted_Artifact_cleanup_pages_actual_same_Account_tenants_and_retries_one_failure_without_starving_neighbors` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Deleted_Artifact_cleanup_requires_exact_committed_owner_and_preserves_metadata_and_neighbor_bytes` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Execution_audit_store_fences_untrusted_lineage_and_stale_witnesses_without_mutating_the_actual_run` | 14 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Execution_authorization_audit_never_copies_unknown_permission_or_malformed_resource_payloads` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Governance_read_is_independent_bounded_coherent_and_revoked_without_policy_or_audit_mutation` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Ordinary_review_requires_the_current_underlying_human_permission_and_independence_without_creating_exceptional_grants` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Tenant_Artifact_source_refuses_foreign_same_Account_owners_mismatched_Sessions_and_nonconcrete_paths_without_creating_roots` | 2 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Tenant_http_completed_checkpoint_reopens_without_network_and_fresh_source_loss_blocks_body` | 4 |
| `AgentCore.Infrastructure.Tests.TenantOperationAuthorizationTests.Tenant_workspace_owner_addresses_reject_foreign_same_Account_resources_and_unowned_backing_scope_injection` | 2 |
| `AgentCore.Infrastructure.Tests.TenantRunAdmissionTests.Current_chat_and_session_read_are_independent_and_requester_is_not_the_session_creator` | 2 |
| `AgentCore.Infrastructure.Tests.TenantUserProfileTests.Tenant_user_Profile_has_actual_membership_ownership_independent_Account_tenants_and_no_implicit_seed` | 2 |

### API — 50 cases

| Selected method | Cases |
| --- | ---: |
| `AgentCore.Api.Tests.TenantAgentAccessTests.Tenant_model_Automation_exact_consent_resumes_once_or_truthfully_refuses_without_creating_work` | 8 |
| `AgentCore.Api.Tests.TenantAgentAccessTests.Tenant_model_Automation_list_filters_before_count_and_current_source_loss_blocks_later_output` | 2 |
| `AgentCore.Api.Tests.TenantAgentAccessTests.Tenant_model_Automation_mutations_create_inspect_edit_enable_disable_and_finish_through_the_real_Runtime_without_legacy_authority` | 2 |
| `AgentCore.Api.Tests.TenantAgentAccessTests.Tenant_model_Automation_semantic_denial_keeps_owned_state_unchanged_and_creates_no_effect` | 2 |
| `AgentCore.Api.Tests.TenantCredentialAccessTests.Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes` | 34 |
| `AgentCore.Api.Tests.TenantSessionAccessTests.Tenant_HTTP_cutover_removes_all_228_retired_routes_even_for_original_tokens_and_platform_operators` | 2 |

## Consolidation and acceptance limits

The current migration chain reproducibly creates one current relational model; repeated startup does not rewrite it. Deleting/squashing migration history solely to reduce file count would require an installed-database transition or a concretely authorized disposable reset. Neither is justified by this passing check, so this module preserves migration IDs and frozen SQL rather than adding adoption/fallback behavior. This assessment does not exclude later consolidation when a concrete safe case is proved.

The original goal remains active. These results qualify the named schema/startup and cross-family workflows. Complete method-level route/store/effect and indirect privilege closure, remaining current interface/document reconciliation, final security/isolation combinations and rendered UI/UX/design-system acceptance are still required. No unexecuted requirement is marked accepted.
