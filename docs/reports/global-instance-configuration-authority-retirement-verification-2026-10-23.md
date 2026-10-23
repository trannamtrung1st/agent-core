# Global Instance configuration authority retirement

Date: 2026-10-23. Preimage: `2d8ef67d`. Scoped cutover evidence; the complete proposal remains open.

## Result and boundary

Removed global `AgentInstanceSettingsService`, `AgentInstanceSkillService`, `SelfManagementService` and `StateIsolationConfigurationService` and their registrations. Current tenant HTTP/model operations keep their owning `TenantAgentConfigurationService` and durable operation paths. They now call explicitly pure `InstanceSettingsRules`, `InstanceSkillRules` and `HarnessManagementRules`. Thirteen retained static methods/fields match their preimages byte for byte. Public DTOs retain their names, namespace and fields. Isolation DTOs moved unchanged; its existing safe audit section/field vocabulary moved to `IsolationConfigurationPlanner`.

The extracted rules read no stores, commit no state and grant no actor authority. Current principal checks, exact durable consent, source ownership, atomic security/resource CAS and output gates stay at their current canonical owners. No permission classification, approval default, migration, protection format or hash-bound historical preparation was changed.

Native tests were preserved rather than replaced with permissive tenant actors. Test-only unowned seeding calls the actual pure preparation/projection plus physical store CAS: native Skill budget/reconciliation/customization/pinning, inherited policy/defaults, freeze/reopen, immutable Built-in content, retained historical preparation bytes and unowned executor refusal. These helpers synthesize no tenant principal, role, consent, Run or claimed authority; they do not qualify canonical authoring. The selected real tenant HTTP/model cases provide that separate evidence.

Two mixed fixtures incorrectly expected canonical settings/version adoption to suspend native unowned Automation registrations merely because their Instance id matched. Current `PlanTenantAsync` excludes those rows. The fixtures retain stale/mixed transaction refusal and now assert that unowned status/revision remain unchanged. Actual owned suspension/restoration and exact model settings-section reconciliation are checked separately through canonical APIs on both stores.

## Verification

There are **203 distinct passing selected cases**, with zero remaining failures/skips in the final selected set:

| Group | Observed result |
| --- | --- |
| Initial affected use case | 3 passed: actual owned HTTP settings/budgets/model/policy/freeze, plus retained preparation-byte policy/freeze on both native stores. These also occur in the API group. |
| Native Skill/settings | 21 passed, including future Always-budget validation, content pinning, atomic customization, revision refusal, archive refusal and inheritance/reset. |
| Domain policy restrictions | 9 passed. |
| Canonical configuration/child state and version adoption | Initial groups: 12/14 and 6/8 passed; the four failures were the two mixed unowned Automation expectations on both stores. Final focused recheck: all four passed. All 22 selected final cases are qualified. |
| Canonical tenant HTTP/model management and native refusal/history | 129 passed in 8m13s. Exact Review/Grant, actual settings/Skill effects, current child/source authority, no-effect denials, Built-in restrictions, inheritance/freeze and coordinated isolation. |
| Additional actual Automation/settings-section reconciliation | 22 passed in 1m45s: both-store HTTP owned policy suspension/restoration and 20 model cases across ten settings sections. Named TRX results confirm both `policy-change` cases actually ran. |
| Final native fixture recheck | 3 passed in 8s; repeats the two Built-in choice/inheritance cases and lazy immutable-procedure case after restricting the fixture seeder to the one exercised Built-in. |
| Serial solution build | Initial 54.15s; final 26.04s. Both zero warnings/errors. |
| Static review | Thirteen extracted members match exact preimages; 16 canonical consumers changed only type names. Retired production/test symbol search, whitespace and four changed-document local links pass. |

Checks use `dotnet test --no-build --no-restore --nologo` against the selected project. Application selects `SkillActivationTests` and `InstanceSettingsResolverTests`; Domain selects `ManagementPolicyRestrictionTests`. Infrastructure selects `Tenant_instance_sparse_settings_`, `Tenant_agent_cannot_enable_or_unfreeze_`, `Tenant_settings_mutation_`, `Tenant_instance_children_`, `Tenant_instance_child_`, `Tenant_version_preview_`, `Tenant_instance_configuration_resolves_`, `Tenant_persona_cas_` and `Tenant_adoption_rechecks_`.

API selects the four `SelfManagement*Tests` classes, owned Instance settings/configuration, `Tenant_isolation_`, `Canonical_management_commands_`, `Canonical_Skill_authoring_`, `Management_commands_with_missing_authority_` and `Management_exact_exceptional_grant_`. The additional filter is `(FullyQualifiedName~Tenant_automations_use_current_human_scope_&DisplayName~policy-change)|FullyQualifiedName~Management_settings_sections_`. Final repeats select only the four corrected Infrastructure cases and the three affected native API cases.

The final build is `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs and preimage/member manifests use `/private/tmp/instance-configuration-retirement-*20261023*`; named results are `/private/tmp/instance-configuration-retirement-results-20261023/configuration-automation.trx`. All build/test handles are terminal. Temporary databases and credentials are fixture-owned. No installed-data reset, Real-host restart, deployment, provider-key call, Docker run, CI, push or PR is part of this module.

## Remaining goal

The full cutover is not accepted. Remaining roots include global Admin inventory/history/Instance lifecycle and other configuration/resource actors, followed by the final tool/store/schema reconciliation. The previously recorded canonical long SSO, large-page discovery, reserved-cleanup journey and proposal-wide security/isolation/function/rendered UI acceptance still require evidence. Historical freeze reports remain scoped to their original candidates.
