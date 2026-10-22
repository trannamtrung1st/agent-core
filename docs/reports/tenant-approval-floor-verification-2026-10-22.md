# Core approval-floor audit — 2026-10-22

This module follows `11996917` and qualifies the current Core permission catalog and actual human/no-human authorization decisions before further Harness authoring integration. The audit found no central default that needed weakening: genuinely admitted independent tasks already proceed under requester-based policy when the executor has authority. Sensitive effects, explicit tenant review and human-only reserves retain their existing controls. The complete IAM goal remains active.

## Explicit, unchanged policy

`PermissionCatalog` now declares each action's risk explicitly. The previous implementation inferred it from action/family names. All **77** existing keys, ordering, resource families, principal flags and classifications remain identical; no role, tenant policy, transport contract or database schema changes. New catalog entries must choose a risk class explicitly.

| Core class | Entries | Minimum | Independent review by default |
| --- | ---: | --- | --- |
| ReadOnly | 24 | None | No |
| Mutation | 36 | WhenRequesterLacksPermission | No |
| Sensitive | 17 | Always | No |

Fourteen permissions remain human-only, including the five System-only permissions. Twenty permissions remain nondelegatable: those fourteen plus Browser/Sandbox read/use/control permissions. Operational built-in roles contain no human-only or System-only permission. Scope aliases and role composition remain unchanged.

The 17 Sensitive entries remain Agent/Definition/Session/Resource/Automation/Workspace/Artifact/Attachment/Drive deletion, Credential bind/use/manage, Email send, Browser use/manage and Sandbox execute/manage. Their `Always` minimum cannot be reduced to requester-based policy. This preserves protection for destructive, secret-use, native and external effects rather than treating IAM possession alone as consent.

Current policy semantics remain:

- With executor authority, None allows ordinary eligibility independently of a requester's action permission. Requester-based policy reviews an actual human who lacks that permission; it does not fabricate a denied human for an independently admitted Schedule/Event root.
- Always requires ordinary Review even with no human initiator. Independence qualifies a required reviewer; it does not change an otherwise Allowed requester-based operation into Always.
- Missing executor authority is never supplied by requester roles. Only a delegatable operation with a qualifying human can return exact ExceptionalGrant eligibility. Inspection creates no request, grant or consent. Nondelegatable and human-only operations remain hard denials.
- Current requester communication/source rights, executor roles, reviewer underlying authority and tenant restrictions are rechecked. Tenant governance cannot weaken Core floors. Additional operation/source/capability/host checks remain independently mandatory.

The existing explicit Review for general control-plane management changes remains intentional; the three permitted Managed conversation preferences keep their separate direct-policy exception. Source-read Review, exact sensitive native consent and semantic/intent gates retain their existing owners. Unconverted Skill/Resource/Harness authoring and private activation must preserve the appropriate Managed choices and current operation policy when integrated; this audit does not authorize a blanket Review requirement or relaxation for those future conversions.

## Executed behavior

The new integration matrix creates actual conversational Owner and lower-privilege requester Runs, or an independently admitted Schedule occurrence/Run, through canonical HTTP/Automation services. It does not inject a nullable requester into a fabricated execution context. Both stores retain the actual executor and source lineage. The scheduled Run has no human initiator; its registration author is not adopted as one.

Across those six flows, expected and observed outcomes match the decision table: ordinary reads and authorized mutations proceed under applicable defaults; a lower-privilege human triggers requester-based Review; a missing human does not. Always reviews all three origins. Independent review with no qualified second human denies the Owner's own request, a genuinely qualified reviewer makes it reviewable, and revoking that reviewer's underlying action permission removes eligibility again. Missing executor update authority returns ExceptionalGrant classification, Browser authority cannot be delegated, IAM privilege assignment hard-denies, and sensitive deletion/Sandbox execution still review. An attempted weaker deletion policy returns 400 without changing the tenant revision. Revoking the actual lower-privilege requester's Chat access makes its retained Run unavailable. Eligibility inspection creates zero approval rows.

Existing autonomous file journeys additionally execute real Synthetic owned file writes/reads under actual scheduled/manual roots. Allowed operations complete without a fabricated human or approval; policy-required Review and exact grants use their existing successors/receipts. Rejection, missing raw reads and revoked write authority withhold the effect. Original task/source identity, no duplicate effect and zero outbound provider calls remain asserted.

## Checks

**117 distinct focused cases passed.** The serial solution build and final affected API-project build passed with zero warnings/errors.

| Check | Result |
| --- | --- |
| `PermissionApprovalFloorTests` | **78 passed**: all 77 exact risk/scope/principal entries and their minimums, plus complete ordered catalog closure and operational-role reserves. |
| Actual Owner/requester/scheduled matrix | **6 passed**, both stores, covering the complete sequence above. The first run passed 4 and failed 2 because the lower-privilege fixture lacked the independent Session-creation permission. Adding only that admission permission repaired the fixture while keeping Agent read/update authority absent. |
| Actual autonomous file effects | **24 passed**: both stores × manual/scheduled × allowed/review/grant/reject/missing-read/revoked-write. |
| Canonical governance HTTP | **1 passed**: current human governance, Core-floor weakening refusal, CSRF, optimistic revision, metadata, tenant isolation and rejected legacy authority. |
| Existing authorization/governance integration | **8 passed**: requester/executor independence, current role revocation, qualified independent reviewers, bounded governance and closed tool authority, both stores. |
| Metadata preservation | Independent pre/post comparison confirms all 77 ordered metadata entries are identical. |
| Documentation/Git | Changed links, anchors, Markdown fences and staged whitespace checked. |

Commands:

```text
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~PermissionApprovalFloorTests
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Canonical_approval_floors_preserve|FullyQualifiedName~Independent_Automation_files_use_actual_executor_exact_consent_and_same_task_successors|FullyQualifiedName~Canonical_operation_policy_HTTP_uses_human_governance'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Actual_requester_action_authority_changes_review_policy|FullyQualifiedName~Stricter_policy_persists_and_independent_review|FullyQualifiedName~Governance_read_is_independent_bounded_coherent|FullyQualifiedName~Shared_tool_boundary_uses_actual_executor_roles'
```

The qualified API group passed **31**, and Infrastructure passed **8**. Counts exclude repeated successful identities from the initial matrix run. Test hosts and databases were disposable; no user database or external service was changed.

This is current catalog/policy qualification, not proposal-wide acceptance. No frontend source or served permission metadata changed, so browser/frontend suites were not repeated. CI, hosted providers, remaining Harness authoring/private Skill activation, dependency/schema cleanup and the final exhaustive security/isolation/UI pass remain outside this module. Read policy None is not a universal free-text disclosure filter; current raw resource/audience gates and least-privilege Agent roles retain their existing responsibility. Remaining work stays recorded in the [delivery ledger](multitenant-iam-delivery-2026-10-16.md).
