# Tenant event Experience and source coverage verification — 2026-10-22

This backend module converts event Experience source review and durable coverage to the existing canonical tenant owners. It does not claim full proposal or milestone acceptance. Canonical model `automation.inspect`/authoring conversion, Harness, remaining legacy/internal/schema retirement and exhaustive proposal qualification remain open.

## Behavior and ownership

- Core event tasks use ordinary protected `experience.source` and optional `experience.record`, with actual Instance ownership, current raw Run/Session reads, live Experience settings and exact read/record consent. No creator/invoker Profile or private User ownership is adopted.
- Validated source witnesses retained in a successful Runtime checkpoint acknowledge only represented `run.completed`/`run.failed` sources in the actual accepted root occurrence's exact coalescing bucket/child. Approval successors resolve the same event root. Inspection need not record anything, and uninspected members remain pending.
- Acknowledgement shares the existing Run transaction/locks and bucket `CoverageJson`/inspection set. It is derived from retained evidence rather than attempted tools, task completion or model-supplied bucket IDs. Unrelated sources, other buckets/children, forged webhook Run IDs, denied inspection and output-budget responses without witnesses cannot establish coverage. Retained/compacted witnesses are idempotent. No migration, table, scheduler, semantic outbox, permission snapshot or mutable conversation owner was added.
- Original-human `GET .../automations/{automationId}/unreviewed-sources` returns current-readable bounded metadata and continuation. It independently requires Agent/Automation reads, filters Core/Webhook and monitored-account source access, and repeats original-login/security fences after I/O. Monitored account metadata must still exist under the actual tenant owner. It does not grant source-content inspection or acknowledge anything.
- Coverage scans at most 32 owned ordered bucket rows, returns 1–24 sources (default 8), and preserves continuation across invisible/covered members. Empty items with continuation are not exhaustion. Immutable event/delivery history is retained.

## Executed integrated workflow

Twelve new API cases exercise both InMemory and temporary SQLite across recording, source-only inspection, exact read Review approve/reject, raw Run-read denial and event-read revocation. Each creates an owned Agent, independent executor grants and a real active `run.completed` subscription, commits two actual substantive UserTurn Runs, matches/coalesces their immutable facts and admits the actual Core-event Session/Run. The real Synthetic model/Runtime then attempts one exact source, optionally records it and finishes through `work.complete`.

Expected and observed: two pending sources initially; one remains only after an authorized source witness is durably checkpointed. Recording creates one Instance Experience fact; source-only review records none and finishes NoAction. Pending or rejected read Review, raw-read denial and revoked event access leave both sources uninspected. Approved read Review resumes under the original Core root and covers only the inspected member. SQLite host recreation retains the pending member and saved fact without duplicating work. The source-only model pauses at its actual next-generation barrier, allowing inspection of the retained checkpoint before release.

The same fixture checks actual HTTP unauthenticated/foreign tenant/Instance refusal, invalid bounds/cursor, one-item continuation without duplicate/omitted members and store-level per-source filtering. A budget-refusal checkpoint with no source witness yields no acknowledgement through the deterministic rule; this is not a new full worker budget-exhaustion test. An invalid bucket witness refuses. The held source-only path additionally submits a stale Run checkpoint containing the remaining source witness and verifies that coverage stays unchanged before the real worker completes.

Existing regressions exercise source privacy/transitive User ownership, manual/scheduled Instance Experience, maintenance consent/factual successors, source-only approval outcomes, current event admission/source Review, coalescing, report/existing-conversation boundaries, captured receipt/bucket recovery and durable bucket/legacy coverage invariants. No frontend behavior was changed; these backend use cases ran through WebApplicationFactory, actual protected tools/Runtime and temporary SQLite rather than a browser. No provider credentials, outbound hosted model calls or user-data reset was used.

## Focused results

| Check | Result |
| --- | --- |
| Final product solution build, serial/no-restore | Passed, 0 warnings/errors, 26.14 s |
| API event/Experience/admission/recovery group | 88 passed, 3 m 10 s; includes all 12 new workflows |
| Application source/admission contracts | 26 passed, 243 ms |
| Infrastructure buckets/coverage/migration group | 20 passed initially; the stale migration assertion was repaired and its one roundtrip case passed separately in 6 s |
| Final migration test-project build | Passed, 0 warnings/errors, 13.86 s |
| Held-worker stale checkpoint assertions | 2 passed (the source-only cases above rechecked with added assertions), 13 s; API test-project build passed, 0 warnings/errors, 9.21 s |

