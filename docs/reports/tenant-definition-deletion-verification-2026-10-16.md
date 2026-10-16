# Tenant Definition deletion verification — 2026-10-16

This checkpoint continues the full Account/tenant/IAM/approval proposal after `ce9b7120`. It completes canonical backend draft/logical-definition deletion over the existing owned authoring graph. It does not complete the full proposal or establish multi-tenant deployment/milestone acceptance.

## Changes and observable behavior

- Definition read plus delete authority is required at the actual owned Definition scope. Cookie/current membership and CSRF protect the canonical HTTP routes; supplied actor/scope values cannot confer authority.
- Draft DELETE checks security and draft revisions. It purges that draft's resource bindings, scenarios and result history. Deleting the last unpublished draft also retires its logical root; another draft/publication keeps the root and its grants active.
- Logical DELETE compares the complete reviewed draft-ID/revision and publication-version/metadata-revision witness inside its transaction. An omitted/extra/changed child or security revision returns `stale_revision` without mutation.
- Any live or archived Instance, live Session or retained Run/Activation blocks retirement with `definition_in_use`. Counts are not exposed. No referenced history is cascade-deleted.
- Retirement removes only direct grants for that Definition, purges authoring children, advances security revision and appends one success audit atomically. Actor/global Account status is checked again at commit. Competing changes share that security CAS.
- `TenantAgentDefinitions.DeletedAt` retains the owning ID after content deletion. Retired roots disappear from inventory, reads and current IAM target resolution while continuing to fence global authoring and ID reuse. SQLite reopen retains this protection. New roots cannot be created already deleted.
- Shared immutable resource bytes remain retained. Removing one Definition's bindings does not erase a sibling/other tenant's content; reference-aware blob GC remains pending.

The implementation adds `20261016140347_TenantDefinitionRetirement` to the intermediate development chain. It was generated/checked against the model and exercised only on new temporary databases. The final consolidated tenant baseline remains pending; no populated user database was migrated or reset.

## Executed workflows

The first runnable use case was `Deleting_last_unpublished_draft_purges_children_and_grants_without_reopening_global_authority` on both stores. A stale draft revision preserved content/evidence and grants; valid deletion removed all children/grants, hid the root, denied global recreation/re-granting, retained private immutable bytes and survived SQLite reopen. **2/2 passed** before extending regression coverage.

The Synthetic HTTP use case then exercised login/CSRF → create → stale delete/re-read → foreign tenant denial → Reader inspect/delete denial → missing-CSRF denial → successful delete → denied subsequent reads. A published Definition plus fork required the complete witness; valid logical deletion denied later publication reads and instantiation. A separately instantiated Definition rejected deletion with `definition_in_use`, preserving revision and publication. **1/1 passed** before the final focused regression selection.

Additional both-store cases verified publication metadata changes invalidate deletion, deleting one fork preserves its siblings/publication bindings, whole deletion leaves a sibling sharing the same bytes readable, stale witnesses take precedence over reference conflicts identically on both stores, only one deletion competitor commits/audits, archived Instances and retained Session/Activation history block deletion, and a suspended global Account cannot commit a prepared deletion. A paused offline evaluator released after retirement failed its commit without resurrecting evidence. Prepared Instance creation and retirement accepted only one security witness, with no orphan identity/Instance or missing referenced publication.

## Final local checks

All commands ran from the repository root. VSTest used approved local execution for its loopback socket, Synthetic adapters and disposable storage; no provider requests were made.

```bash
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~TenantDefinitionIntegrationTests|FullyQualifiedName~IamIdentityIntegrationTests|FullyQualifiedName~AgentDefinitionAdminStoreContractTests|FullyQualifiedName~AdminLifecycleDeletionTests' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~TenantDefinitionAccessTests|FullyQualifiedName~TenantAgentAccessTests|FullyQualifiedName~IdentityAndTenantAccessTests|FullyQualifiedName~IdentitySqliteRestartTests' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal
dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build
git diff --check
```

Results: **86/86 Infrastructure**, **13/13 API**, zero skips; EF reported no pending model changes; whitespace check passed. The EF tool is 10.0.5 versus runtime 10.0.12 and printed its version advisory; migration generation and model verification succeeded. An initial compilation caught an incorrect Activation field assumption; the corrected reference check uses its actual Session relation, and retained-Activation coverage passes on both stores. All nine changed Markdown files passed 459 local link/fragment checks and balanced-fence validation.

## Remaining work

Builtin-template forks with approved content, full tenant Definition/resource/deletion UI, previews/standalone resource IAM and runtime resolution remain pending. So do the other resource owners, Session/Run/Hub/tool/background effect cutover, trusted execution lineage, durable approval continuation, obsolete authority removal, final schema consolidation and full responsive UI acceptance. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) retains the original full goal. This backend checkpoint did not run browser acceptance, CI, deployment, a user-host restart or any user-data reset.
