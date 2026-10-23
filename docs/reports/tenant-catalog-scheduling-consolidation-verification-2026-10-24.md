# Tenant catalog and scheduling consolidation

Date: 2026-10-24. Preimage: `1cb5af7f`. This qualifies the catalog/planner cleanup and current HTTP ingress boundaries; proposal-wide acceptance remains open.

## Result and source isolation fix

The generic `IAgentDefinitionStore` registration now aliases the immutable `IBuiltInAgentDefinitionStore`. The unused composite catalog is removed. Current tenant publications already resolve through `ITenantDefinitionStore` in `TenantAgentConfigurationService` and `TenantRunConfigurationResolver`; retained unowned publications never become a shared runtime catalog.

The audit also found an active source collision: `InheritedDefinitionResourceCatalog` started with retained unowned publication resources before adding approved built-in Knowledge. A matching built-in ID/version could therefore acquire an extra private resource, and a same-path row suppressed the approved Knowledge binding. Built-in inheritance now starts with the approved file source alone. Content addresses and deterministic built-in Resource IDs remain unchanged. No historical rows or bytes are deleted.

The both-store regression seeds actual retained publications at the **exact** built-in ID/version, with an extra private Reference and a conflicting approved Knowledge path. Physical native reads still prove those rows exist. Catalog exact/default/list results exclude the unowned definition. Inherited resources preserve their original approved hashes and identities, and an actual bootstrapped tenant Agent's current configuration excludes both private bindings. This is a source-selection fix, not a claim that a model could previously create those retired global rows through a current API.

The catalog's non-built-in branch remains a native retained-data reader. Current tenant call chains select owned GUID publications directly, or first resolve an exact immutable built-in before invoking this catalog. This was checked in both configuration resolvers, both stores' child-scope catalogs and both stores' management-resource readers. It is not an alternative tenant authoring or authorization path.

## Removed global helpers and preserved behavior

Removed fourteen unused lookup/admission/catalog methods across the composite catalog, `ExecutionModelAdmission`, the complete old `AutomationDestinationPolicy`, `StateIsolationAccess.ResolveAsync`, `InstanceAutomationPolicy.PlanAsync`, and the old global methods/denial DTOs in `TriggerDurableSchedulingPolicy`. Production call-site inspection found no current callers of these branches. `InstanceAutomationPolicy` no longer requires `ITriggerStore` or global `IMemoryStore` Profile services; its current tenant reconciliation body is unchanged.

Current model selection and capability checks, monotonic isolation intersection, definition-only scheduling eligibility and tenant Automation reconciliation remain with their existing owners. Three obsolete composite-catalog test declarations (six inherited store cases) are removed with the retired behavior; physical draft/publication/CAS/reopen tests remain. Two native unowned-registration tests now seed their own trigger store instead of relying on unrelated configuration-service construction. The canonical fixture constructors use the dependency-free planner. `NativeInstanceConfigurationSeed` explicitly reads exact retained unowned publication data in tests; it no longer relies on a production composite-catalog fallback.

No permission, approval default, wire contract, migration, historical preparation hash or provider adapter changes. The syntax audit found 548 declared methods across 93 store interfaces; inheritance is not counted twice. That census is navigation, not method-level authorization proof.

## Current HTTP ingress qualification

`Every_current_private_HTTP_route_refuses_original_tokens_and_every_tenant_route_refuses_foreign_humans_including_platform_operators` enumerates actual runtime endpoints on InMemory and SQLite:

- All **241 current private method/path pairs** have authorization metadata, no anonymous override, and return `401/not_authenticated` for both anonymous callers and the retired owner header.
- All **227 tenant method/path pairs** return `403/not_member` for an ordinary human from another real tenant and for an actual platform operator outside that tenant.
- **1,872 negative requests** across the two stores leave both tenants' revisions and audit counts unchanged and make zero outbound provider HTTP calls.

