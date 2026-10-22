# Tenant Automation Experience maintenance execution — 2026-10-22

## Delivered scope

The Synthetic tenant Experience adapter now exercises one bounded consolidation through the same configured Automation, current owned Experience projection, protected tool, exact approval and continuation path as ordinary execution. It reads the typed Run task directly rather than appending trigger evidence to the command. It selects two actual observed Instance candidates, submits their literal bounded replacement, requests exact consent and reports the saved applied or refused result without replay. It cannot supply ownership, raw reads, opt-ins or approval authority.

This qualifies canonical maintenance execution, preserving the current tenant contract: consolidation always needs exact human consent; the default-off maintenance pin intersects current settings and unfrozen Experience/continuity policy. Independent roots maintain Instance Experience and never acquire creator/invoker User ownership or private learned Memory. Primary-only one-time manage authority supplies no source reads. Human review presents complete current sources and the exact replacement. Normal transaction, source, reviewer, receipt, root and successor guards own the result. The integrated rejection path exposed and corrected work.complete classification: typed unapplied Experience refusals, factual denied mutation receipts and source-only consent results are not completed mutations. Successful, already-completed and uncertain effects still reject NoAction; arbitrary tool data cannot supply this classification. No schema, scheduler, outbox, preset authority or frontend change is introduced.

## Executed evidence

The final focused groups total **170 distinct passing cases**, with no selected skips. Repeated runs are not counted again.

| Check | Result |
| --- | --- |
| Final solution build | Pass; zero warnings/errors; 21.55s |
| New actual maintenance/source-consent runtime matrix | 18 passed; 1m14s |
| Existing Experience, route inventory and SQLite Activity recovery | 23 passed; 1m14s |
| Completion, maintenance, independent-task, approval and checkpoint contracts | 112 passed; 188ms |
| Existing Synthetic tool/authorization adapter regressions | 17 passed; 157ms |
| Focused grant investigation repeats | Both store variants passed three consecutive runs; six repeated cases |

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`, log `/private/tmp/tenant-experience-maintenance-solution-build.log`.

All tests use the respective repository project with `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`. Runtime filter: `FullyQualifiedName~Tenant_experience_Automation_maintenance_|FullyQualifiedName~Tenant_experience_source_review_can_finish_NoAction`. The API regression filter selects the other `Tenant_experience_` cases, canonical retired-route inventory and `Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation_without_replay`, excluding the new runtime matrix. Application selects `TenantIndependentTaskContractTests`, `TenantBackgroundCreationContractTests`, `TenantBackgroundReportContractTests`, `TenantExperienceContractTests`, `TenantContinuityContractTests`, `TenantApprovalToolContractTests`, `AgentRunToolCallCheckpointTests`, `WorkCompletionRequestTests`, `BackgroundCompletionDeadlineTests`, `BackgroundCompletionProjectionTests` and `IdentityMaintenanceToolContractTests` using `FullyQualifiedName~`. Infrastructure selects `InMemoryAndScriptedTests|SyntheticAuthorizationAuditTests` using the same property.

TRX files are under `/private/tmp/tenant-experience-maintenance-results/`: `experience-maintenance-qualified-flow.trx`, `experience-maintenance-regressions.trx`, `experience-maintenance-contracts-final.trx`, `experience-maintenance-synthetic-final.trx` and the three `experience-maintenance-grant-repeat-<n>.trx` files. Logs have the corresponding `/private/tmp/tenant-experience-maintenance-` names. Grant repeats use `FullyQualifiedName~Tenant_experience_Automation_maintenance_&DisplayName~grant`; discovery confirmed exactly the two store variants before execution.

The first matrix exposed a frozen-clock fixture issue: approved decisions had saved pending signals, while the periodic dispatcher had not advanced. The fixture now calls the production durable dispatcher before worker execution. Its confirmed owned first runner was interrupted after this failure was established; unrelated processes were untouched. The next matrix reproduced the actual rejected-consolidation completion bug, then the final classifier also covered read-only source consent. One interim InMemory grant run timed out without a terminal-state diagnostic; that original stall was not root-caused. Status/retry diagnostics were added, and the final complete matrix plus three focused two-store repeat runs passed. This timing observation remains a recorded limitation rather than a claim that it never occurred.

Changed-document relative links/fences and `git diff --check` pass.

The actual HTTP/model matrix creates two separate Instance Experience facts through normal source/record tool execution, then uses the real ScriptedLanguageModel for the affected maintenance phase. Cases cover manual and scheduled roots, exact approval and primary manage grant, rejection, default-off maintenance, source suppression and current opt-out while review is pending. They assert full source/replacement review, actual request ownership, no premature supersession, factual successor outcome, one retained replacement with two superseded sources, grant consumption and no repeated effect. Four additional source-consent cases approve or reject exact inspection and finish NoAction without ever recording a fact. Both InMemory and temporary SQLite use the same path.

## Remaining scope

This closes the exercised canonical Automation Experience maintenance execution slice. Preset authoring/orchestration, event-triggered Experience qualification, remaining Harness/internal/schema consolidation and exhaustive proposal inventory/qualification remain open. This is focused local Synthetic evidence; no hosted CI, full local CI suite, live providers, new browser or milestone-wide acceptance was requested or run. The original goal remains active.
