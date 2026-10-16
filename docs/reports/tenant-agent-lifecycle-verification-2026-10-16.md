# Tenant Agent Instance transaction verification — 2026-10-16

This checkpoint continues the [full delivery ledger](multitenant-iam-delivery-2026-10-16.md) after resource-scope ownership commit `30bc286e`. It implements the canonical actual Instance create/read/inventory/archive/restore boundary. It does not complete the proposal or accept full tenant isolation.

## Changes and review corrections

- `TenantAgentService` creates from exact immutable shipped templates only. It authorizes the current actor, creates the existing Instance and initial Skill state with its own SystemIdentity/Principal, increments tenant security revision and appends audit in one transaction. New agents receive no role automatically.
- `ITenantAgentStore` joins the unique backing identity to actual Instance rows. Tenant and resource-permission filters apply before inventory count/order/pagination. An empty allowed-ID set stays empty; no global catalog/store fallback is used.
- Archive/restore requires both security and Instance revisions, changes Instance lifecycle and identity/Principal status together, and preserves IDs/grants. The full host uses existing lifecycle coordination and post-commit Automation/Sandbox reconciliation.
- Store commit rechecks actor availability and global Account status. Prepared writes lose safely after suspension or tenant authority changes. Existing identity backing, Principal and tenant cannot be rebound or removed through security updates.
- The new restrictive backing Instance FK and matching InMemory retention prevent dangling identities and unscoped backing deletion. Old lifecycle store paths reject owned roots. Removed the standalone identity-only agent-registration service method.
- Review aligned SQLite's explicit missing-backing validation with InMemory. The first SQLite inventory projection could not translate filters in EF; the final member-initialized projection executes filtering/count/pagination in the database. Neither issue remains in the passing final checks.

## Executed local checks

All .NET commands use `--no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. Local test execution uses the approved socket-capable environment. No hosted providers or CI ran.

| Command (followed by the common arguments above) | Final result | Exercised outcome |
| --- | --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~IamIdentityIntegrationTests` | **14/14 passed**, zero skips | Both stores: actual owned Instance/identity creation, initial shipped Skill state, no automatic grants, two-tenant/scoped inventory, stable bounded pages, archive authority revocation, restore of same IDs/grants, stale-write audit rollback, suspended Account commit denial, competing CAS without orphan Instances, immutable ownership, missing backing rejection, legacy lifecycle denial, restrictive deletion and SQLite reopen. Existing identity/membership/Owner tests remain passing. |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AgentInstanceStoreContractTests\|FullyQualifiedName~AdminInstanceLifecycleHistoryTests\|FullyQualifiedName~AdminManagedInstanceHistoryTests'` | **37/37 passed**, zero skips | Existing shared Instance-store and lifecycle/history behavior remains passing after helper reuse and the owned-root guard. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~TenantAgentAccessTests\|FullyQualifiedName~IdentityAndTenantAccessTests\|FullyQualifiedName~IdentitySqliteRestartTests'` | **8/8 passed**, zero skips | Full Synthetic HTTP cookie/CSRF creation, resource-only Reader inventory, cross-tenant denial, no automatic agent roles, stale lifecycle conflict, valid restore and immediate scope revocation. A separate real identity/tenant endpoint host restarts with temporary SQLite and preserved key ring: cookie authority and the same owned Instance/Principal survive. |

Total: **59 focused scenarios passed**. Builds performed by these commands succeeded. The final new backing-reference/Skill assertions passed in the 14-scenario rerun. Documentation validation passed across nine changed Markdown files and 453 local links/fragments, with balanced fences; `git diff --check` passed. No frontend implementation changed in this checkpoint; no new browser result is claimed.

## Remaining boundaries

Ownership is normalized through SystemIdentity, not a second Instance store. Existing Instance settings/resources/reads and old owner APIs still need tenant-aware conversion; these passing canonical APIs do not secure those unfinished paths. Tenant durable Definitions/publications, delete/decommission/tombstone UX, Sessions/Runs/Hub/tools/background effects, trusted requester/executor/root lineage, durable first-class approval/exact grants and tenant-aware agent/Chat/approval UI remain pending.

Automation/Sandbox reconciliation follows the lifecycle commit. A cleanup failure may surface after the durable change and does not roll it back. This check does not prove tenant-aware worker interruption or approval continuation. The migration chain is intermediate and still needs final baseline consolidation. All storage used here was fresh disposable test data; no user database/root was migrated/reset, no user host was restarted and no deployment occurred. The original goal remains active.
