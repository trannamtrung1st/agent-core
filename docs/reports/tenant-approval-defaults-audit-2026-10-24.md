# Tenant approval-default catalog audit — 2026-10-24

This report extends the [77-permission approval-floor qualification](tenant-approval-floor-verification-2026-10-22.md) to the current 78-permission catalog, including the subsequently added explicit HTTP permission. It audits the closed catalog and its Core review minimums. It does not lower policy, add a permission or authorize a provider effect. Operation-specific consent/capability/source rules and proposal-wide acceptance remain separate requirements.

## Exact catalog and vocabulary

The [explicit machine-readable inventory](../../tests/AgentCore.Api.Tests/Fixtures/tenant-permission-policy-inventory.json) records every current permission key, primary resource family, classification, Core minimum, human-only/delegatable and system-only flags. It contains **78 permissions**: 24 ReadOnly, 36 Mutation and 18 Sensitive. Fourteen are human-only (including the five system-only permissions). The remaining 64 are eligible for Agent principals: 22 ReadOnly, 24 Mutation and 18 Sensitive.

The [catalog](../../src/AgentCore.Application/Security/PermissionCatalog.cs) owns actual metadata and scope applicability. `sandbox.read/manage` also support sandbox-profile targets; `artifacts.create` and `attachments.create` also support Session targets. The inventory records the primary resource type; these additional target shapes remain owned by the catalog and their real resource services. A tenant-scope policy decision does not prove access to every concrete child.

Current built-in roles are seven tenant roles — Owner, Access Administrator, Contributor, Reader, Agent Operator, Agent Administrator and Approver — plus the system Platform Operator. Their stable keys are `owner`, `access-administrator`, `contributor`, `reader`, `agent-operator`, `agent-administrator`, `approver` and `system-operator`. Owner and Platform Operator are human roles in their separate planes. Approver supplies approval read/approve, not underlying effect rights or general exceptional-delegation authority. Contributor excludes HTTP, approvals, human-only and tenant-governance permissions; it does not bypass any permission's review minimum. Exact role grants remain defined by BuiltinAccessRoles.

## Defaults and mandatory-risk rationale

| Classification | Catalog entries | Core minimum | Authorized independent task |
| --- | ---: | --- | --- |
| ReadOnly | 24 (22 Agent-eligible) | None | Current raw permission and normal source/capability gates permit a decision without human review |
| Mutation | 36 (24 Agent-eligible) | WhenRequesterLacksPermission | An admitted no-human source has no fictitious denied requester; a real human requester is checked separately |
| Sensitive | 18 | Always | Exact Review remains required even when the Agent has raw permission |

The 18 Sensitive entries retain intentional Core minimums:

| Permissions | Rationale |
| --- | --- |
| `agents.delete`, `agent-definitions.delete`, `sessions.delete`, `resources.delete`, `automations.delete`, `workspaces.delete`, `artifacts.delete`, `attachments.delete`, `drive.delete` | Destructive lifecycle or retained-data removal |
| `credentials.bind`, `credentials.use`, `credentials.manage` | Credential association, protected use and security material |
| `email.send` | External transmission |
| `browser.use`, `browser.manage` | Coarse privileged native Browser effects and lifecycle |
| `sandbox.execute`, `sandbox.manage` | Coarse privileged native execution and lifecycle |
| `http.request` | Arbitrary external request; ordinary public research keeps its narrower read tools |

None of these minimums was weakened. Browser read/use/manage and Sandbox read/execute/manage permissions remain nondelegatable; lack of their raw rights cannot become a coarse exceptional grant. Ordinary delegatable permission loss may request only a qualified exact ExceptionalGrant. All human/system-only permissions hard-deny Agent execution regardless of roles or approval.

Tenant governance may tighten mode or independent review and cannot lower a Core minimum. A stricter policy is authoritative on the next decision. Metadata inspection neither creates an ApprovalRequest nor consumes a grant, changes Run state, grants a role or dispatches an effect.

