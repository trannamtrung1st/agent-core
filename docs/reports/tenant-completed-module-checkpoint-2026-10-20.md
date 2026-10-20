# Completed tenant module checkpoint — 2026-10-20

Owner request: commit completed modules before continuing unfinished work. Parent: `4273ac179052873a1ab7e2accc2e59f3de58436e`.

## Scope and staging boundary

This checkpoint groups the coupled completion inbox/wait/read-consent paths, human-attributed background Browser/Sandbox operations, connected-account human/model reads and exact actions, account discovery/reconciliation/recovery, and the qualified Schedule/manual Automation foundation. Existing module reports retain their exact evidence and acceptance limitations. Shared approval discriminators, Runtime/tool contracts, DI, transactional stores and the sequential EF migrations make a single coherent checkpoint preferable to independent file-only commits that would not compile.

Staging used an isolated source snapshot and separate Git index. The live working source files were not replaced or reset. New autonomous protected-file offering/policy changes and their unfinished API tests remain outside the commit. Automation task identity and source recognition required by the foundation remain included, while the tool-shape gate retains human-attributed protected-task restrictions. A first snapshot omitted part of the required Automation context plumbing; focused API execution exposed that omission. The corrected snapshot preserves it. No failing initial-snapshot result is counted as passing verification.

The Automation foundation remains deliberately bounded to Schedule/manual sources, a new Background Session and no completion destination. Admission Review, autonomous protected tools, event/webhook/watch ingress, reporting/destinations and Automation UI remain open. Tenant voice, remaining System/audit and interaction paths, final obsolete-authority/schema removal and proposal-wide acceptance remain open. No live data reset, host restart, external-provider operation, deployment, CI or push is part of this checkpoint.

## Fresh snapshot verification

- Selected application contract/projection tests: 75 passed on the corrected snapshot.
- Shared approval and account-action UI tests: 114 passed across `TenantApprovals.test.tsx` and `AccountActionPreview.test.tsx`. The existing NaN-height warning remains non-failing.
- Frontend production build: passed. Existing SignalR annotation and bundle-size warnings remain.
- Focused API journeys: 66 passed on the corrected snapshot (4m17s). The scenarios exercise owned terminal-completion consumption, exact connected-account consent including denial/revocation, and actual scheduled/manual Automation outcomes across InMemory and SQLite.
- Separate staged-index whitespace check: passed before commit.

These fresh checks supplement the individual module reports; they are not full proposal, live-provider, deployment or tenant-isolation acceptance.

## Commands and retained local logs

Run from an isolated copy of the staged tree:

- `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantBackgroundFileObservationTests|FullyQualifiedName~TenantWaitContractTests|FullyQualifiedName~TenantCompletionContractTests|FullyQualifiedName~TenantAccountActionContractTests|FullyQualifiedName~TenantAccountReadContractTests|FullyQualifiedName~TenantAccountOperationReadContractTests|FullyQualifiedName~TenantCredentialDiscoveryContractTests' --nologo --verbosity quiet -m:1 -p:UseSharedCompilation=false`
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_completion_inbox_actual_model_reads_takes_and_acknowledges_only_with_durable_parent_answer|FullyQualifiedName~Model_connected_account_read_waits_for_exact_consent_and_returns_only_current_owned_data|FullyQualifiedName~Tenant_automations_use_current_human_scope_owned_rows_fresh_occurrence_roots_and_actual_worker_outcomes' --nologo --logger 'console;verbosity=normal' -m:1 -p:UseSharedCompilation=false`
- `pnpm --dir web run test --run src/features/access/AccountActionPreview.test.tsx src/features/access/TenantApprovals.test.tsx`
- `pnpm --dir web run build`

Corrected backend logs: `/private/tmp/agent-core-completed-application-tests.log` and `/private/tmp/agent-core-completed-api-tests.log`. These are local ephemeral evidence; module reports remain the durable detailed workflow records.
