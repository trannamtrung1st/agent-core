# Tenant management eligibility and Core procedures — 2026-10-22

This module qualifies canonical live Self-Management eligibility and immutable Core procedure activation on top of `8b4355ea`. The original IAM proposal remains active. Protected Harness/configuration observations, authoring, private Skill loading and typed remediation are separate remaining work.

## Implemented boundary

`ITenantRunContext.CurrentHarnessContextAsync` uses the incumbent tenant Run authority and configuration resolver. It resolves the actual stored Run, current claim/lease, requester lineage, tenant executor and selected immutable publication. Security/Run changes discard the observation and permit one fresh read; persistent change fails closed. Disabled admission cannot acquire management by a later edit. Mode, scopes, auto-apply, source/tool eligibility and Built-in choices intersect the admission and current policy; either freeze wins.

Initial context, model continuations, mailbox capability discovery, Skill admission and execution-policy context use the canonical path for tenant owners. Canonical projection also narrows the management policy used for preference offering and authorization schemas. The legacy Instance reader continues hiding tenant Instances; it supplies no fallback. The restriction is confined to canonical tenant projection, preserving existing legacy fixture semantics.

Conversational `skills.load` offers only exact immutable Core Built-in keys already in the Run catalog and still delegated by current policy. This contextual operation uses the existing mailbox, revisioned Run command, activation budget and accounting. It accepts no private Instance/Definition Skill selector. Duplicate argument fields and wider selectors are rejected. Loading a procedure grants no IAM, child-content read, consent or management effect. Unconverted Harness/configuration tools remain unavailable. No database migration, permission or execution owner was added.

## Executed workflows

The new 16-case matrix uses authenticated tenant HTTP ingress, the actual Synthetic worker/Runtime and InMemory/SQLite stores. A human creates General Assistant v32, delegates management and grants the executor only scoped `agents.read`, without private Skill reads. The model loads the exact Harness Core procedure, inspects its next typed context and finishes through persisted HTTP `/messages` history. Expected and observed activation counts, keys and Core sections agree.

Both stores exercise unchanged delegation, freeze before loading, freeze after activation, disabling the Built-in after activation, removing its scope, changing Managed to Assisted, adding Settings after admission and submitting a private Skill key. Freeze-before-load/private-key attempts activate nothing. Later revocation removes the loaded procedure from model context while preserving factual activation history. Assisted removes direct auto-apply offering; scope expansion cannot add it to the existing Run. Admission configuration/policy pins stay unchanged. Completed Runs cannot reacquire management context. No approval or configuration mutation is created by procedure loading.

## Focused local results

178 distinct cases passed across grouped checks and focused repair rechecks; one explicitly opt-in hosted case was skipped. These totals are a union of case identities, not one final all-suite command.

| Boundary | Evidence |
| --- | --- |
| API | 74 distinct passing cases. Initial grouped run: 69 passed, five legacy Harness failures. After confining the restriction to canonical projection, the affected 35-case group passed: all 16 new tenant cases, six direct preference cases and the complete selected Harness Chat class. The other grouped cases cover exact preference consent, policy authority, operation owners and typed continuation. |
| Application | 91 distinct passing cases; one hosted-only skip. Initial group: 90 passed and an obsolete exact tool-set assertion failed. That assertion omitted the eight already implemented Automation tools. The repaired assertion and all 13 new projection cases passed together (14/14). Other passing cases cover restrictive access, capability discovery and dynamic Skill/runtime activation. |
| Domain | 9/9 management policy intersection cases passed. |
| Infrastructure | 4/4 Skill pin/CAS/reopen cases passed. |
| Build | Final solution build succeeded with zero warnings and zero errors. |

Commands used `dotnet test <project> --no-build --no-restore --nologo --filter <focused-filter> --logger 'console;verbosity=normal'`. API grouped filters selected the new `Tenant_Core_procedures_use_claimed_policy` matrix, direct/approved preference workflows and `SelfManagementAuthorityTests`, `SelfManagementOperationTests`, `SelfManagementContinuationTests`, `HarnessChatAuthoringTests`. The repair filter retained the new matrix, direct preferences and Harness Chat. Application grouped filters selected `ManagementProjectionTests`, `StateIsolationPolicyTests`, `CapabilityProjectionTests`, `DynamicSkillActivationTests`, `SkillActivationTests`, `MessagingSkillJourneyTests`; the repair filter selected the new projection class and the exact converted-tool inventory case. Domain and Infrastructure selected `ManagementPolicyRestrictionTests` and `SkillPinStoreTests`.

Final build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.

Local logs: `/private/tmp/tenant-management-context-{api,application,domain,infrastructure}-grouped-20261022.log`, `/private/tmp/tenant-management-context-{api,application}-qualified-20261022.log`, `/private/tmp/tenant-management-context-qualified-final-build-20261022.log`. Initial checks also corrected the contextual-tool eligibility gate and new fixture import/property/history-route mistakes before qualification.

## Remaining acceptance

This backend module has actual Runtime/store/HTTP evidence. It changes no frontend presentation; browser and hosted-provider acceptance were not run. CI was not requested. It does not accept the whole Harness/Self-Management family, autonomous approval defaults, legacy/schema consolidation, the boundary inventory or the original proposal-wide security/isolation/functional/UI matrix. Current remaining work belongs to the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).