## Executed qualification scope

Four scenarios use the actual canonical Automation creation/admission path: scheduled/no-human and manual/real-human in memory and SQLite. The dispatcher is held before provider execution; each real Run is claimed through the existing runtime store. An explicit Agent role supplies every non-human tenant permission. A separate retained Automation-source role keeps source validity independent of later effect-role revocation. A distinct current human reviewer permits the stricter independent-review branch.

Each scenario checks every one of the 78 inventory entries against current persisted Run/IAM decisions at the exact trusted tenant scope. For no-human work, all 46 eligible read/ordinary-mutation permissions are allowed by their defaults; all 18 sensitive operations require Review; 14 human/system permissions deny. Manual qualification uses the actual Owner requester with underlying rights; it does not claim the Reader-requester combinations already owned by broader approval qualification.

The scenarios then tighten an ordinary mutation to Always/independent review, reject weaker mutation and sensitive policies without changing security revision, revoke the effect role, and distinguish a delegatable HTTP ExceptionalGrant decision from a nondelegatable Browser denial. They assert unchanged Run revision, no new approval requests and zero outbound HTTP.

Existing real model Automation creation/inspection/edit/enable/disable, independent task authoring and exact deletion-consent journeys accompany the catalog checks. Those exercise actual effects rather than treating inspection metadata as execution. Existing governance checks qualify bounded reads, separate management rights, tightening, revocation and no read-side policy mutation.

The first new fixture used an unsupported literal tenant resource selector; it was corrected to the incumbent selectorless tenant-scope form derived from the trusted Run reference. This was a fixture failure, not evidence of a default-policy defect. Initial compilation also caught two references to the wrong decision property name; no product contract changed.

## Focused results

**20 distinct cases passed**: 4 complete catalog scenarios, 14 actual Automation effect/consent cases and 2 governance cases. The four catalog cases perform **312 inventory decision checks**, plus tightening, invalid-policy and revocation checks; those checks are not counted as separate tests.

| Focused check | Result |
| --- | ---: |
| Complete inventory, actual scheduled/manual admission, both stores | 4 passed |
| Real model Automation mutation and independent authoring | 4 passed |
| Exact Automation deletion approval/rejection/change/revocation/source review | 10 passed |
| Governance reads/writes/revocation, both stores | 2 passed |
| Serial solution build, no restore | 0 warnings / 0 errors |
| JSON inventory parse/uniqueness, whitespace and local document targets | Passed |

The first API batch was 14 pass/4 fixture failures. Correcting the scope form changed only the four new catalog scenarios; their focused rerun passed 4/4. The passing effect and governance groups were not repeated. No default-policy defect was found and no production policy was changed.

- Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`; `/private/tmp/tenant-policy-catalog-final-build.log`.
- Catalog: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~Actual_independent_Automation_checks_the_complete_reviewed_permission_catalog`; `/private/tmp/tenant-policy-catalog-api-final.log`.
- Accompanying API selection: exact `Tenant_model_Automation_mutations_create_inspect_edit_enable_disable_and_finish`, `Tenant_independent_Automation_authors_a_new_disabled_task` and `Tenant_model_existing_Automation_deletion_requires_exact_current_consent` method-name filters; `/private/tmp/tenant-policy-catalog-api.log` retains their 14 passing cases and the corrected initial catalog failures.
- Governance: `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~Governance_read_is_independent_bounded_coherent_and_revoked_without_policy_or_audit_mutation`; `/private/tmp/tenant-policy-catalog-governance.log`.

CI, live providers and browser/UI checks were not requested or run for this backend audit. The earlier qualification retains its real lower-privilege requester and independent-reviewer revocation evidence; this report does not relabel those as newly executed cases.

 The catalog audit alone does not close every operation-specific mandatory Review, concrete resource/store/effect boundary, Harness serialization or final security/isolation/UI acceptance item. No production default, role, schema, migration, wire contract or UI source changed.