Setup uses actual Account registration and System operator HTTP assignment, fresh login, tenant creation, operator revocation and another fresh login. This avoids manufacturing a foreign tenant or assuming operator privileges imply membership. The five authentication and four purpose-bound webhook/OAuth pairs are outside this private ingress matrix; their dedicated proof contracts retain separate qualification. The existing exact 250-pair route manifest and all 228 retired routes are exercised separately.

These ingress checks establish authentication/membership rejection before endpoint binding. They do not replace per-operation IAM, source, approval, effect-race or wire tests.

## Focused verification

**152 distinct selected cases qualified**, with zero remaining failures or skips. Passing repeats are not added to that total.

| Group | Observed result |
| --- | --- |
| Current native catalog/resource collisions and DI | **4 passed** in 9s, including both store variants and dependency-free planner construction. |
| Retained physical stores, owned publication, current configuration/adoption/settings and isolation | **39 passed** in 1m22s. |
| Current owned Knowledge/child scopes and native publication Resource/Knowledge bytes | **21 passed** in 37s. |
| Shared model, native effective configuration and isolation policy | **26 passed** in 120ms. |
| Complete current private HTTP ingress | **2 passed** in 13s, covering the route counts above. |
| Canonical HTTP/model/worker and retired-route regressions | 44 selected: **40 passed** in the 1m57s initial group; four native retained-publication fixture failures are qualified by the follow-up below. |
| All callers of the explicit native retained-data fixture | **26 passed** in 35s; includes the four corrected failures, six earlier passing repeats and sixteen additional unowned execution/inspection/Skill boundary cases. |
| Final solution build | **Passed**, zero warnings/errors in 34.79s. The final test-only API rebuild passed, zero warnings/errors in 11.47s. |

The 39-case configuration group preceded the final built-in Resource source correction. Its passing cases were retained; the 21 Resource/Knowledge cases and final four-case collision group exercise that correction. After removing the production composite catalog, four native freeze/inheritance cases exposed their fixture's implicit dependence on it. The test-only exact retained-publication reader corrects those preconditions; all four fixture-caller classes then pass. The original forty passing API cases were retained, with only the six shared fixture-caller cases repeated. Early build failures identified two old fixture constructor calls; early ingress attempts failed in fixture setup (single-System-principal assumption, cross-Account self-service tenant creation, then display-name search). Final setup uses immutable Account IDs and canonical System HTTP commands. These failures do not qualify ingress and are superseded by the passing two-case run.

All commands use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. Application selects `ExecutionModelPolicyTests`, `NativeEffectiveConfigurationTests`, `StateIsolationPolicyTests`. Infrastructure selections are recorded by method/class above; native Resource fixtures are `PublicationKnowledgeRetrieveTests` and `DefinitionPublicationResourceWorkspaceTests`. The final native API follow-up selects `SelfManagementAuthorityTests`, `SelfManagementCompletionTests`, `SelfManagementRemediationTests` and `SelfManagementOperationTests`. API selects the new current-route method, existing retired-route method, owned author/publish/instantiate, owned configuration/persona/adoption, upload/evaluate/publish/read, fresh adopted-occurrence configuration, current Runtime policy, Event activation defaults, `SelfManagementAuthorityTests`, `SandboxApiTests`, and exact Knowledge approve/reject/restart.

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs are `/private/tmp/tenant-catalog-consolidation-*20261024*.log`; the final passing ingress log ends `qualified.log`, collision log ends `initial-infrastructure-20261024-final.log`. All 1,086 relative link targets in the changed Markdown files and whitespace checks pass. The preimage preserves retired declarations. Fixtures use Synthetic adapters, private temporary roots and physical SQLite reopen where selected. No product UI changed; this backend slice does not add rendered UI acceptance. No installed-data reset, Real-host restart, provider-key call, CI, push or PR was performed.

## Remaining proposal acceptance

The [store/effect inventory](tenant-store-effect-inventory-2026-10-24.md) and [delivery ledger](multitenant-iam-delivery-2026-10-16.md) retain the proposal-wide method/effect/authority reconciliation and final security/isolation/design acceptance. Previously completed scoped combinations remain qualified by their successor reports; this cleanup does not restart them or establish complete proposal acceptance.
