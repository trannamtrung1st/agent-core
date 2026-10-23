# Global resource, workspace and configuration-reader authority retirement

Date: 2026-10-23. Preimage: `4d828aa6`. Scoped cutover evidence; full proposal acceptance remains open.

## Result

Removed `AgentInstanceResourceService`, `AgentInstanceWorkspaceService`, `AgentRunConfigurationResolver` and `ComposerReferenceService`, including registrations. Current tenant configuration and protected file/Resource operations keep their owning stores, current principal/Run/source checks and durable consent/effect boundaries. `InheritedDefinitionResourceCatalog` and native byte mechanisms remain because current tenant resolution uses them.

Extracted eight existing pure members into `InstanceResourceRules` and `ComposerInputRules`; their bodies match exact preimages. Resource paths/media, hash/length, quotas/dependencies, origin-qualified views and selected/Always Skill limits retain their previous semantics. Infrastructure still uses the shared hash/collection validators. `ResolvedAgentRunConfiguration` moved unchanged into its own file and remains the current tenant resolver's value record.

The shared Runtime no longer accepts an optional global Composer reader or branches into its store-reading input validation/pinning. Current tenant construction supplied no such reader. The existing behavior is preserved: generic Resource-reference message parts remain unavailable, and pure selected-Skill validation supplies the existing input pin. Current claimed tenant configuration still resolves through `TenantRunConfigurationResolver`/`TenantToolAuthorization`; native immutable-Definition fallback grants no tool authority. No permission/default, migration, protected bytes, preparation hash or current HTTP shape changed.

## Fixture boundary

Removed the unused `OwnedWorkspaces` helper and the one `PublicDownloadRuntimeTests` method (two retry cases), whose Profile-only Runtime/global file facade did not supply current tenant/Run authority. Native policy/unowned-refusal fixtures now read only their explicitly unowned metadata through a small test helper and inspect raw native resource state after denied effects. They synthesize no tenant, grant, consent or execution authority. Current protected workspace and private-Skill model cases provide independent actual tenant evidence.

The old public download→edit→export sequence is not claimed as qualified by package/export or uploaded-file tests. Its full canonical successor remains a required final integration combination, alongside the previously recorded long SSO, large-page discovery and cleanup-budget journeys. Generic Composer Resource-reference behavior also needs explicit final requirement reconciliation; removing a dead global reader does not establish feature acceptance.

## Verification

**125 distinct passing selected cases**, zero runtime-test failures/skips. All build/test handles are terminal. Initial actual-use-case results: 8 tenant Instance-child transaction/content/authority cases passed in 22s; 10 model/HTTP home/working read cases passed in 44s. The latter check Allowed, missing authority, exact Review and disabled Home policy on both stores.

Additional Application cases: 21 passed in 16s (Skill activation/selection and shared Runtime completion behavior). Additional Infrastructure cases: 30 passed in 1m09s (resource no-change/path conflicts, byte-I/O authority races, exact grant retirement, foreign references and protected downloads). The additional API group passed all 56 cases in 2m57s, including current private-Skill independent reads/consent/source fences, one-time protected workspace mutations and unowned refusal cases. The serial solution build passed in 53.69s with zero warnings/errors. Exact pure-member/record checks and retired-symbol search pass.

All checks use `dotnet test --no-build --no-restore --nologo` against the selected project. Initial API selects `Tenant_workspace_model_HTTP_journey_`; initial Infrastructure selects `Tenant_instance_children_preserve_` and the three `Tenant_instance_child_` transaction/content/agent-write families. Application selects `SkillActivationTests` and `SessionRuntimeCompletionTests`. Additional Infrastructure selects the three Resource preparation families and three Definition-resource grant/download families. Additional API selects `Canonical_private_Skills_load_`, `Tenant_workspace_model_mutations_HTTP_` and the native `SelfManagementOperationTests`/`SelfManagementCompletionTests`/`SelfManagementRemediationTests` refusal suites.

Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs/manifests use `/private/tmp/resource-workspace-retirement-*20261023*`. Temporary stores/keys are fixture-owned. No installed-data reset, Real-host restart, provider-key call, deployment, Docker run, CI, push or PR is part of this module.

## Remaining goal

Reconcile the remaining native/store/tool callers and safe final schema consolidation, complete the explicit canonical integration combinations above, then perform the proposal-wide security/isolation/function/rendered UI acceptance audit. Historical milestone freezes remain unchanged.

## Composer successor decision — 2026-10-24

The user resolved the open specification conflict in favor of restoring the existing reference feature through tenant IAM. The [tenant Composer successor](tenant-composer-references-verification-2026-10-24.md) now restores all six typed categories and bounded source content under independent current human/executor reads. It does not restore the retired Profile owner, generic resolver or owner endpoint. The earlier qualification above remains evidence for the retirement itself.
