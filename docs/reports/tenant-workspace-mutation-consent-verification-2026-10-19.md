# Exact workspace mutation consent checkpoint — 2026-10-19

This continues the full proposal from source-witness checkpoint `beebdfc6`. It adds the exact typed consent, compound decision and persistence boundary. It does not expose a mutation authorization intent to models or execute approved mutations. The full goal remains active.

## Contract and ownership

`PreparedWorkspaceMutation` saves actual Session, supported tool, closed canonical supplied arguments, private observation hash, derived primary scope/permission and required workspace scopes. Rebuilding rejects forged derived fields, malformed hashes, noncanonical arguments and mixed payloads. Exact action hashes bind mutable witnesses; semantic root correlation deliberately excludes those witnesses while retaining exact tool/Session/arguments.

Trusted internal preparation uses the current claimed Run and canonical private source observation. It may omit only the primary delegatable mutation permission. Parent reads, patch/copy source reads and secondary mutation permissions remain raw independent executor authority. Human visibility requires every underlying target plus normal approval visibility. Deciding humans require the corresponding current underlying permissions and approval scopes; current compound independent-review policies apply. Only an exceptional primary mutation creates an exact one-time grant. Decisions leave files and effect receipts unchanged.

The existing approval stores own request/decision/grant/signal transactions. Both stores repeat actual owner/configuration/compound executor and reviewer checks. Home/working source and destination visibility filters execute before inbox count/paging. SQLite extends only the incumbent approval table constraint and refuses downgrade with retained mutation consent. Domain rebuilding additionally verifies complete derived scope coverage.

The direct Allowed mutation path now checks the same root correlation before current policy, inside atomic dispatch reservation and at native dispatch gates. Earlier refusals remain binding after source or policy changes. Correlation uses fixed dummy witnesses only to compute semantic identity; those values never validate sources or authorize effects. Known direct receipts still return factual outcomes without native replay.

## Representative executed evidence

The initial eight store journeys passed across memory/SQLite, home/working and Review/exceptional grants. They prepared a blind literal overwrite without workspace read, persisted a waiting Run, inspected only supplied replacement text, approved the exact action, recorded the expected grant/signal and verified unchanged original bytes with no receipt or native dispatch. `/private/tmp/agent-core-workspace-mutation-review-first.log` records 8/8.

The typed contract suite passed 9/9 (`/private/tmp/agent-core-workspace-mutation-approval-contract-fixed.log`), covering all seven tools, derived primary/source/secondary scopes, JSON round trips, forged fields, mixed payloads and stable refusal identity under changed witnesses. Initial compilation corrections were missing Ports imports in the SQLite helper and an ambiguous test assertion; neither run was counted as passing.

The first boundary run passed fifteen cases but two refusal fixtures attempted a policy weaker than Core permits. They were corrected to relax Always to the supported requester-aware minimum. A later run passed twenty-three cases, including all eight compound independent-review scenarios; four failures were fixture mistakes (a different call ID than the pending checkpoint and `kind` instead of the structural contract's `op`). Those fixtures were corrected before final qualification. A separate missing test namespace import was corrected after its failed compilation. Earlier failed runs are not acceptance evidence.

The corrected focused run passed **64/64** (`/private/tmp/agent-core-workspace-mutation-review-qualified.log`): twenty-seven new consent cases plus thirty-seven existing source-preparation and direct-effect/recovery cases. Refusal tests rejected both the ordinary service attempt and a correctly checkpointed atomic store reservation after current policy relaxed and current file bytes changed. Private-source tests rejected same-length/timestamp-restored replacements without displaying their bytes. Scoped cross-root tests required independent source read, destination write and home-parent access before returning any item or count. The independent-review matrix qualified each of write/source-read/Session-read/Agent-read policy through both stores. SQLite upgrade/reopen, eight malformed/mixed payload variants and retained-consent rollback refusal passed.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_mutation_Review|FullyQualifiedName~Tenant_workspace_effects|FullyQualifiedName~Tenant_workspace_mutation_target_' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The shared approval follow-up passed **56/56** (`/private/tmp/agent-core-workspace-mutation-consent-shared-regression.log`). Four added mixed-batch cases persisted Review or exceptional delete consent while requiring independent write authority, recorded the expected exact grant and verified no deletion, directory creation or receipt after the decision. Fifty-two existing cases rechecked ordinary/exact grant decisions, expiry/qualification CAS, lineage/schema corruption, scoped inventory counts, Workspace saved-read consent, Browser compound visibility/SQLite upgrade and Credential metadata visibility/upgrade. This is selected shared-boundary coverage, not the full suite.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_mutation_Review_mixed_batch|FullyQualifiedName~Durable_approval_retains|FullyQualifiedName~Durable_decision_commits|FullyQualifiedName~Approval_schema_rejects|FullyQualifiedName~Approval_inventory_filters|FullyQualifiedName~Workspace_read_Review_|FullyQualifiedName~Tenant_Browser_Review_filters_scope|FullyQualifiedName~Tenant_Browser_SQLite_upgrade|FullyQualifiedName~Credential_metadata_Review_inbox|FullyQualifiedName~Credential_metadata_SQLite_upgrade' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The actual hosted Synthetic model mutation matrix passed **16/16** (`/private/tmp/agent-core-workspace-mutation-consent-hosted.log`). Both stores retained direct write/patch/mkdir/copy/batch behavior, known and uncertain outcome handling, current IAM/policy refusal, HomeAccess narrowing after admission, malformed-input rejection, unavailable settlement and completed SQLite host recreation. The model continued with bounded results rather than replaying uncertain native work. Provider HTTP attempts were zero. This checks the existing direct model path after adding its required approval-store dependency and new schema; it does not verify an unimplemented approved mutation journey.

```sh
dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_workspace_model_mutations_HTTP' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

The contract suite passed **9/9 again** against final source (`/private/tmp/agent-core-workspace-mutation-consent-contract-final.log`). Distinct focused qualification totals **145 passing cases**: 120 store/source/effect/shared-approval cases, sixteen hosted Synthetic cases and nine contract cases. Repeated runs are excluded. These are development checks, not full proposal or milestone acceptance. Changed canonical documents were synchronized; relative file links and fenced blocks were checked, and `git diff --check` passed.

`dotnet ef migrations has-pending-model-changes --no-build --project src/AgentCore.Infrastructure` returned success with no pending model changes (`/private/tmp/agent-core-workspace-mutation-consent-model-check.log`). The installed EF tools/runtime version advisory remains informational. All build/test/EF handles returned terminal success before commit; 709 relative documentation file links and balanced fenced blocks passed the final check.

```sh
dotnet test tests/AgentCore.Application.Tests --no-restore --filter 'FullyQualifiedName~TenantWorkspaceMutationApprovalContractTests' -m:1 -nodeReuse:false --logger 'console;verbosity=minimal'
```

## Remaining full-proposal work

Approved mutation start/claim/grant consumption, canonical off-mailbox I/O, immutable settlement, abandoned-dispatch recovery, exact successor/checkpoint projection, bounded model intent and shared approval inspector remain required. Batch execution must preserve its coarse ordered-prefix/possible-partial-effect semantics without rollback or replay. No model mutation authorization intent is offered by this checkpoint.

Workspace virtual resources/Office/source publication, autonomous origins, other proposal families and final authority/schema consolidation also remain open. No installed/user database migration/reset, Real host restart/deployment, live provider, CI or full suite was used. No frontend source changed and no visual acceptance is claimed.
