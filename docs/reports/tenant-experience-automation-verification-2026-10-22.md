# Tenant Automation Experience and source privacy qualification — 2026-10-22

## Delivered scope

Manual and scheduled tenant Automations execute the existing Experience source/record tools as their own Agent SystemIdentity. The resulting facts are Instance-owned; subsequent Automation context can recall those facts without acquiring the creator/invoker’s private Profile, learned Memory or Experience. A human can inspect an otherwise readable terminal Run containing only Instance Experience without converting its evidence to private ownership. A later human record retains that human’s separate private ownership.

Run-source classification now resolves the actual retained Experience fact owner metadata after current output-source authorization, under the same InMemory locks or SQLite transaction. It no longer equates every Experience fact reference with private human data. Private Profile, learned Memory, private Experience facts and private Run-source witnesses retain their actual causal User. A Run that derives its result from a private source cannot lose that attribution just because it has no direct private Profile/Memory/fact reference. Both model inspection and human review preflight enforce the same rule. Missing metadata, invalid actual owners and changed/suppressed proofs fail closed. Existing raw-read, opt-in, source/record consent and output fences remain independent. There is no schema, UI, scheduler or origin change.

## Executed local evidence

The focused final groups total **184 distinct passing cases**, with no selected skips. Repeated runs are not counted again.

| Check | Result |
| --- | --- |
| Solution build | Pass; zero warnings/errors; 49.22s |
| Final API test-project build after fixture completion | Pass; zero warnings/errors; 9.22s |
| Experience/continuity/protected Memory store transactions | 88 passed; 2m48s |
| Independent task/background creation/report, Experience/continuity/approval/checkpoint contracts | 73 passed; 153ms |
| Experience HTTP/model/Automation, canonical route inventory and SQLite Activity recovery | 23 passed; 1m12s |

Build commands use `dotnet build <solution-or-project> --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs: `/private/tmp/tenant-experience-automation-build.log` and `/private/tmp/tenant-experience-automation-qualified-build.log`.

Each test command uses `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`, with TRX under `/private/tmp/tenant-experience-automation-results/`. Infrastructure filter: `FullyQualifiedName~Tenant_experience_|FullyQualifiedName~Tenant_continuity_|FullyQualifiedName~Tenant_memory_operation_`. Application filter: `TenantIndependentTaskContractTests|TenantBackgroundCreationContractTests|TenantBackgroundReportContractTests|TenantExperienceContractTests|TenantContinuityContractTests|TenantApprovalToolContractTests|AgentRunToolCallCheckpointTests`, each using `FullyQualifiedName~`. API filter: `FullyQualifiedName~Tenant_experience_|FullyQualifiedName~Tenant_HTTP_cutover_removes_all_228_retired_routes_even_for_original_tokens_and_platform_operators|FullyQualifiedName~Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation_without_replay`.

TRX files are `experience-automation-store.trx`, `experience-automation-contracts.trx` and `experience-automation-http.trx`; logs have the corresponding `/private/tmp/tenant-experience-automation-` names. Baseline reproduction results are retained separately as `experience-automation-first.trx` and `experience-source-privacy-first.trx`. Changed-document relative links/fences and `git diff --check` pass.

The eight new HTTP/model cases cover both stores and manual/scheduled roots: record and recall Instance facts, inspect the Instance-only terminal result, create a separate human private copy, suppress the original fact and refuse later source reuse. They also cover independent refusal of private human Run evidence and creator Experience, and a human Run containing only Instance evidence remaining readable to an Automation even after the creator Profile is populated. Model contexts and retained source witnesses are inspected, not just HTTP status.

Two new store cases construct an actual private Memory-backed terminal source, derive another substantive Run from its Experience source witness without direct private Memory/fact reads, then verify that raw-read/creation-capable peers cannot inspect or adopt it while its actual human can. Existing Experience, continuity, protected Memory, source/record consent and Activity recovery cases remain in the grouped regression scope.

Baseline execution reproduced the Instance-only refusal in both manual Automation store variants and the missing transitive private attribution in both stores. Existing independent private-source adoption cases already refused. The first fixed manual cases passed; scheduled fixture assertions initially mistook a zero local admission count for missing work. The running hosted scheduler had already admitted the same occurrence (Completed, occurrence count1, exact saved graph). The final fixture asserts the single saved Automation graph regardless of which caller wins the existing admission gate. No scheduler behavior was changed.

All runtime evidence uses WebApplicationFactory, actual tenant HTTP/configuration/Automation admission, real model-worker tool dispatch and isolated InMemory/temporary SQLite. It uses no provider credentials. No frontend behavior was changed; this module does not claim a new browser qualification.

## Remaining scope

This qualifies the exercised manual/scheduled Automation Experience and terminal-source ownership paths. Event-triggered Experience orchestration, autonomous maintenance, remaining Harness/internal/schema consolidation and exhaustive proposal-wide inventory/qualification remain open. The preceding manual-review and Experience reports remain historical evidence, superseded only for this module’s ownership/source scope. No full CI suite, hosted Actions, live providers or milestone-wide acceptance was requested or run. The original goal remains active.
