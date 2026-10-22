# Canonical Skill authoring — 2026-10-22

This module follows `32655e50` and integrates `skills.create`, `skills.update`, `skills.set_enabled`, `skills.reset_enabled`, `skills.delete` and `skills.customize` through the incumbent tenant management and local-effect owners. The original IAM goal remains active. Resource/Harness content authoring, private Skill activation, typed remedies/fresh configuration continuations, broader dependency/schema cleanup and proposal-wide acceptance remain open.

## Delivered boundary

`TenantAgentSkillPreparation` shares human and model semantic planning through the existing `AgentInstanceSkillService`. No new execution framework, permission or table is added. Current/admitted Skills delegation, capability and an actual conversational human root remain necessary. Scheduled self-authoring is outside this qualified path. Models cannot supply another tenant, principal, Instance or trusted lineage.

The primary action is `skills.manage`: tenant scope for create/customize, exact owned child otherwise. `agents.update` cannot substitute. Independent current parent `agents.read` and raw `skills.read` are mandatory; exact exceptional grants supply only the primary write. Protected source content under read Review needs its own exact saved-read consent first. Managed `SkillChanges` allows eligible create/update/choices under current Allowed policy; Assisted changes, delete, customize and stronger tenant policy require exact Review. Independent-review policy remains effective.

Identical content and choices leave parent/child revisions unchanged and create no request, effect or receipt. Built-in procedures remain immutable; choices cannot enable management or unfreeze it. Customize preserves source provenance and atomically disables the inherited choice. Bound Definition-resource requirements are rejected instead of silently lost. Current Runs retain admitted catalogs; newly authored content applies to future Runs and is not activated by a write.

Both stores revalidate exact child/configuration/source witnesses and current authority, then commit actual child/parent state, removed-child assignment retirement, Run effect, receipt, grant consumption and audit together. A fresh claim/lease check precedes mutation/save. Factual results contain the committed key, revision, enabled/deleted state and customization source choice. The immutable receipt contains bounded procedure-free evidence, not Skill procedure text.

Retained observations rebase only through an unbroken chain of the same task's exact receipts, with current raw reads. External changes invalidate the old observation. Deleted content requires current tenant-wide raw reads. Human detail and Inbox counts require current independent child read/manage authority before paging. Child-only history additionally binds the original Instance revision or its exact matched committed revision. Deleting/recreating/regranting a key cannot revive the old private proposal; a tenant-wide reader/manager can inspect the exact recorded deletion through its immutable receipt.

## Persistence

`20261022110000_TenantManagementSkillAuthoring` adds nullable bounded `ManagementSkillJson` to the existing operation receipt table. Frozen approval-table SQL preserves the installed 117 guards, refining the existing completed-management receipt guard to defer typed Skill evidence to its dedicated guard. Two new insert/update guards bring the current total to **119**. Existing indexes remain exact. Prepared Skill plans and procedure-free receipt evidence retain typed closed shapes and bounded fields.

Down refuses retained new Skill plans or evidence. A safe roundtrip restores all 117 prior trigger definitions and indexes exactly, then upgrades to the identical 119-guard schema. Current-model and reopen checks find no pending changes; `foreign_key_check` returns no violations. Historical migration execution files are unchanged; new target metadata uses the existing content-addressed frozen-resource mechanism.

## Executed checks

**422 distinct focused cases passed.** Repeated repair checks are counted once; the corrected settings case is included in the 260 adjacent regression identities. The checks use disposable Synthetic stores without provider credentials. Product source was unchanged between the final solution build and the final API-project build; the latter compiled additional tests and a migration-fixture correction.