Total: **135 distinct focused passing cases** across API, Application and Infrastructure. Earlier diagnostic repeats are not added to that count. No full local CI suite or GitHub Actions was requested or run. Frontend build/browser checks were not needed for this backend-only module.

Commands (repository root):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_event_Experience_retains_exact_source_coverage_only_after_authorized_durable_inspection|FullyQualifiedName~Tenant_experience_independent_Automation|FullyQualifiedName~Tenant_experience_human_Run_with_only_Instance_evidence|FullyQualifiedName~Tenant_experience_Automation_maintenance|FullyQualifiedName~Tenant_experience_source_review_can_finish_NoAction|FullyQualifiedName~Tenant_event_receipt_snapshots_match_and_admit_only_the_exact_current_authorized_owner|FullyQualifiedName~Tenant_event_recovers_captured_receipt_bucket_and_exact_consent_without_duplicate_work' --logger 'trx;LogFileName=event-experience-qualified.trx'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantEventAdmissionContractTests|FullyQualifiedName~TenantExperienceContractTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~CoreEventBucketTests|FullyQualifiedName~TenantEventMigrationTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantEventMigrationTests'
```

Logs: `/private/tmp/tenant-event-experience-final-build.log`, `-qualified-api.log`, `-qualified-application.log`, `-qualified-infrastructure.log`, `-migration-final-build.log` and `-migration-final.log`. The API TRX is `tests/AgentCore.Api.Tests/TestResults/event-experience-qualified.trx`. Additional held-worker checks used `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_event_Experience_retains_exact_source_coverage_only_after_authorized_durable_inspection&DisplayName~source-only'`, with output `/private/tmp/tenant-event-experience-checkpoint-final.log`. Raw requests, cookies, temporary databases and model traces are not committed.

## Corrections and limits

The first negative/Review fixture incorrectly used `memory.read` for a Run source. Correcting it to independent `agent-runs.read` qualified the actual source authority. Two early InMemory approval continuations entered WaitingToRetry; their exact transient cause was not isolated. They then passed in a narrow diagnostic run and four repeats (ten case executions). The new fixture now stops its own hosted Run/scheduler dispatchers before fake-time jumps and drives the actual processors explicitly, avoiding a competing dispatch against an unconfigured scripted model. The final 88-case group passes with that fixture. This is not a claim of a production retry bug fix; broader retry/concurrency qualification remains part of the final audit.

The migration regression's old latest-schema guard count (83) was stale; current schema has 104. Inspection of the frozen event upgrade proves 68 declared unique guards. The repaired test asserts 68 at that exact migration, preserves every prior guard, checks all converted guards remain in the latest schema, rejects concealed tenant event ownership, preserves the retained legacy fact, and verifies exact downgrade/reupgrade guard sets, current model parity and foreign-key integrity on its own disposable database. An intermediate attempted historical assertion of 83 also failed and was corrected from the frozen migration source. No historical migration was edited or user database changed.

Current monitored-account checks are retained in coverage reads, but no dedicated new Email-coverage browser journey was run. Model Automation discovery/authoring and full worker output-budget exhaustion remain separate work; source metadata never supplies those capabilities or bypasses raw source reads. No whole-surface UX or full proposal acceptance is claimed.

Documentation checks found no new broken links or unbalanced fences. The historical boundary inventory already contains 225 links to removed baseline files; a HEAD comparison confirms all 225 predate this change. They remain an explicit final documentation/cutover-audit gap rather than being presented as a passing complete inventory link audit.

Canonical owners: [ports](../04-backend-interfaces.md#tenant-event-experience-coverage), [backend](../12-backend-implementation-spec.md#canonical-tenant-event-experience-coverage), [HTTP](../14-api-and-realtime-protocol.md#tenant-event-unreviewed-source-http), [persistence](../15-persistence-and-configuration.md#tenant-event-experience-checkpoint-coverage), [testing](../16-testing-strategy.md#canonical-event-experience-coverage-verification).
