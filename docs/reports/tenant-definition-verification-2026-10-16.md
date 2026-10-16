# Tenant definition ownership verification — 2026-10-16

This checkpoint follows `9c33627e` and continues the [full proposal ledger](multitenant-iam-delivery-2026-10-16.md). It adds canonical tenant-owned Definition authoring/publication and Instance creation from an exact owned publication. The full replacement remains incomplete.

## Implemented behavior

- `TenantAgentDefinitions` is an actual owning root with Tenant FK, opaque generated definition ID and UTC millisecond creation time. Existing draft/publication/resource/evaluation records remain the content owners. No parallel durable catalog is introduced.
- `TenantDefinitionService` authorizes current create/read/update/publish permissions. Root inventory applies tenant/resource permission filters before count/order/page. Direct draft/version reads prove root ownership. Definitions have their own IAM scope, independently of Instance scope.
- Create persists root + starter draft + tenant security revision + audit atomically. Edit fences both security and draft revisions. Fork copies an owned exact publication and resource bindings. Publish preserves candidate/model/tool validation, current resource requirements, actual immutable resource bytes and required revision/fingerprint/scenario evidence, assigns a version, consumes draft/children and commits security/audit together. Earlier payloads remain immutable. Deprecation advances only metadata.
- Every canonical write rechecks active actor and current global Account at commit. A collision or stale security witness leaves no orphan root, consumed draft or success audit. Competing publication attempts admit one winner.
- IAM resolves definition ownership through its real store; security writes independently validate definition assignment references. Fabricated/foreign definition scopes fail closed. Bootstrap cannot seed resource identities/grants.
- Unscoped authoring readers exclude tenant-owned drafts/publications/resource/evaluation records. Old writes cannot append drafts (including GUID spelling aliases) or evaluation results to owned roots. Unscoped draft creation serializes its ownership check with insertion, closing the race with canonical root creation. SQLite uses explicit root exclusion queries; InMemory uses the same root and locked operations.
- `POST /agents/from-publication` requires both tenant `agents.create` and exact `agent-definitions.read`, rejects foreign/deprecated versions and pins the selected active tenant publication. Shipped template creation stays separate and never supplies a global durable fallback.

## Executed checks

All .NET test commands used `--no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. Approved local execution allowed test sockets. All data/keys were disposable; no user host/database/root was restarted, reset or migrated.

| Project / filter | Passing scenarios | Evidence |
| --- | --- | --- |
| Infrastructure: `FullyQualifiedName~TenantDefinitionIntegrationTests` | **6/6**, zero skips | Both stores: actual owning records, consumed drafts, immutable earlier publication, copied resource bindings, foreign/raw assignment denial, global-path read/write denial, missing required evidence and physical-byte rollback, one winning publish CAS, colliding draft insertion without an orphan root, suspended-Account write rejection and SQLite reopen. Final assertions reran after the ownership insertion fence. |
| Infrastructure: `FullyQualifiedName~IamIdentityIntegrationTests` | **14/14**, zero skips | Existing IAM/agent scope, identity, Account, last-Owner and Instance transaction scenarios still pass with external definition ownership resolution and bootstrap restrictions. |
| Infrastructure: `FullyQualifiedName~AgentDefinitionAdminStoreContractTests` | **23/23**, zero skips | Existing shared draft/publication lifecycle and metadata contracts remain passing; rerun after serializing unscoped insertion. |
| Infrastructure: `FullyQualifiedName~AgentDefinitionResourceAdminStoreContractTests` or `~DefinitionDraftEvaluationStoreTests` or `~DefinitionResourceInfrastructureDiTests` or `~AdminLifecycleDeletionTests` | **61/61**, zero skips | Shared resource/evaluation/DI/deletion behavior remains passing after root exclusion guards and publication helper reuse. |
| API: `FullyQualifiedName~TenantDefinitionAccessTests` or `~TenantAgentAccessTests` or `~IdentityAndTenantAccessTests` or `~IdentitySqliteRestartTests` | **9/9**, zero skips | Actual Synthetic cookie/CSRF HTTP composition; new create/edit/publish/fork/deprecate/instantiate flow; immutable v1 after v2; stale/deprecated/foreign denial; two same-tenant definitions with exact resource-only Reader inventory and revocation. Existing identity/agent flows and SQLite identity/agent host restart remain passing. The final two-definition case reran separately. |

Total: **113 distinct focused scenarios passed**. A final combined Infrastructure rerun covers new definition and existing authoring tests. Test builds pass. `dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build` reports no model change after the migration. The local EF tool reports 10.0.5 versus runtime 10.0.12; generation and verification succeeded. Documentation validation passed across nine changed Markdown files and 455 local links/fragments with balanced fences; `git diff --check` passed. No frontend implementation changed, no browser result is claimed and no CI was requested/run.

## Remaining full-goal work

Canonical definition resource upload/bind/edit and evaluation authoring/execution, builtin-template fork content, draft/logical deletion, complete tenant authoring UI, and tenant-aware runtime definition/resource resolution still require conversion. Seeded child fixtures above prove preservation of publication gates, not completion of their editing flows. In particular, unscoped runtime definition readers intentionally cannot resolve tenant publications yet; the new authoring/Instance path is not a claim that tenant conversation execution is accepted.

Other Instance settings/resources/decommission, Sessions/Runs/Hub/tools/background effect admission, requester/executor/root lineage, durable first-class approval/exact grants, old authority removal, final schema consolidation and full designed UI acceptance remain pending. This intermediate source is not a multi-tenant deployment. The original goal remains active.
