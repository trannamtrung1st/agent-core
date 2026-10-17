# Canonical agent original-session write atomicity — 2026-10-18

This checkpoint extends the existing canonical agent transactions with the original human authentication-session witness. It complements the [read fence](tenant-agent-read-fence-verification-2026-10-17.md) and advances the full multi-tenant proposal's security cutover. It does not complete Phase 4, the remaining UI or the full proposal.

## Reproduced failure and implementation

The real Synthetic HTTP settings write was paused after Definition preparation. Its original login was logged out through the public authentication API before the mutation resumed. Expected: denial with unchanged Instance/security state. Before the fix, the route returned **200 OK** and accepted the mutation. The original failing status assertion is retained in `/private/tmp/tenant-agent-write-fence-before.log`.

Canonical agent mutation endpoints now derive the original Account/authentication-session/tenant-Principal from server cookie/tenant context and pass it through the existing services. `TenantAgentConfigurationChange.Initiator` carries this transient reference to the specialized configuration store; creation and lifecycle stores accept the same optional reference. Clients do not supply it. Any supplied Principal must match the audit actor and a live Account/session/stamp/expiry/exact tenant User/Principal. The Application Commit helper also validates the original human before store submission, and a no-op inherited Skill reset revalidates before returning without a transaction.

The actual stores enforce the reference inside the existing atomic boundary. InMemory checks under its existing locks immediately before mutating state and rechecks tenant revision after binding-content verification. SQLite checks immediately before SaveChanges inside the transaction, so denial rolls back all staged changes. A replacement login for the same Account never substitutes for the request's retired session. Current explicit recovery can succeed using unchanged revisions; no mutation is retried automatically.

Existing IAM, management restrictions, policy preparation, publication/child witnesses, audit actions and response shapes remain in use. Trusted non-HTTP execution retains its existing actor/IAM/management checks without fabricating a human login. The witness is command context, not a new grant or persisted credential: no schema migration, serialized settings field, copied role, UI source or new authority engine was introduced. Verified content preparation can leave an unbound immutable blob after denial, but creates no binding, catalog entry or permission.

## Canonical HTTP mutation coverage

All routes are under `/api/v2/tenants/{tenantId}`. Thirty matrix cases cover every row with logout and logout followed by a new same-Account login **at the specialized store call**, after the Application precheck. Both variants must deny, preserve prior projections/security state, and allow one subsequent explicit request under the current login.

| Endpoint | Existing atomic owner |
| --- | --- |
| `POST /agents` | Template creation / tenant agent store |
| `POST /agents/from-publication` | Owned-publication creation / tenant agent store |
| `PUT /agents/{id}/lifecycle` | Lifecycle / tenant agent store |
| `PUT /agents/{id}/persona` | Configuration transaction |
| `PUT /agents/{id}/active-version` | Configuration/adoption transaction |
| `PATCH /agents/{id}/settings/{section}` | Sparse settings/configuration transaction |
| `PUT /agents/{id}/execution-budgets` | Configuration transaction |
| `PUT /agents/{id}/unattended-model` | Configuration transaction |
| `PUT /agents/{id}/self-management` | Configuration transaction |
| `POST /agents/{id}/self-management/freeze` | Configuration transaction |
| `POST /agents/{id}/skills/commands` | Skill/configuration transaction |
| `POST /agents/{id}/resources` | Resource/configuration transaction |
| `POST /agents/{id}/resources/{key}/copy` | Resource/configuration transaction |
| `POST /agents/{id}/resources/{key}/enabled` | Resource/configuration transaction |
| `DELETE /agents/{id}/resources/{key}` | Resource/configuration transaction |

The matrix compares pre/post-denial Instance, inventory, Skill and resource JSON projections and unchanged security revision, then verifies successful explicit recovery advances security and the actual Instance or inventory. The preparation-time settings regression independently verifies its language override remains absent. The no-op inherited Skill reset returns denial after original logout, then `changed: false` under a current login without a security revision increment.

## Final focused verification

All checks ran locally with Synthetic fixtures and disposable stores. No provider calls, user-data reset, Real host restart, deployment, CI or full CI suite occurred.

