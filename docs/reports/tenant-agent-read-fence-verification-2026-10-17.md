# Canonical agent read freshness — 2026-10-17

This successor hardens existing tenant agent reads required by the full multi-tenant proposal. It consolidates the editor-only original-human fence across canonical directory, configuration and child-resource reads. The full proposal remains active; this is not full Phase 4, UI or milestone acceptance.

## Reproduced defect and change

The real Synthetic HTTP settings route was paused by a one-shot test adapter after Definition I/O. The original cookie session was logged out through the public authentication API before the read completed. Expected: safe denial with no captured settings. Observed before the fix: **200 OK**. The regression failed at the status assertion; `/private/tmp/tenant-agent-read-fence-before.log` retains that result.

`TenantAgentReadService` now extends and replaces `TenantAgentEditorService`. It is an ordinary typed read facade over the existing directory/configuration services and the same `IamAuthorizer`, not another authority engine. API routes derive the original Account/authentication-session/tenant-Principal from authenticated server context; callers cannot choose that identity. Exact reads validate current `agents.read` before protected I/O and after it, retain the matching Instance/Principal identity, and reject changed security or Instance revisions. Collection reads retain their existing scoped filtering or template-create rules and reject a changed tenant witness after I/O. The original human session is checked at both ends, including after actual resource byte reads; a new login cannot substitute for a retired request session.

Successful wire shapes, paging, child permissions, byte validation and no-store/file behavior are preserved. The existing write owners and transaction checks are unchanged. The former editor's composed settings checks and unsupported Sandbox exclusion remain intact. No UI source, schema, provider, token, header or storage port was added.

## Mapped read coverage

All routes below are under `/api/v2/tenants/{tenantId}`. The same signed-in human boundary wraps their existing owners; protected results are not mapped to HTTP until it returns.

| Read | Existing owner/permission behavior retained | Added fence evidence |
| --- | --- | --- |
| `GET /agent-templates` | Read-only shipped catalog; current `agents.create` | Captured template list: original logout/relogin denied; changed security rejected; explicit current read recovers |
| `GET /agents` | Tenant-owned inventory; exact readable IDs filtered before count/page | Captured inventory: original logout/relogin denied; changed security rejected; scoped inventory regressions pass |
| `GET /agents/{id}` | Exact owned Instance and `agents.read` | Captured Instance: original logout/relogin denied; changed security rejected |
| `GET /agents/{id}/effective-config` | Existing resolver and readable-child redaction | Original logout/relogin HTTP cases; InMemory/SQLite persona/security/logout races |
| `GET /agents/{id}/settings` | Existing typed effective/default/override/source sections | Original logout/relogin HTTP cases; InMemory/SQLite persona/security/logout races |
| `GET /agents/{id}/execution-budgets` | Existing default/override/effective/host-ceiling projection | Original logout/relogin HTTP cases; InMemory/SQLite persona/security/logout races |
| `GET /agents/{id}/self-management` | Existing per-field policy/default/source view | Original logout/relogin HTTP cases; InMemory/SQLite persona/security/logout races |
| `GET /agents/{id}/skills` | Exact child `skills.read`, filtering/count/page and safe dependencies | Original logout/relogin HTTP cases; InMemory/SQLite races; existing per-item child journey |
| `GET /agents/{id}/resources` | Exact child `resources.read`, visible catalog and safe dependencies | Original logout/relogin HTTP cases; InMemory/SQLite races; existing per-item child journey |
| `GET /agents/{id}/resources/{key}/content` | Exact owned/readable resource, verified bytes and current child access | Actual byte-I/O logout/relogin/security-change cases release no sentinel; fresh authorized read returns exact bytes |
| `GET /agents/{id}/editor` | Safe persona/settings composition and exact update hint | Original logout/relogin HTTP cases; both-store composed-read matrix and previous editor regressions |

Agent Access, Connections/bindings and Chat discovery have their own existing original-human services and were not rerouted. Other resource families and mutation/worker ingress are not claimed covered by this table. Legacy owner-capability removal remains required by the proposal.

## Executed verification

All tests used Synthetic fixtures and disposable stores; no real provider call, installed-data reset, persistent app restart or CI occurred. Test adapters intercept the actual owning port after I/O and fire once; there are no sleeps or product/debug hooks.

| Command | Observed result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Human_agent_reads_reject\|FullyQualifiedName~Tenant_instance_editor' --nologo` | **50 passed**: 42 new races across seven read shapes × three changes × both stores, plus eight existing coherent-editor cases |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantAgentAccessTests\|FullyQualifiedName~Owned_instance_skills_and_resources\|FullyQualifiedName~Exact_child' --nologo` | **27 passed**: 20 original-logout/relogin cases across ten reads, three byte-download races, three existing scoped agent/editor tests and the existing owned child/byte workflow. `Exact_child` matches no additional case; the actual named per-item case is run below. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Directory_reads_reject\|FullyQualifiedName~Item_scoped_child_grants' --nologo` | **4 passed**: three new directory/template stale-read cases plus the existing exact child-grant filtering/download/deletion/retirement journey |
| `git diff --check` | Passed |
| New canonical-document relative links | Four checked; all resolve to the current evidence report |

**81 distinct focused cases passed.** Both final suites compile the affected Application/Infrastructure/API projects. An intermediate infrastructure test compile found a missing `Ports` import in the new test file; it was corrected before the 50-case run. The initial one-case HTTP regression was observed failing before the implementation and passing afterward, before matrix expansion.

In each logout case, the request's original session is invalidated during captured I/O; the response is `403 initiator_unavailable`. In the relogin variant, a new session for the same Account exists before the original response finishes, but the original still fails; a subsequent explicit read succeeds. Persona/security changes during Definition reads reject `409 stale_revision`, followed by successful explicit read recovery. Directory captures behave the same under a concurrent harmless role creation. Byte-download responses after logout/relogin or a security change contain no protected sentinel; current-session/current-security recovery returns the original bytes exactly. Existing cross-tenant, read-only, sparse save/reset and per-item child grant behavior remains verified by the selected regressions.

Logs: `/private/tmp/tenant-agent-read-fence-stores.log`, `/private/tmp/tenant-agent-read-fence-http.log`, `/private/tmp/tenant-agent-read-fence-collections.log`, and the before/first-after logs. All process handles reached terminal results; no task-owned host was left running.

## Scope and remaining acceptance

Backend runtime verification used the real WebApplicationFactory HTTP/auth/services/stores and both-store integration fixtures. No frontend source or visual system changed; this checkpoint did not rerun Playwright, frontend build or Impeccable captures and does not renew those acceptance claims. The previous scoped editor report remains evidence for its original UI commit.

Canonical interface/backend/protocol/testing documents, README, implementation plan and delivery ledger record the new read owner and exact behavior. Historical dated reports retain their original class names, source and evidence scope.

Full tenant-resource/effect ownership, remaining Instance and resource UI, trusted autonomous ingress, voice/rich delivery and receipts, input arbitration, Governance/platform administration, obsolete owner/approval authority removal and final schema/reset consolidation remain required. The previously tracked SQLite direct-preference/provider-unknown reliability issue also remains separate. No full proposal or milestone completion is claimed.
