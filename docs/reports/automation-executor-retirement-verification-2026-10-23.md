# Generic Automation executor retirement — 2026-10-23

This follows [canonical Automation integration](canonical-automation-integration-verification-2026-10-23.md). It retires the generic Automation execution/preparation pipeline while preserving canonical tenant reads, authoring, exact consent and actual-human manual invocation. The [original goal](multitenant-iam-delivery-2026-10-16.md) remains active.

## Removed authority

- Removed `TriggerScheduleCommands.ExecuteAsync`, its create/list/update/activation/cancel helpers, local `PrepareAsync`/`AutomationCommandPreparation`, caller `TriggerCommandContext`/classification and their verification/action-hash reuse.
- Removed the executor's Automation switch/inspection/activation handlers, local preparation methods, Runtime preparation branch and Profile-based schedule attach lookup. Removed four direct executor dependencies/factory arguments: `IAutomationService`, `ITriggerCommandAuthorizer`, `AdminAutomationAuthoringService` and `ICoreEventStore`. The canonical semantic verifier registration remains.
- Unowned Automation offering and execution deny all eight command/read names despite explicit Definition allowlists and fabricated Managed delegation/Instance/Run/Session context. Actual tenant dispatch retains its current ownership/claim/permission/source fences before the generic refusal.
- Removed the now-unused legacy preparation guard facade from `TenantToolAuthorization` and the executor. The retained tenant boundary test invokes actual refused execution rather than calling a removed preview.
- Pruned only parser methods outside the canonical `NormalizeTenant` dependency closure. Shared one-shot/relative/calendar/fixed-interval, timezone, child-trigger, destination and delivery normalization remains. Helpers now receive Core-bound reference time/Session directly; no Profile owner or supplied verification record enters parsing.
- Retired executor-only and transitive local Harness tests. Preserved unrelated Artifact behavior, source policy/recovery, template immutability, result/referent projection and timing checks. Time-resolution tests now exercise canonical pure normalization, and the Secretary fixture retains its actual owned Artifact workflow.

## Guidance and canonical clarification

Automation descriptions now distinguish current human requests and actual admitted-task intent. `automation.run` remains actual-human only. Scheduling procedure guidance uses qualified human saved-action review; unowned contexts receive no Scheduling procedure/clock evidence.

Migration of the old timezone-clarification test exposed a canonical gap: the verifier received no prior conversation evidence. The replacement uses the current owning store after raw checks to read at most eight prior saved rows, before the actual root input entries. Application rules retain prompt/nonstreaming turns, clamp assistant text to received/heard content and bound JSON to 12,000 characters. The evidence is explicitly untrusted and does not replace current intent, IAM or exact consent. Independent tasks receive none.

The result is a transient `TenantAutomationMutationObservation` property passed only to the existing semantic classifier. No transcript is added to prepared actions, exact hashes, grants, public DTOs or durable schema. Store metadata remains transcript-free. No new worker, framework, permission, approval default, migration or global authority fallback is introduced.

Eight real canonical HTTP/worker/store cases cover allow/deny after a Vietnamese timezone clarification, disabled creation retaining the exact verified Boolean, and missing timezone refusal before classification, with both stores. They verify current input stays separate from history, unseen assistant prose/current input are excluded from evidence, historical context cannot override a Deny result, and no unwanted Automation is stored.

## Verification

| Check | Observed result |
| --- | --- |
| Serial solution build: `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | Final build passes, zero warnings/errors, 56.45 s. |
| Application registry/policy, pure timing/semantics, referent projection, semantic verifier, source policy and retained Artifact | 109 distinct cases qualified: 108 initially pass; the clock fixture's missing verified requester facts are corrected and that one case passes its recheck. Other tested paths are unchanged by the subsequent canonical-only history read repair. |
| Infrastructure actual tenant sensitive dispatch boundary | 2 pass, both stores; no legacy draft read and no generic Automation fallback. |
| First current tenant authoring selection | 4 pass: complete current-user CRUD/activation flow and independent admitted-task authoring across both stores. The later canonical group includes these identities again after the history repair. |
| Migrated canonical clarification/disabled/timezone module | 8 pass after the bounded owning-history repair. |
| Canonical model read/authoring/consent/replay/deletion/manual-invocation group | 73 pass: current/independent reads, create/update/activation/delete, raw read and exact consent/recovery, truthful replay and actual-human manual invocation. |

**192 distinct focused backend cases are qualified.** Initial current-authoring cases overlap the final canonical group and are counted once.

Tests used `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`. Exact selectors:

```text
Application: FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~TriggerScheduleTimeResolutionTests|FullyQualifiedName~TriggerScheduleSemanticsTests|FullyQualifiedName~TriggerScheduleContinuationTests|FullyQualifiedName~TriggerCommandAuthorizerTests|FullyQualifiedName~Secretary_non_store_turn_creates_an_owned_artifact|FullyQualifiedName~TriggerDurablePolicyTests
Clock repair: FullyQualifiedName~Scheduling_context_includes_trusted_clock
Infrastructure: FullyQualifiedName~Tenant_sensitive_dispatch_cannot_read_a_legacy_draft
First canonical: FullyQualifiedName~Tenant_model_Automation_mutations_create_inspect_edit_enable_disable|FullyQualifiedName~Tenant_independent_Automation_authors_a_new_disabled_task
Clarification: FullyQualifiedName~Canonical_timezone_clarification|FullyQualifiedName~Canonical_disabled_creation|FullyQualifiedName~Canonical_missing_timezone
Canonical group: FullyQualifiedName~Tenant_model_Automation_|FullyQualifiedName~Tenant_model_manual_Automation_|FullyQualifiedName~Tenant_independent_Automation_authors|FullyQualifiedName~Tenant_independent_Automation_metadata_read|FullyQualifiedName~Tenant_model_existing_Automation_deletion|FullyQualifiedName~Tenant_Automation_direct_replay
```

Logs: `/private/tmp/automation-executor-retirement-{build,build-r2,build-r3,build-r4,build-r5,application,clock,boundary,api-first,clarification,clarification-r2,canonical}-20261023.log` (separate files). The initial clarification run had four passing cases and four null-history failures; all eight pass after the store read repair. Compilation also corrected the timing-result namespace. No passing broad group is claimed for an unfinished or failed run.

Roslyn retained the canonical parser call closure and removed reviewed obsolete method bodies without text-range guessing. Review manifests are `/private/tmp/automation-retirement-parser-inventory.txt`, `/private/tmp/automation-retirement-obsolete-tests-20261023.txt` and `/private/tmp/automation-retirement-removed-tests-20261023.txt`; transitive executor fixtures were pruned while preserving their unrelated methods/helpers. Current source scanning finds no removed preparation/context/execution APIs or executor dependency references.

## Remaining scope

Other global/internal store and service assumptions, historical fixtures not using this removed execution pipeline, final tool-transport/store authority inventory, inert Harness preparation hash/schema retirement and proposal-wide security, isolation, functional and UI acceptance remain open. Internal global helper registrations are not claimed retired by removing their tool injection. This backend module changes no UI/wire contract and claims no new browser/UI acceptance. All state is disposable Synthetic. No CI, hosted provider, user-data reset, Real restart, push or deployment was performed.