| Command/filter | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Every_agent_HTTP_mutation\|FullyQualifiedName~Agent_settings_write_rejects' --nologo` | **31 passed**: thirty store-boundary cases and the preparation-time regression |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Noop_skill_reset\|FullyQualifiedName~TenantDefinitionAccessTests.Owned_instance\|FullyQualifiedName~Item_scoped_child_grants' --nologo` | **6 passed**: no-op reset plus existing persona/adoption, sparse settings/budgets/model/management, owned child/byte, inherited copy and per-item grant workflows |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Atomic_agent_writes_validate\|FullyQualifiedName~Tenant_instance_configuration\|FullyQualifiedName~Tenant_persona_cas\|FullyQualifiedName~Tenant_adoption_rechecks\|FullyQualifiedName~Tenant_instance_sparse\|FullyQualifiedName~Tenant_agent_cannot_enable\|FullyQualifiedName~Tenant_settings_mutation\|FullyQualifiedName~Tenant_instance_children_preserve\|FullyQualifiedName~Tenant_instance_child_transactions\|FullyQualifiedName~Tenant_instance_child_content\|FullyQualifiedName~Tenant_instance_child_agent\|FullyQualifiedName~Tenant_chat_discovery' --nologo` | **52 passed**: 24 atomic-human cases and 28 affected existing store/authority/discovery regressions |
| `git diff --check` | Passed |
| New relative documentation/report links | Six resolve; frontend/design-system source is unchanged |

**89 distinct focused cases passed.** The test commands compile the affected Application/Infrastructure/API projects. The 24 atomic cases cross InMemory/SQLite × configuration/creation/lifecycle × logout/relogin/password rotation/mismatched Principal. They assert no partial state or allowed audit, then current-session success; SQLite reopen verifies the successful effect/security/audit persisted. Configuration cases call the store directly, bypassing the Application precheck, to prove the transaction itself rejects the invalid witness. Existing agent self-management/child tests retain independent executing-principal IAM and scope restrictions.

Final logs are `/private/tmp/tenant-agent-write-fence-http-final.log`, `/private/tmp/tenant-agent-write-fence-regressions.log` and `/private/tmp/tenant-agent-write-fence-stores-final.log`. All final handles reached terminal success.

## Corrected verification setup

- The first copy-resource matrix used a nonexistent `support` template; it was corrected to the actually shipped `customer-support` v3 before the passing matrix.
- Initial SQLite equality assertions compared an in-memory creation response's fine-grained timestamps with SQLite's existing millisecond storage. The tests now compare captured persisted snapshots before/after denial, then compare committed rows after reopen. No timestamp/storage implementation changed.
- The first no-op reset interceptor attempted nested HTTP logout while an InMemory child-scope catalog lookup held the IAM monitor. The confirmed live test host was inspected with a bounded native sample and `dotnet-stack`: one stack waited in `ChildCatalog`/`ValueTask.GetResult`, while nested cookie validation waited in `ReadSessionAsync` on the same monitor. Only that task-owned test session was interrupted; its process exit was confirmed. The callback was moved to the owning configuration-snapshot port after its lock releases, and the final regression passed. Diagnostic evidence remains in `/private/tmp/tenant-agent-write-fence-hang.managed.txt` and `/private/tmp/tenant-agent-write-fence-hang.sample.txt`. The interrupted run is not counted as a pass; no production hook or catalog architecture change was added.

## Documentation and remaining scope

Interface, backend, protocol, persistence and testing owners, README, implementation plan and the delivery ledger record the transient witness and atomic behavior. No frontend/design files changed, so frontend build, Playwright and Impeccable captures were not rerun or claimed for this backend-only successor. Prior design/UI evidence retains its original scope.

Other resource/effect and autonomous ingress families, remaining Instance/resource UI, tenant voice/rich delivery and receipts, input arbitration, Governance/platform administration, final obsolete owner/approval authority removal and schema/reset consolidation remain required. The previously tracked SQLite direct-preference/provider-unknown reliability issue remains separate. The original full goal stays active; no full proposal or milestone acceptance is claimed.
