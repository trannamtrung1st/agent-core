# Retired conversation Run content — 2026-10-26

The feedback's startup-migration finding was explicitly withdrawn. `SqliteMemoryStore.EnsureCreatedAsync`, API persistence startup and the Self-Management resolver remain unchanged. Supported installed databases still require stopped-host backup and explicit offline EF migration; normal startup remains fail-closed. The earlier [DeepSeek snapshot update](deepseek-flash-1017-verification-2026-10-26.md) ships with this change, with its upgrade instructions corrected to this policy.

## Reproduction and implementation

Before the fix, both SQLite deletion and expiry regressions failed: a completed Temporary conversation retained a distinctive private marker in `AgentRuns.PayloadJson`, including progress and its serialized interaction checkpoint. The regression submits an actual owned message, claims its Run, writes a valid tool transcript with user text, arguments/results and an opaque continuation, then completes the Run before deleting/expiring the Session.

Both Session-store deletion transactions now minimize settled Run conversation data. A typed terminal-only Domain operation removes transcript/continuation copies, mutable progress/result/failure narratives and settled direct-operation argument copies. It retains immutable admission/configuration and model/lineage identity, outcome/entry/continuation references, timestamps, accounting, failure code/diagnostic identity, action hash/disposition/start provenance and exact approval references. The checkpoint becomes a non-runnable tombstone with original counters. Repeated minimization does not advance revisions again.

Prepared/InFlight/Indeterminate effects preserve their exact recovery context and defer successful retirement; manual deletion returns `session_effect_reconciliation_pending`. Due retirement preserves existing factual cancellation/expiry behavior and can still minimize unrelated settled Runs. Pending outgoing reports retain the result body required by their consumer until Handled/Delivered/Skipped. Exact immutable approval operations, grants and receipts are not rewritten.

The existing working cleanup service also invokes owned Run minimization before checking native deletion eligibility. This repairs pre-fix deleted owners across restart, independently of a held sandbox lease, child settlement or a filesystem failure. Native bytes still require the incumbent complete settlement/lease/path witnesses. There is no new worker, deletion ledger, schema migration, reset or execution authority.

## Observed verification

Final focused verification: **88 passed, zero failed/skipped** — 34 Infrastructure, 53 Domain and one API restart test. The Infrastructure run includes canonical startup, Self-Management, Chat retention and DeepSeek snapshot migration checks.

- Both stores: manual deletion and exact 24-hour expiry remove the private marker from retained Run payloads and entry rows; reopen and repeated cleanup retain the result. Seeding an old full payload beneath an already deleted owner is repaired by the existing recovery pass without another Run.
- Both stores: uncertain action checkpoint bytes remain unchanged, manual deletion refuses, due work becomes factually Cancelled/Expired and recovery remains deferred. A settled old transcript is removed while an unrelated unconfirmed native lease and its working bytes remain protected.
- Both stores: perform an actual approved persona effect, settle its Run, then delete its Session. Original approval and receipt serialization, action hash/disposition, admission, outcome and accounting survive; the private checkpoint/result marker does not.
- Actual SQLite application recreation: complete a Synthetic HTTP conversation, delete it, seed a valid pre-fix checkpoint into its retained Run, stop the application and start a second host on the same database. Startup cleanup removes the checkpoint and working directory. Exactly one original Run remains; no new Run is admitted. **1 passed**.
- Domain Run/Management policy invariants: deferred report body minimization, idempotence and Definition adoption with saved source/tool eligibility/freeze and inherited unwrapped defaults. **53 passed**. The resolver's behavior is retained.

Final API and Infrastructure test-project builds passed with zero warnings/errors. The Domain test project also built successfully. An intermediate parallel build had a transient assembly-copy retry; final builds ran sequentially. Initial new test-fixture errors were corrected (serialize tenant Runs through their persistence mapping; explicitly grant fixture Space read; seed a tool checkpoint for a one-call Synthetic response). No product assertion was removed to obtain a pass.

Exact focused commands, from the repository root after the builds:

```sh
dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~AgentRunContractTests|FullyQualifiedName~ManagementPolicyRestrictionTests' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantChatRetentionTests|FullyQualifiedName~Session_deletion_minimizes_settled_Run_content|FullyQualifiedName~Tenant_working_cleanup|FullyQualifiedName~ChatRetentionMigrationTests|FullyQualifiedName~SelfManagementMigrationTests|FullyQualifiedName~CanonicalStartupMigrationTests|FullyQualifiedName~DeepSeekFlash1017' --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~Tenant_working_cleanup_SQLite_application_restart --logger 'console;verbosity=minimal'
git diff --check
```

The affected API/Infrastructure builds use `dotnet build <test-project> --no-restore --disable-build-servers --nologo -m:1 -p:UseSharedCompilation=false -v minimal`. The local test-runner socket requires permitted execution outside the restricted sandbox. Final raw logs are `/private/tmp/agent-core-retention-infrastructure-final.log` and `/private/tmp/agent-core-retention-api-restart-final.log`.

## Limits

This minimizes logical conversation copies, not immutable admitted task/configuration or approval/effect evidence, SQLite WAL/backups or external destinations. Unresolved effects and pending reports retain the evidence stated above until their normal reconciliation/accounting. No live providers, user database, installed application, browser UI or hosted CI were exercised or changed. No milestone acceptance is claimed.
