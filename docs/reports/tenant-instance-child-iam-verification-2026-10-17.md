# Tenant Instance child IAM verification — 2026-10-17

This continues `209005e7` toward the full tenant/IAM/approval proposal. Exact Resource scopes now cover current local, owned-publication and shipped-source Skills/resources inside an owned Instance. The full goal remains active; Definition child scopes, other resource/effect families, runtime/approvals and complete UI acceptance are unfinished.

## Implemented contracts

`resourceType` is `skill` or `resource`; `resourceId` is `{instanceId:D}:{childKey}`. Existing Skill keys use `builtin:`, `definition:` or `instance:`; files use `definition:`/`instance:` plus their canonical resource GUID. The parent is an identity qualifier within one Resource scope, not another IAM inheritance level. Unqualified readable IDs, paths and hashes never prove ownership. Same-named local Skills in different Instances are distinct targets.

The canonical scope parser, aggregate shape validation and existing authorizer/assignment/access inspection support these addresses. Both stores prove actual tenant/Instance ownership and current local/source/manifest membership. Owned definitions resolve through their actual immutable publication; shipped sources resolve through explicitly composed builtin and approved-resource catalogs. Dormant inherited state remains retained for inheritance semantics but is not a current IAM target. Unsupported/fabricated/foreign references fail closed. Resource-type applicability and human-only permissions remain independent of role names.

Every Instance child operation still requires exact-parent `agents.read`. Existing-item reads and mutations accept their typed exact grant or inherited tenant grant. Creating a new local child, customization or inherited-resource copy requires tenant-wide destination read/manage; an existing-item grant cannot create siblings. Agent writers additionally need their current Skills/Resources self-management area. No agent permission is copied from a human requester.

Collections filter unreadable rows before counts and pagination; resource dependency projections omit unreadable Skill keys. Effective configuration filters actual Skill/resource payloads and reports `skills`/`resources` plus `allSkills`/`allResources` access flags. Full knowledge-source configuration stays redacted without tenant-wide resource read. Protected downloads recheck the exact current grant after verified private-byte I/O.

Deletion and explicit version adoption derive the resulting actual child catalog and retire assignments for removed targets in the same child/parent/security/audit transaction. Raw store changes retaining such assignments reject before mutation. Grant-versus-delete writes serialize through the same security witness. Recreating a readable local key or later restoring removed inherited content cannot revive retired assignments. Existing assignment columns/indexes and child stores are reused; no migration, resource registry, parallel storage or compatibility path was introduced.

Review also found that generic tenant security CAS did not recheck an already-prepared acting Account. Both stores now fence the current actor/Account at commit. Invitation acceptance is the one new-member attribution path: it separately validates the accepting Account and existing inviter status rather than pretending the new Principal already belonged to the tenant. InMemory propagates cancellation through child-source checks and checks before security commit. Production identity registration composes the actual shipped catalogs; the minimal identity-restart fixture explicitly registers those dependencies and test SQLite bootstrap uses the same sources.

## Observed workflows

| Use case | Expected and observed result |
| --- | --- |
| Exact local Skill/file grants | HTTP editor sees only those items and filtered totals/pages; updates and deletion succeed on those targets, other siblings and same readable ID in another Instance deny |
| Destination authority | Exact item management cannot create a new Skill or upload a sibling file; parent update or scoped Reader authority does not substitute for typed child permission |
| Partial configuration and dependencies | Hidden procedure/file sentinels do not appear; access flags report subsets; a resource-only reader sees no hidden Skill dependency, and the explicit Skill read grant reveals only that dependency |
| Scoped IAM inspection | A human granted IAM inspection/assignment on one child can inspect it but cannot assign the same role on another child; malformed/unqualified/nonexistent targets reject |
| Current inherited source | Owned publication and shipped Definition Skill/approved knowledge scopes resolve real content. Cross-tenant rebinding fails in authorizer/store; removed inherited state cannot remain an IAM target |
| Delete/adopt/restore | Deletion and adoption retire target grants with audit/security CAS; unrelated grants survive. Recreated/restored content receives no previous authority; SQLite recreation retains the retired-grant state |
| Raw transaction and races | Raw delete retaining a child grant rejects with no revision/audit change. Competing same-witness grant/delete yields exactly one success and a coherent resource/assignment graph |
| Revocation during byte I/O | Gate the private read, revoke the exact resource grant, release I/O: no protected bytes return; still-authorized owner read succeeds |
| Prepared grant after Account suspension | Prepare an Access Administrator's exact grant, suspend its global Account, then commit: both stores reject `account_unavailable`, without saving the assignment, tenant revision or success audit |

