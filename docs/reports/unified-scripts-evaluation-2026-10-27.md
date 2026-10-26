# Unified Scripts and Evaluation delivery — 27 October 2026

The unified tenant capability and legacy tenant cutover are implemented and locally verified. The proposal baseline is `8f3e379e3cad4a376e481e3cb60aa4f56e0af452`. Delivery preserves parent `9bc029386e4e98e9d2aeffba3cc5e39a03d8bb4d`, which adds one TODO note and has identical application/test source. This report qualifies the delivered local behavior; it does **not** accept another application milestone, arbitrary native Sandbox execution, hosted model quality, or production isolation. Verified implementation source: [`c36dfabe7d7cbbcba479a98c5fc8a47cf390f528`](https://github.com/trannamtrung1st/agent-core/commit/c36dfabe7d7cbbcba479a98c5fc8a47cf390f528). The following verification-only commit changes this ledger, with no application/test-source changes.

## Delivered ownership and behavior

| Concern | Canonical owner and delivered behavior |
| --- | --- |
| Catalogs | Immutable tenant Script and Suite versions, exact references/hashes, bounded content, archival, independent read/manage/execute permissions. Existing InMemory/SQLite IAM stores implement `ITenantEvaluationStore`. |
| Attempts | Durable reservation before evaluation; exact subject/revision/fingerprint/Suite/model pins; current authority and source CAS before settlement; trusted commit ordering; duplicate request convergence; immutable final findings; expired reservations become Indeterminate without replay. |
| Definition publication | Common associated/reusable Suites replace tenant scenarios/results. Criteria saves advance draft and Suite atomically. The existing publication transaction checks current required evidence and freezes associated criteria. Fork clones publication criteria to a new associated v1. Missing, stale, failed or uncertain required evidence cannot authorize publication. |
| Scripts | Closed offline Synthetic predicates preserve the existing five draft checks. Additional Run predicates observe bounded saved metadata. Saved Sandbox bodies return `sandbox_evaluation_authority_unavailable` / Inconclusive; no host process or substitute effect executor exists. |
| Run hooks | Exact Definition and governance bindings are frozen by existing Run admission. BeforeRun shares one bounded precondition budget across bindings and uses the existing model-pump/mailbox failure path. AfterRun/OnFailure attach read-only work to the incumbent supervised coordinator. Only required preconditions constrain factual admission. Denied optional/terminal observations remain unverified. Factual completion, failure, cancellation, receipts and transcript remain unchanged. |
| Model judge | Explicit eligible offering, protected current route/credential authority, fresh application capability, tool-free request, bounded tokens/time/output, strict outcome JSON. No fallback provider, retained reasoning or copied model rationale. Findings identify the exact judge route/version and remain judgments. |
| Manual review | Signed-in human, current subject/Suite/Script read, human-only review permission, exact pending check and revision CAS, explicit outcome/reason and durable attribution. Deterministic failures remain unchanged. Advisory review cannot invalidate conclusive required findings. |
| Policies | Closed gate-specific policies; no IAM/Review replacement. `RequestManualAssessment` requires a declared Manual check; whether it is a blocking criterion is explicit in that check's Required flag. Instance settings currently inspect inherited exact Definition/organization bindings. |
| UI | One tenant Evaluations area for Suites/Scripts/Results; shared Suite/check/binding/evidence forms; converted draft publication panel including reusable Suites; inherited Instance view; governance defaults; verification in the existing Run inspector. Direct Ant Design v6 and incumbent layout/feedback/draft guards. |

Read authorization is repeated independently for the subject, Suite and referenced Scripts. Result collection filtering precedes paging/counts. Draft snapshots retain only the latest trusted witnesses needed by current checks and read SQLite history in bounded batches. A consumed draft retains an authorized publication/source-owner route to historical evidence. A deleted/expired/unreadable source cannot disclose retained findings. Neither result references nor passing outcomes grant execution authority.

## Clean cutover and recovery

The tenant scenario/result service, mutation ports, routes, DTO consumers and authoring UI are retired. There are no alias routes, dual tenant writers or compatibility switches. The existing pure Synthetic evaluator algorithm and legitimate non-tenant fixture stores remain separate, with tenant writes fenced. Sandbox Profile initialization/readiness and exact operation Approval remain separate incumbent facilities.

Migration `20261027010000_UnifiedEvaluations` adds common catalogs, attempts and governance policy with tenant-qualified keys/FKs, CAS, deduplication, expiry/status indexes and immutable history guards. Installed data does not silently upgrade on host startup. With the host stopped, use:

```sh
dotnet run --project src/AgentCore.Api -- migrate-evaluations /absolute/state.db /absolute/new-backup.db
```

Use the complete matching predecessor migration chain and a new backup path. The command backs up SQLite including committed WAL state, migrates, converts owned legacy draft rows, verifies FKs and removes old tenant children only after conversion. A pass remains usable only with exact source revision/fingerprint, criterion version, known Synthetic runtime, bounded valid findings, matching audit/timestamp and trusted commit order. Unproven/malformed results retain Inconclusive historical evidence. Untrusted or out-of-range original commit counters are not promoted to current ordering witnesses; they cannot permanently outrank fresh evaluations. Existing publications/Runs remain unchanged. Restore the matching database backup and binary on failure; destructive Down is deliberately refused. Only disposable copied/test databases were migrated here.

## Executed local checks

All test execution used cached/local dependencies, disposable data, Synthetic adapters or fake provider HTTP; no hosted inference or CI was invoked.

| Check | Result and observed scope |
| --- | --- |
| API build | `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false`: passed, zero warnings/errors. |
| Affected Infrastructure integration | Filter `FullyQualifiedName~Unified_\|FullyQualifiedName~Owned_definitions_publish\|FullyQualifiedName~Deleting_last_unpublished_draft\|FullyQualifiedName~Definition_deletion_uses\|FullyQualifiedName~Delete_rejects_foreign_targets`: 34 passed. Covers common stores, immutable publication/fork, independent source reads, CAS, duplicate/cancel/source-edit races, recovery/reopen, populated predecessor migration, model response schema/no-replay and actual Runtime hooks/governance. |
| Current authority/budget follow-up | `FullyQualifiedName~Unified_`: 26 passed after shared precondition-budget and Script settlement-authority corrections. Final `FullyQualifiedName~Unified_actual_Runtime` follow-up: 12 passed, including pending advisory review with a conclusive required precondition and denied optional/terminal observation authority without blocking factual execution. Installed-data ordering follow-up: 1 passed; final direct evaluation/publication HTTP follow-up: 2 passed. |
| Affected HTTP integration | `FullyQualifiedName~TenantDefinitionAccessTests&FullyQualifiedName!~Sandbox`: 143 passed. Covers cookie/CSRF tenant authoring, actual bytes, evaluation/publish/fork, resource scopes, original-login retirement and deletion/privilege boundaries. |
| Web behavior | `pnpm --dir web exec vitest run src/services/tenantEvaluations.test.ts src/features/access/accessRoute.test.ts`: 18 passed. Checks trusted commit/stale/uncertain evidence selection and route behavior. |
| Web build | `pnpm --dir web run build`: TypeScript and Vite passed. Existing large-chunk warning remains; no new bundle-size qualification is claimed. |
| Consistency | Changed Markdown local links and balanced fences checked; `git diff --check` passed. Canonical architecture/ports/backend/frontend/protocol/persistence/testing/operations and roadmap/status owners synchronized. Historical verification reports unchanged. |
| UI quality | Detector ran once on changed targets: no findings. Fresh Impeccable finish reviewer: **ship**, all 19 supplied captures valid, no material fixes. Independent documenter confirmed incumbent system preservation; no DESIGN.md/theme/token change required. Pre-existing stale Impeccable metadata/legacy briefs were left unchanged. |

Commands for integration rows use `dotnet test <affected .csproj> --filter '<filter>' --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false`; detailed logs are retained locally under `/tmp/eval-*.log`. These are focused local checks, not a full CI or milestone run.

### Browser behavior

Playwright MCP exercised a disposable SQLite Synthetic API at `127.0.0.1:5096` and Vite at `127.0.0.1:5186`:

1. Created a deterministic Script and reusable Suite. Appended Script v2 and inspected v1 to confirm its original content/hash stayed unchanged.
2. Created a Definition with required deterministic and Manual criteria. Missing evidence disabled publication. Ran the exact saved draft, observed a deterministic pass and pending human assessment, stored an attributable assessment, reopened it, then published successfully.
3. Forked publication criteria, attached a reusable publication Suite, ran it from the saved draft panel and inspected its exact evidence. Saved binding changes now refresh current criteria automatically.
4. Saved/reloaded governance defaults and inspected inherited Instance bindings. Created an Instance from the exact publication, granted separate fixture read authority, completed an actual Synthetic chat response, opened its existing Run inspector, followed the evidence link and deliberately evaluated the saved Run. The evaluation passed its specified metadata predicate; the Run retained Completed and did not acquire an unconfigured Verified label.
5. Submitted incomplete forms and observed field-specific validation. Escape → Keep editing retained the Manual reason. The Suite form's primary footer remained reachable at 320×568; Manual assessment remained reachable by scrolling. Confirmed publication readiness at 320/390/768/1024/1440 with no document horizontal overflow. Long evidence hashes wrap.

Captured screens include Definition, Suites, Scripts, Results, pending Manual evidence, Run verification, Governance and the short form. Local files: `.impeccable/review/unified-evaluations/`; review notes: `finish-review.md` and `design-system-check.md`. Screenshots were opened before review; one transient resize capture and transient toast capture were replaced after layout settled. These local ignored artifacts are not remote hosted evidence.

The current-page network readback returned successful evaluation/governance requests; development StrictMode superseded reads were aborted as expected. A temporary access-context 500 while this task's host was deliberately stopped/rebuilt was resolved after restart. No unresolved evaluation-flow console exception was observed.

## Bounds and measurements

Shared concurrency: 2; evaluation execution deadline: 10 seconds; all BeforeRun bindings share at most 10 seconds and one quarter of the admitted Run duration. Per Suite: at most 100 checks / 4 judges; per judge: 512 output tokens and 2,048 response characters; Script body: 16,000 characters; finding: 2,000 characters; catalogs/versions: 200/1,000; attempts: 1,000 per subject. These are enforced limits, not capacity promises.

The final local Runtime test measured eight accepted observation samples: BeforeRun n=4, mean 5.895 ms, nearest-rank p95 12.861 ms; AfterRun n=4, mean 14.585 ms, nearest-rank p95 36.397 ms. Four additional denied-observation cases completed the main Run without granting verification. Samples combine InMemory and SQLite and include local transaction/cold-test overhead. They are illustrative fixture measurements, not production percentiles or load/contention/cleanup SLAs. Asynchronous work did not await the interactive response path. Hosted evaluation spend was zero.

## Final review and remaining qualifications

Self-review corrected independent draft evidence Script-read fencing, current Script execution authority at settlement, original initiator read recheck, result paging, bounded draft evidence retention, source-history recovery, governance policy eligibility, no-replay request handling, advisory review semantics, shared precondition deadlines, responsive evidence layout and saved-draft Suite refresh. The installed-data review also rejected untrusted/out-of-range commit counters as current ordering witnesses. Final UI reviewer disposition is **ship** for the rendered incumbent extension; it does not certify backend behavior or omitted device states.

- **Native Sandbox execution is blocked by design in this delivery:** an EvaluationAttempt alone has no independent current AgentRun/profile/exact Approval authority or isolated input-dispatch contract. Bodies are saved, but evaluation returns Inconclusive. No native command smoke, arbitrary multi-tenant isolation or cleanup guarantee is claimed. Completing this boundary requires the incumbent independent execution admission and isolation qualification, not a host shell workaround.
- Real hosted judge behavior/quality, held-out comparative measurement, provider billing, production contention/load, physical devices, measured contrast and deployment/restore qualification were not run. Actual 200% native zoom was attempted through browser shortcuts; DPR/viewport/scale did not change, so it remains unverified. Viewport resizing is reported separately.
- A broader Definition regression attempt was stopped to keep checking focused. Its Sandbox SDK deletion cases timed out waiting for `deletionCompleted` at `TenantDefinitionIntegrationTests.SandboxSdk.cs:216`. The exact two cases were rerun and failed on both stores, then reproduced identically from an untouched `git archive` of parent `9bc0293` (identical application/test source to proposal baseline `8f3e379`; only TODO.md differs) in `/tmp/agent-core-evaluation-baseline`. This pre-existing failure remains open; it is not part of the green evaluation checks or evidence of new native Script qualification.
- Hosted CI and full CI suites were not requested or run. Goals/Delegation remain future consumers; no milestone was advanced by this report.

The task-owned Synthetic/Vite hosts were stopped after verification and the disposable copied baseline/database resources removed. Local screenshots, review notes and test logs remain available for inspection.
