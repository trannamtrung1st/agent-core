# Tenant Agent decommission backend qualification — 2026-10-20

This checkpoint implements canonical human Agent decommission, irreversible ownership retirement and supervised Browser/profile/workspace cleanup. It advances the original full IAM proposal. The frontend decommission control, autonomous Browser/background/event/transport integration, remaining resource families, general System guardrails/audit, historical execution reliability qualification and final obsolete-authority/schema consolidation remain open. The goal stays active; no milestone or full proposal acceptance is claimed.

## Resulting behavior

The current original human can inspect exact owned decommission preflight under raw `agents.read`. Decommission additionally requires raw `agents.delete`, an archived Agent, exact security/Instance revisions, a fresh operation ID and the incumbent reference checks. Live Sessions, active learned identity memory, Automations/occurrences, retained Runs/Approvals/Activations and a pending Browser reset remain blockers. Neither a model executor nor a different/global principal substitutes for the original tenant human. Current login and permission checks repeat inside the owning write; preflight rechecks the original login after its final metadata read.

The existing IAM/Instance transaction marks SystemIdentity and Principal Removed, advances Instance/security revisions, retires executor and Agent/home/Skill/resource assignments, unbinds credentials, purges local child configuration and writes one canonical body-free audit receipt attributed to the actual human. The archived backing Instance stays as the FK-backed owner tombstone. Live inventory/configuration/Chat/resource scope discovery excludes it; ordinary restore, generic identity writes and raw SQLite revival/removal are fenced. Same-operation factual readback requires current authority and the same actor/target/Instance witness; it cannot repeat logical retirement. A retired marker stops counting as a live Definition reference while genuine retained Instance/Session/execution references remain protected.

The committed owner/receipt drives technical cleanup immediately and through the incumbent supervised resource sweep. Cleanup uses bounded pages, a rotating cursor and a time budget. Existing native profile observation/reset gates own confirmed close, safe path and exclusive lock, last owner check and verified absence. Canonical no-follow workspace purge preserves external link targets. A canonical Sandbox lease query includes mounts hidden by legacy enumeration; any live or uncertain mount keeps workspace bytes. Logical retirement remains committed through logout, caller cancellation, native failure or restart. Its acknowledgement reports `cleanupStatus=completed|pending` separately. This technical retirement mandate is deliberately retryable; it is different from automatically replaying an uncertain user Browser reset.

Authenticated GET `/api/v2/tenants/{tenantId}/agents/{instanceId}/decommission` and strict-body DELETE `/api/v2/tenants/{tenantId}/agents/{instanceId}` expose the backend flow. No old owner-capability alias is added. Migration `20261020090000_TenantAgentDecommission` adds irreversible-status guards on existing tables without a new owner table, column, ledger or model snapshot. Empty downgrade/upgrade preserves model parity; populated downgrade refuses.

## Focused local evidence

**40 distinct passing cases**, with focused repeats counted once: 36 Infrastructure cases and four API cases. No skipped case is counted as a pass.

| Check | Result | Observed boundary |
| --- | --- | --- |
| `TenantAgentDecommissionTests` | 22 passed | Both stores; active/archive preflight; current human/reader/agent denial; commit-time logout/password/security race; post-metadata logout; grant/binding retirement; scoped profile/home deletion; external/sibling preservation; same-operation readback; retained deleted Session owner; unknown cleanup after logout/store reopen; hidden mount refusal and later clearance; pending reset; SQLite raw revival/delete/nullable-owner guard, downgrade refusal, empty down/up/model parity. |
| Actual native decommission journey | 2 passed | Disposable Chromium with InMemory and SQLite ownership: deleted canonical Session and archived Agent still have an actual live persistent context; decommission confirms its closure/profile absence and preserves the other Agent's live context/profile. |
| Existing IAM Instance/Account regressions | 6 passed | Actual Instance identity/inventory/archive/revocation, Account/status/stale transaction fences and same-Account tenant/system separation in both stores. |
| Definition retirement batch | 6 passed | Two new decommission-marker/Definition-release cases plus four incumbent exact-witness and retained Instance/Session/Activation protection cases. |
| Decommission HTTP | 3 passed | Cookie/CSRF and strict body, active-lifecycle rejection, anonymous denial, coherent preflight, exact profile/home purge, sibling preservation, hidden inventory/restore denial, same-operation facts and actual SQLite application-host recreation after committed unknown cleanup. |
| Identity SQLite restart regression | 1 passed | Production Synthetic registration, retained cookie, tenant authority and Instance identity through host recreation with the new module registered. |

Executed batches:

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~TenantAgentDecommissionTests|FullyQualifiedName~Decommission_closes_actual_owned_native_profile|FullyQualifiedName~Actual_instances_have_atomic_identity_scoped_inventory_and_archive_revocation|FullyQualifiedName~Instance_identity_transactions_recheck_account_status_and_lose_stale_races_without_orphans|FullyQualifiedName~Same_account_has_distinct_tenant_authority_and_no_platform_or_agent_shortcut' --nologo
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Decommissioned_owner_tombstone|FullyQualifiedName~Archived_instances_and_retained_session_activations_block_definition_deletion|FullyQualifiedName~Definition_deletion_uses_all_revision_witnesses' --nologo
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~TenantAgentDecommissionTests' --nologo
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Tenant_Agent_decommission|FullyQualifiedName~IdentitySqliteRestartTests' --nologo
```

The first combined Infrastructure batch passed 28 cases before the two final post-metadata login cases were added. The final module-only run passed all 22 module cases; the native/IAM and Definition checks above retain their separate measured scopes. API tests compile the actual application and run the authenticated flow; this is functional backend verification, not a health/build-only claim.

## Corrections and limits

Initial compilation caught a TimeProvider budget parameter and fixture/property signature mismatches; these were corrected before functional execution. Initial functional assertions tried to read a deliberately hidden deleted Session through its live projection and used lowercase raw GUID SQL that matched no identity row. They now inspect retained ownership explicitly and parameterize GUIDs. The native/HTTP flows passed after those corrections.

Review then found that legacy Sandbox enumeration suppresses tenant-owned leases. Workspace cleanup now checks the canonical lease owner before purge, with a regression staging a controlled durable orphan mount and proving refusal until termination. Another integration edge was corrected: keeping the owner row must not permanently block an otherwise unreferenced Definition's deletion. Both stores now exclude only retired Instance markers from that count, while incumbent reference blockers still pass. The final preflight login recheck closes a logout window after its last metadata read.

The hidden-mount case stages controlled durable lease facts; it does not allocate or terminate real Docker. The native journey executes real disposable Chromium but no live model/provider, enterprise target or external account. Host-restart evidence recreates the actual SQLite application host after a controlled native cleanup failure, rather than claiming a physical process kill. Frontend code was unchanged and no frontend browser acceptance is claimed. Full CI and hosted CI were not requested or run. Tests used exclusively disposable database, profile, workspace and key roots; no user data or user host was reset/restarted.