## Focused verification

Commands use `dotnet test <project> --filter '<selection>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`, with approved local execution and no provider calls. Projects are `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` and `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`. Each selection term is prefixed `FullyQualifiedName~` and joined with `|`.

| Selection | Evidence |
| --- | --- |
| API `Owned_instance_skills_and_resources`, `Owned_instance_inherited_resource_copy` | 2 passed before broad regression; incumbent child journeys remained functional after scope changes |
| API `Item_scoped_child_grants` | Initial real HTTP scope journey passed; expanded shipped-source assertions are included in the selected API family below |
| Infrastructure `Tenant_child_scope`, `Tenant_instance_child_content_rechecks` | Initial 6 passed across both stores, before final Account-suspension assertions |
| Infrastructure `TenantDefinitionIntegrationTests`, `IamIdentityIntegrationTests`, `AgentInstanceStoreContractTests`, `AdminInstanceDefinitionVersionHistoryTests`, `AdminInstancePersonaHistoryTests`, `AdminInstanceLifecycleHistoryTests`, `AgentDefinitionResourceAdminStoreContractTests`, `InstanceSkillsStartupTests`, `DefinitionResourceInfrastructureDiTests` | 175 passed, zero skipped after the final Account actor fencing extension and DI assertion |
| API `TenantDefinitionAccessTests`, `TenantAgentAccessTests`, `IdentityAndTenantAccessTests`, `IdentitySqliteRestartTests`, `AgentInstanceSkillTests`, `SelfManagementAuthorityTests`, `SelfManagementOperationTests` | 40 passed, zero skipped after the final Account actor fencing extension; includes expanded shipped-source and actual cookie/SQLite restart scenarios |
| API build | Passed, zero warnings/errors; subsequent selected test commands compile their affected source |

A new fixture initially omitted the explicit system actor parameter for tenant creation, and the expanded DI test initially lacked the identity extension namespace import. Both compile errors were corrected in the fixtures. Production authority and bootstrap were not relaxed. The post-review storage/IAM selection passed **175/175**, including the final dependency graph and Account-fencing assertions. The focused `Tenant_child_scope_assignment_rechecks|IamIdentityIntegrationTests` run also passed **16/16** (overlapping cases). Documentation hygiene checked 10 changed Markdown files, 644 local links/fragments, 20 complete JSON examples, balanced fences and whitespace; all passed. The final selected API rerun passed **40/40** after actor fencing; together with the final storage selection this is **215 focused checks**, excluding overlapping reruns.

## Remaining scope

Definition child scopes and the remaining tenant-owned resource/effect families, tenant Sandbox/decommission, complete Instance/editor UI/access/preview panels, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approval execution, obsolete global authority removal and final schema/reset consolidation remain required. The [full delivery ledger](multitenant-iam-delivery-2026-10-16.md) preserves these obligations. This source remains an intermediate nondeployable cutover, not full tenant isolation or milestone acceptance.

No frontend source changed, so no new browser/visual acceptance is claimed. Checks use disposable Synthetic/InMemory/SQLite fixtures; user data, Real hosts and credentials are preserved. No live reset, deployment/restart, hosted providers or CI was performed.