| Check | Result |
| --- | --- |
| Serial solution build | Passed, zero warnings/errors. `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Log: `/private/tmp/tenant-skill-authoring-build-20261022-r12.log`. |
| Final API-project build | Passed, zero warnings/errors, same serial flags. Log: `/private/tmp/tenant-skill-authoring-build-20261022-r13.log`. |
| New Skill API/Runtime/store matrix | **58 distinct passed**, both stores: 56 grouped cases plus two read-Review-then-direct-write cases. Logs: `/private/tmp/tenant-skill-authoring-qualified-20261022.log`, `/private/tmp/tenant-skill-authoring-combined-20261022.log`. |
| Application contracts | **80 passed**: `ManagementSkillContractTests`, `ManagementMutationContractTests`, `ManagementChildReadContractTests`, `ManagementReadContractTests`, `ManagementInspectionContractTests`. Log: `/private/tmp/tenant-skill-authoring-app-qualified-20261022.log`. |
| Infrastructure/schema and incumbent child authoring | **24 passed**: new Skill and existing child-read/inspection/mutation/read migrations plus tenant child-scope/editor cases. Log: `/private/tmp/tenant-skill-authoring-infra-qualified-20261022.log`. |
| Corrected existing SQLite settings approval case | Passed in the three-case combined run above; the other two identities are included in the 58 new Skill cases. |
| Affected management API regression | **260 passed**, zero failures/skips, 18 minutes 14 seconds. Canonical management observations/commands, consent/source/revocation/restart boundaries, Core procedures and incumbent human child authoring. Log: `/private/tmp/tenant-skill-authoring-api-regression-20261022-r2.log`. |
| Documentation/Git | Changed file links, fences and staged whitespace checked; no missing targets or unmatched fences. |

Tests use `dotnet test <project> --no-build --no-restore --nologo --filter <filter>`. The new matrix filter is `FullyQualifiedName~Canonical_Skill_authoring_uses_actual_child_authority_exact_effects_and_historical_source_retention`. Application and infrastructure filters select the classes/families above. The final API regression filter is:

```text
FullyQualifiedName~Canonical_management|FullyQualifiedName~Management_read_|FullyQualifiedName~Management_command|FullyQualifiedName~Management_settings|FullyQualifiedName~Management_mutation_|FullyQualifiedName~Management_exact_|FullyQualifiedName~Management_Sandbox_|FullyQualifiedName~Tenant_Core_procedures|FullyQualifiedName~Owned_instance_skills_and_resources|FullyQualifiedName~Owned_instance_inherited_resource_copy|FullyQualifiedName~Item_scoped_child_grants|FullyQualifiedName~Child_editor_HTTP
```
 Repeated repair identities are counted once.

Observed scenarios include all six operations; direct, approve and reject outcomes; inherited/Built-in choices; unchanged results; stronger/independent policy; missing raw reads; child-only write authority; rejection of parent-only authority; primary-only exceptional grant consumption without permanent assignment; actual human private detail/Inbox filtering; completed deletion history; same-key recreation/regrant isolation; and source retention after exact own effects versus external changes. Assertions inspect actual child records, parent/child revisions, immutable receipts, grants and pinned Run catalogs. Read Review withholds protected procedure content until consent, then an eligible direct update succeeds while the old Run retains its original procedures.

## Repaired findings and limits

Grouped qualification exposed wrong create/customize consent scope, an approved path still demanding `agents.update`, nested SQLite ownership transactions, and historical detail checks that omitted the matched receipt on a second inspection. These were corrected and the affected identities passed. Scope-aware transaction checks use the store's already-owned security snapshot, avoiding a second SQLite ownership transaction while a writer is reserved. Fixtures now use actual inherited/Built-in revisions and exercise HTTP Inbox counts rather than an internal default-hidden collection.

An earlier adjacent regression run was interrupted after a transient SQLite connection-open error and an existing settings migration-probe failure. The connection case passed in isolation. The migration fixture had committed compatible earlier Down steps before a lower migration refused; continuing the current binary against that intermediate schema was invalid. It now restores the current schema and asserts the current model before resuming the pending task. The corrected settings case passed. The interrupted run is not represented as green.

No frontend source changed. Browser/UI checks, hosted providers, CI and proposal-wide or milestone acceptance were not run for this backend module. Existing presentation is reused; a final proposal-wide UI/security/isolation pass remains required. The current boundaries are tracked in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).
