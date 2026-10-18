# Tenant current-policy read recovery — 2026-10-18

Baseline: `9a28592b00b932ab4f1f31ea7e4193e0c284636b`. This advances the full multi-tenant IAM proposal's current-authority and reliable execution requirements. The full goal remains active; this is no milestone acceptance.

## Finding and correction

`TenantRunExecutionAuthority.CurrentDefinitionAsync` correctly discarded a policy captured across a tenant security revision change, but immediately returned unavailable even when the change was unrelated and current authority remained valid. A deterministic source-read hook reproduced this in both InMemory and SQLite: creating an unrelated role during lookup returned no policy.

The method now permits one fresh read after discarding that raced snapshot. Each attempt resolves the exact saved Run, immutable owner/context, current executor/requester, active lifecycle, matching claim generation and unexpired lease before and after current publication/Instance lookup. A second revision change returns unavailable. Actual authority loss is never retried into permission, caller cancellation propagates, and neither cached policy nor admitted policy supplies a fallback. The caller still applies admitted and current restrictions independently. Model calls, effects, approval decisions and grants are not replayed by this method.

The [interfaces](../04-backend-interfaces.md#current-tenant-execution-policy-resolution), [backend behavior](../12-backend-implementation-spec.md#tenant-current-policy-enforcement) and [test strategy](../16-testing-strategy.md#tenant-current-policy-read-recovery) own the final contract. No schema, route, worker, transport, frontend or design-system change was introduced.

## Executed journeys

- Both stores: change an unrelated role once during source lookup; discard the captured policy and return a coherent result after exactly two source reads. Run revision/status, claim, trusted context, admitted instructions/home restriction and input graph remain unchanged.
- Both stores: change the Instance Home policy during lookup through the existing atomic configuration owner; the returned policy contains the fresh Disabled restriction. Original admission remains pinned.
- Both stores: revoke the actual requester's Chat/history role during lookup; return unavailable after the first source read. Read cancellation propagates without another lookup.
- Both stores: cancel the Run during lookup; return unavailable. Change the revision during both attempts; stop after two reads, and a later independent stable read can recover.
- Hosted Synthetic: sign in, create an owned Instance, enable Managed preference auto-apply, grant the actual agent its own read/update authority, create a Session and submit text through HTTP. After the model proposes the typed preference call, interleave one actual IAM role change during policy lookup. The normal worker/Runtime completes with the expected Vietnamese/concise settings, unchanged persona, one effect receipt/audit, no approval and the normal two model requests. No model/effect retry substitutes for the read recovery.
- Paired hosted cases retain the ordinary allowed and read-only-executor outcomes in both stores. The read-only executor completes with RequiresApproval/applied=false and no mutation, receipt or approval creation.

## Verification results

The final selections passed **38 distinct cases: 24 Infrastructure and 14 API**, with zero failures or skips. The Infrastructure run took 36s; the API run took 57.4s. Both projects compiled successfully during the focused checks. Earlier 12-case store and six-case hosted runs overlap these final selections and are not double-counted.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Current_policy_read_|FullyQualifiedName~Requester_context_uses_actual|FullyQualifiedName~Requester_role_hints_|FullyQualifiedName~One_Account_in_two_tenants_has_distinct_requester_|FullyQualifiedName~Tenant_Run_current_authority_' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Model_managed_preferences_use_current_authority_and_direct_receipts_through_worker_and_Runtime|FullyQualifiedName~Tenant_model_receives_current_actual_requester_across_tool_rounds_and_durable_decisions|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive_SQLite_host_recreation_before_human_decision' --logger 'console;verbosity=normal'
```

The API binary was rebuilt and the six preference cases passed immediately before the final no-build regression; only Infrastructure test assertions changed afterward. Final Infrastructure checks also retain current Account suspension, expired/replaced claims, same-Account distinct tenant role hints and later publication adoption. Final API checks retain requester refresh/revocation, distinct approved continuation identity and exact persona/preference approval recovery across SQLite host recreation. Expected revoked-requester journeys fail the Run before another model call; their existing provider-unknown terminal classification is unchanged by this correction.

Logs: `/private/tmp/agent-core-policy-read-infrastructure-final-20261018.log` and `/private/tmp/agent-core-policy-read-api-final-20261018.log`. Deterministic reproduction and intermediate check logs use the same `agent-core-policy-read-` prefix. Changed Markdown links/fragments/fences and `git diff --check` passed. All test handles are terminal; hosted factories disposed their own Synthetic hosts.

## Historical failure and limits

The earlier one-off SQLite direct-preference provider-unknown failure did not reproduce in the initial four-case baseline. The new deterministic revision race is established independently; its correction does not prove the historical failure's cause or close that follow-up. Historical reports retain their evidence scope.

The first sandboxed runner built successfully but could not bind its local test-runner IPC socket; execution was repeated outside that restriction. Initial new fixture errors (missing import, reference equality across SQLite materialization and the required empty Automation policy plan for a settings transaction) were corrected. Before the production change, both unrelated-change cases failed the expected non-null assertion; those reproductions also exposed the separate fixture comparison error. No failed run is counted as a passing final check.

No source/test file was edited while a build/test process was live. All test data is disposable; no user data, Real host, live provider/registry/SDK, CI, deployment or reset was used. Existing resource/effect families, Governance/platform, voice/rich Chat, input arbitration, autonomous origins and final authority/schema consolidation remain required by the full proposal. UI/visual behavior was unchanged and was not requalified by these backend checks.
