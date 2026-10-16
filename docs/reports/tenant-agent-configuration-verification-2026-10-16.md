# Tenant Instance configuration verification — 2026-10-16

This continues `41f86cbb` toward the original full Account/tenant/IAM/durable-approval proposal. Canonical owned Instance inspection, persona and exact active-version adoption are implemented and locally verified. The full goal remains active; this is not runtime, full tenant isolation or milestone acceptance.

## Implemented boundary

`TenantAgentConfigurationService` resolves a server-owned Instance and coherent Skill/resource snapshot, then its exact tenant publication and manifest or exact shipped builtin. Owned GUIDs never fall back to a global durable catalog. Existing immutable publication, sparse-settings, effective configuration, Skill/resource and model resolvers are reused. Authorized Instance readers can inspect inherited configuration independently of Definition collection access; adoption of an owned publication separately requires Definition read. Configuration/capability metadata grants no IAM permission or execution admission. The HTTP projection omits the old Admin durable-execution eligibility flag.

Persona and version changes require current scoped Instance read/update, security and Instance revisions; persona also requires its exact revision. Archived Instances are inspectable and reject changes. Version adoption checks exact active publication/status/metadata, current overrides/model/Skill/resource compatibility and the observed Automation policy plan. Already pinned deprecated payloads remain inspectable. Publishing another version never implicitly repins an Instance; persona remains independent.

Both stores commit Instance/persona revisions, new inherited Skill state, observed Automation policy changes, security revision and audit together. SQLite reads children in one transaction and reuses existing mappings; InMemory shares the actual store locks in existing order. Current global Account status is checked at commit. Failed publication/persona/plan witnesses, cancellation or competing CAS leave no partial parent/security/success-audit write. No schema migration or second configuration store was added. Sandbox enforcement follows the existing post-commit reconciler; tenant Sandbox dispatch remains unqualified.

Old direct active-version, general parent-revision and persona/version-history write paths now reject owned Instances with `tenant_context_required`, including before history replay. Other old read/child/runtime routes remain explicit cutover obligations. A real malformed-persona request exposed a null-field crash in shared identity validation; missing/null/blank fields now produce safe validation failure.

## Runtime evidence

| Scenario | Expected and observed outcome |
| --- | --- |
| Owned publication → Instance → configuration | Cookie/CSRF HTTP resolves the owned payload; global publication/resource readers remain excluded; both-store shipped Support fork resolves the actual owned resource manifest |
| Local persona → new publication → explicit adoption | Persona persists independently; publication alone leaves old instructions pinned; explicit adoption changes to exact v2 while preserving persona; Instance/security revisions advance and SQLite recreation preserves payload, persona and resource identities |
| Scoped access | Resource-only Reader inspects only its Instance and cannot mutate; foreign tenant reads/writes deny; revocation denies the next configuration read. An agent with only own-Instance Contributor scope can inspect its configuration but cannot adopt a Definition without separate Definition read authority |
| Stale/archived/malformed/CSRF | Stale security/Instance/persona witnesses conflict; archived configuration remains readable and changes fail; incomplete persona returns 400; missing CSRF rejects mutation |
| Atomic race and Account suspension | Two same-witness persona writes yield exactly one success/audit; stale persona/cancelled write leaves state unchanged; an Account suspended after preparation cannot commit and cannot call the service |
| Publication/reconciliation witnesses | Wrong publication metadata and deprecated target reject without parent/security change; a conflicting Automation plan rolls back; actual persisted-profile Automation becomes SuspendedPolicy with its revision advanced in the successful adoption transaction |
| Old write path | General and direct-version legacy store writes reject owned Instances; unrelated existing managed Instance history/store behavior remains passing |

## Focused checks

| Command / selection | Result |
| --- | --- |
| API: `TenantDefinitionAccessTests|TenantAgentAccessTests|IdentityAndTenantAccessTests|IdentitySqliteRestartTests` | 15 passed, zero skipped; includes the full owned configuration/persona/adoption HTTP journey and existing cookie/CSRF/real SQLite restart composition |
| Final tenant-agent HTTP rerun | 1 passed after adding archived-read/mutation-denial and revoked-configuration assertions |
| Infrastructure: `IamIdentityIntegrationTests|Tenant_instance_configuration|Tenant_persona_cas|Tenant_adoption_rechecks|AgentInstanceStoreContractTests|AdminInstanceDefinitionVersionHistoryTests|AdminInstancePersonaHistoryTests|AdminInstanceLifecycleHistoryTests` | 63 passed, zero skipped; both stores and affected history/shared write-guard consumers |
| Final adoption/reconciliation rerun | 2 passed after extending the fixture with real persisted-profile Automation reconciliation |
| Domain: `AgentDefinitionValidatorTests` | 17 passed, zero skipped |
| Build | Affected Domain/Application/Infrastructure/Contracts/API/test projects compile in the focused commands |
| Documentation hygiene | Changed-file local links/fragments, fences, complete JSON examples and whitespace checked before commit |

Commands use `dotnet test <project> --filter '<selection>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. API and Infrastructure projects are `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` and `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`; Domain is `tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj`. Local test socket execution used approved sandbox escalation. Initial build/test setup corrected a missing model namespace and fixture method name; the malformed `style` fixture was corrected to canonical `tone`, with the observed null-field failure independently fixed and tested. Final executed checks above passed. Overlapping reruns are not additional unique cases.

## Scope and remaining work

No frontend code changed, so no new browser/visual acceptance is claimed. Synthetic HTTP and temporary migrated SQLite execute the affected use cases without providers, browser or Sandbox workloads. Existing user databases, Real hosts, credentials and Sessions were preserved. No live reset, deployment/restart or CI was performed.

Canonical sparse-settings, local Skill/resource operations and decommission, complete Instance/editor UI and resource preview/access panels, tenant Session/Run/Hub/tool/background execution, trusted requester/executor/root lineage, durable approvals, obsolete global authority removal and final schema/reset consolidation remain required by the [full delivery ledger](multitenant-iam-delivery-2026-10-16.md). Existing Runs retain their pinned configuration; full effect revocation and tenant execution must be verified during their own cutover. Do not use configuration projection or these tests as proof of those boundaries.
