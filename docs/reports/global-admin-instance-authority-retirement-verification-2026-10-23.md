# Global Admin and Instance authority retirement

Date: 2026-10-23. Preimage: `b12de723`. Scoped continuation of the active tenant cutover; full proposal acceptance remains open.

## Result

Removed `AdminReadService`, `AdminHistoryService`, `AdminAgentInstanceService`, `AgentInstanceService`, `IAgentInstanceService` and their registrations. They had no canonical entry-path callers. Current inventory/create/archive/restore stays with `TenantAgentService`; configuration/persona/adoption stays with `TenantAgentConfigurationService`; deletion stays with `TenantAgentDecommissionService`. The actual tenant transaction, principal/source checks, resource revisions, safe audit and cleanup recovery remain at those existing owners.

The current tenant configuration read already uses pure `AdminEffectiveConfigurationResolver`; it remains unchanged. Its view records moved byte for byte from the global read service to `AdminConfigurationViews.cs`. Shared physical Instance/Skill/resource store contracts and records are unchanged outside the removed actor interface. No HTTP contract, permission/default, migration, protected payload or hash-bound historical preparation changed.

## Fixture boundary

Retired 26 obsolete global actor test methods: thirteen base managed-create/persona/adoption history methods; three lifecycle-history methods; two `AgentInstanceTests` methods; three global `AdminInstanceCreateTests` methods; five mixed global Instance/lifecycle-concurrency methods. Exact original bodies are in the preimage. Their global authoring, Admin operation-id replay and trusted-local admission are not the current product contract.

Preserved the separate native insert/persona/version history rollback and operation-lock checks, retained historical summary projection, pure overview tests, independent Definition/native concurrency tests, Skill version reconciliation/pinning and existing accepted-Run storage checks. Native fixtures seed unowned rows directly; they synthesize no tenant, principal, grant, consent or Run authority. Unowned policy/history/refusal fixtures also seed Instance metadata directly. This is native persistence/projection evidence, not replacement actor authorization.

Current tenant HTTP/Integration cases separately verify exact-source/default/custom persona creation, filtered inventory, version/persona CAS, archive/restore, reference and publication races, stale/foreign/suspended refusals, tombstones, profile cleanup uncertainty/restart and archived-owner decommission. Thus removing an old actor fixture does not turn native seeding into a tenant authority claim.

## Verification

**108 distinct passing focused cases**, zero runtime-test failures/skips:

| Group | Result |
| --- | --- |
| Initial affected HTTP use case | 4 passed in 11s: current inventory/create/archive/restore, persona/version and both-store decommission. Repeated in the API group below. |
| Application native/pure regressions | 27 passed in 23s: native overview/history projection, Skill activation/reconciliation/pinning, settings resolution and accepted-work store isolation. |
| Infrastructure native and canonical lifecycle | 49 passed in 1m27s: physical rollback/lock guarantees and actual owned lifecycle, reference races, publication/persona/adoption witnesses, safe decommission and recovery. |
| API lifecycle and native refusal/history | 32 passed in 45s: current HTTP lifecycle/deletion/decommission and preserved native policy/history/unowned-refusal checks. |
| Solution build | Final passing serial build: 5.30s, zero warnings/errors. Earlier build diagnostics caught an overbroad interface edit and a pure-fixture variable; shared port records were restored exactly and the fixture corrected before tests ran. |
| Static checks | Removed-symbol search across production/tests empty; view-record extraction and unchanged shared-port remainder verified; whitespace and changed-document local links pass. |

All tests use `dotnet test --no-build --no-restore --nologo` against the selected project. Application filters: `NativeEffectiveConfigurationTests`, `NativeInstanceHistoryProjectionTests`, `SkillActivationTests`, `InstanceSettingsResolverTests`, `ManagedInstanceP7DRegressionTests`. Infrastructure selects native Admin managed/persona/version history and remaining lifecycle concurrency plus `TenantAgentDecommissionTests`, owned configuration/persona/adoption/version-preview, tombstone and Definition deletion/reference-race cases. API selects the four `SelfManagement*Tests` native suites, `Creation_scoped_inventory_and_archive_restore_`, `Owned_instance_configuration_persona_and_version_adoption_`, `Tenant_Agent_decommission_` and `Definition_delete_http_`.

Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs/manifests: `/private/tmp/admin-instance-retirement-*20261023*`. All build/test handles are terminal. Temporary stores, profiles and keys are fixture-owned. No installed-data reset, Real-host restart, provider-key call, deployment, Docker run, CI, push or PR occurred.

## Remaining goal

Global Definition draft/resource/publication and remaining native configuration/tool/store roots still require caller reconciliation. Safe final schema consolidation and the previously recorded canonical long SSO, large-page discovery, reserved-cleanup journey and proposal-wide security/isolation/function/rendered UI acceptance remain open. Historical milestone freezes are unchanged.
