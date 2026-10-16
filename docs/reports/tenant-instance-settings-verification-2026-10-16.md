# Tenant Instance settings verification — 2026-10-16

This continues `8fcf8d72` toward the full tenant/IAM/approval proposal. Canonical Instance field settings, budgets, unattended model and self-management/freeze now use the actual owning Instance and current IAM. The full goal remains active; this is not full resource/runtime/UI or milestone acceptance.

## Implemented behavior

The existing tenant-agent transaction accepts exactly one persona, publication, settings, budget, model or management change. It commits parent/security revisions and audit together. Settings carry their observed Automation policy plan into that same boundary. Mixed commands, stale witnesses and failed reconciliation leave no partial state. Existing parent columns/JSON are reused; no schema migration or parallel store was added.

Canonical settings read the pinned owned publication or shipped builtin and reuse the incumbent field defaults, effective values, sparse overrides, sources and constraints. Patch uses explicit `set`/`clear`, configured model/provider choices, secret-token rejection, browser host restrictions and Definition limits. Clearing restores inheritance. Unknown/conflicting fields, capability expansion, disabled safety choices and unconfigured providers fail without saving. Tenant Sandbox patches deliberately fail `resource_unavailable`; a Definition's configured profile ID is not proof of ownership of an unconverted catalog entry. No global Sandbox lookup was added.

Budgets retain existing per-class inheritance, validated presets and system ceilings; null resets overrides. Unattended model selection uses the configured catalog/reasoning rules and supports reset. Management views and preparation/freeze logic are shared with existing services. Server-resolved principal kind controls agent restrictions: an agent cannot enable/unfreeze management and needs its existing management area to edit policy, while a currently authorized agent can stop active management without that policy-editing area. Freeze preserves independent choices and cancels unpublished preparation/pending decisions. These settings grant no IAM permission. Canonical effective configuration now includes the resolved local management policy.

InMemory snapshots deep-copy nested management state on storage and reads. Mutating submitted or returned collections cannot change saved policy without a command/revision; SQLite serialization provides the same independent state boundary.

## Observed workflows

| Use case | Expected and observed outcome |
| --- | --- |
| Owned publication/Instance → field patch → clear | Canonical HTTP and both stores save the local instructions, preserve immutable publication defaults, report Instance source and restore exact inherited instructions on clear |
| Invalid field/provider/capability/safety choice | HTTP rejects unknown/conflicting fields, unknown provider, unauthorized capability and disabling required unsupported-claim protection; stale revision does not overwrite the saved candidate |
| Budget/model set and reset | Valid bounded budget reports Instance source; invalid ceiling fails; reset restores inheritance. Configured unattended model persists, unknown model rejects and clear removes the override |
| Management → freeze → reopen/reset | Independent Built-in choices and areas persist; freeze keeps them, effective configuration reports Frozen and SQLite recreation retains settings/budget/model/policy. Clearing overrides restores Definition defaults |
| Submitted/returned collection mutation | After save, mutate both the caller's scopes array and the inspected view; subsequent reads still show the original saved areas on both stores |
| Own agent authority | Scoped Contributor agent cannot enable/unfreeze its management; authorized human enables it; the agent can freeze without ManagementPolicy area; revoking its role blocks the next mutation |
| Reader/foreign tenant/revocation/CSRF | Resource-only Reader can inspect only its granted Instance and cannot change any new policy family. Foreign reads deny; revocation denies subsequent inspection; missing CSRF rejects mutation |
| Atomic settings/reconciliation | Mixed command and stale Automation plan leave settings, parent/security revisions and audit unchanged. A real persisted-profile Automation is suspended in the successful trigger-policy patch transaction |

## Focused verification

| Check | Final result |
| --- | --- |
| API: `TenantDefinitionAccessTests|TenantAgentAccessTests|SelfManagementAuthorityTests|SelfManagementOperationTests|StateIsolationDefinitionInheritanceTests` | 29 passed, zero skipped |
| Infrastructure: `IamIdentityIntegrationTests|Tenant_instance|Tenant_agent_cannot_enable|Tenant_settings_mutation_rejects|Tenant_persona_cas|Tenant_adoption_rechecks|AgentInstanceStoreContractTests|AdminInstanceDefinitionVersionHistoryTests|AdminInstancePersonaHistoryTests|AdminInstanceLifecycleHistoryTests` | 69 passed, zero skipped; includes the six new both-store settings cases and affected parent/history/IAM consumers |
| Final actual settings reconciliation assertions | 2 passed on both stores; overlapping rerun, not additional unique cases |
| Application: `InstanceSettingsResolverTests` | 6 passed, zero skipped |
| API build | Passed, zero warnings/errors; affected subsequent source also compiled in the test runs |
| Documentation hygiene | Changed-file local links/fragments, fences, complete JSON examples and whitespace checked before commit |

Commands use `dotnet test <project> --filter '<selection>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. Projects are `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`, `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` and `tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj`; each selection is listed above. Local socket execution used approved sandbox escalation. The representative new HTTP journey ran before expanding regression scope.

The initial HTTP fixture read the wrong authoring-model property (`catalogKey` instead of `key`); its corrected journey passed. The broader API run initially had five startup failures in older SQLite fixtures, before their affected behavior ran. Harness fixtures overrode content stores to SQLite while late-bound options left identity in InMemory; durable fixtures also lacked explicit identity bootstrap. `TestSqliteIdentity` now explicitly selects matching SQLite identity, migrates the fixture's disposable target and bootstraps known test credentials before app startup. The full selected API rerun passed, including actual SQLite reopen. Production registration/startup was not weakened and still requires deliberate SQLite bootstrap. No mixed-provider compatibility branch was added.

## Remaining scope

No frontend source changed, so no new browser or visual acceptance is claimed. Verification uses Synthetic HTTP/application services and temporary SQLite/InMemory state without providers or Sandbox workloads. Existing user data, Real hosts and credentials were preserved; no deployment/restart, live reset or CI was performed.

Tenant Sandbox catalog/selection, local Skills/resources/decommission, complete Instance/editor UI and resource preview/access panels, Session/Run/Hub/tool/background tenancy, trusted requester/executor/root lineage, durable approval authority, obsolete global authority removal and final schema/reset consolidation remain required. The [full delivery ledger](multitenant-iam-delivery-2026-10-16.md) retains those obligations. Configuration tests do not prove protected execution or full tenant isolation.
