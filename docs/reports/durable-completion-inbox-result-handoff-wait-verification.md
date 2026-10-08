# Durable completion inbox, result handoff and wait verification

Closed on final behavior `9edccab46782d616b8b8f7464be9495933e8bdfd` on 2026-10-08. All five required [hosted Synthetic/Compose jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37799412089) passed on that exact SHA. Successor authorized on 2026-10-08, starting at `513cbed0`. The preceding Automation destinations/report-back enhancement is complete on behavior `53c0c2fe9f87f28e866e80a2ff48127b5dbda3e8`; [all five hosted jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37781579245) are historical baseline evidence. Earlier milestone reports and freeze SHAs remain immutable. P10/P11 remain unopened.

## Implemented behavior

Existing SessionRuntime, coordinator and AgentRunStore owners now provide canonical completion accounting, active result consumption, deterministic report batches and typed same-Run suspension. The existing receipt table is evolved by one forward migration. Inbox rows retain source references, claim/acknowledgment accounting and delivery links; child Runs own result content. A successful parent answer commits its assistant entry, Run and Handled links atomically. Failed/cancelled/expired consumption releases results. A parent that leaves results unhandled receives requested reports after its Run and pending input are available. General Assistant explicitly authorizes the five new tools; acknowledgment projection requires a current matching claim.

WaitingForSignal stores a typed deadline/condition, suspended generation, pending checkpoint and cumulative budgets. Wake retains Run/Activation/Response/attempt, creates a new generation and appends one normal result. Timeout never consumes a completion. Reads leave claim state unchanged; expiry repair runs through lifecycle delivery coordination. Foreign child/parent ownership is rejected. Existing-session Automations retain direct execution without redundant inbox obligations.

## Review corrections

Regression review corrected JSON owner/wait persistence, atomic answer ordering around existing save hooks, filtered-index schema validation, synthetic result-fixture ordering/casing, exact lookup beyond bounded pages, cursor paging and acknowledgment projection. Final review added pure inspect/list behavior, bounded claim repair, current-wait timeout telemetry, richer safe metadata and suppressed misleading unread attention on handled results. No provider, second runner, queue or UI toolkit was introduced.

## Executed local evidence

| Check | Command / evidence | Observed result |
| --- | --- | --- |
| Store integration | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~AgentRunAdmissionStoreTests --no-restore --nologo -p:UseSharedCompilation=false -m:1`; `/tmp/inbox-atomic-rollback-store.log` | 98 passed: publication, provisional ACK, atomic successful answer, fail/cancel release, expiry, take/report race, busy-parent fairness, two-source reservation, invalid wait ownership, duplicate wake, reopen, timeout/cancel, 102-item cursor paging populated previous-schema migration, duplicate take and stale-answer atomic rollback. |
| Full backend review | `dotnet test AgentCore.sln --no-restore --nologo -p:UseSharedCompilation=false -m:1`; `/tmp/inbox-final-backend.log` | 2,754 passed, 20 optional skips. Final full run after pure-read correction: 2,764 passed, 14 optional skips (`/tmp/inbox-final-backend2.log`; six local nopCommerce read-only probes were available). Later added tests passed separately: Domain 173 total including five wait-budget/validation cases (`/tmp/inbox-wait-budget-tests.log`), four runtime wait cases including reattach/Steer (`/tmp/inbox-wait-runtime-final.log`), and two stale-answer rollback parity cases in the 98 store tests. |
| Frontend unit suite | `pnpm --dir web run test --run --maxWorkers=1`; `/tmp/inbox-final-frontend.log` | 98 files, 749 tests passed. |
| Frontend build | `pnpm --dir web run build`; `/tmp/inbox-final-web-build2.log` | TypeScript/Vite passed; existing bundle-size warning. |
| API build | `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo -p:UseSharedCompilation=false -m:1`; `/tmp/inbox-final-api-build2.log` | Passed, zero warnings/errors. |
| Focused Synthetic browser | `pnpm --dir web exec playwright test --config /tmp/inbox-playwright.config.mts durable-completion-inbox.spec.ts`; `/tmp/inbox-browser-review.log` | 3 passed before the added timeout journey; full core run includes all four cases, which passed. The corrected Automation label assertion passed in isolation (`/tmp/inbox-admin-rerun2.log`, 1 test). |
| Compose / volume | `COMPOSE_PROJECT_NAME=agentcore-inbox-review AGENTCORE_COMPOSE_PORT=5298 ./scripts/compose-sqlite-volume.sh`; `/tmp/inbox-final-compose.log` | Passed: owner-capability path, SQLite volume survives container recreation; task containers/network cleaned up. |

The temporary browser configs import the repository config and use the already-built API to avoid local shared-compiler contention. Core and acceptance use isolated ports/databases. They preserve repository test selection and sequential acceptance server lifecycles. Optional hosted model/speech/nopCommerce probes remain skipped; no provider credits were used.

## Actual user journeys and responsive review

Playwright CLI created an active General Assistant parent, launched a child, observed WaitingForSignal with attempt 1 and no early report, then completed inspect/take/ACK on the same parent Run. The final answer used the child result, persisted Handled and produced zero BackgroundCompleted Activations. View handling run opened that exact completed parent. Reload preserved accounting and response identity. The unhandled variant produced one ordinary report after parent terminal; cancellation prevented wake and late answer. The timeout variant is part of the full core gate.

Playwright MCP exercised the actual Synthetic app on :5185/:5278. It submitted `[test:completion-handoff]`, observed the answer using the child evidence, opened Background Work and the exact handling Run, and reopened the conversation after the API restarted. It then submitted `[test:execution-wait]`, navigated to the actual Admin Runs collection, opened the waiting Run and observed condition/deadline/Cancel at all three widths. The previous duration completed normally on the same Run and attempt 1. The final handled view contained no unread attention label and emitted zero page errors; current-navigation console error inspection returned zero errors. Temporary server shutdown/reconnect errors during the earlier runner handoff were setup events, not counted as product failures.

The bounded presentation review reused Ant Design and the existing shared drawers. Both Background Work and handling details measure 640px at 1440/768 widths and 390px at x=0 on mobile. Initial screenshots taken before responsive drawer animation settled were replaced after a 500ms settle. Long results/actions wrap; waiting remains distinct from retry/approval. Product/design context and Chat/Admin surface briefs are synchronized.

Evidence: [handled background mobile](assets/durable-completion-inbox/handled-background-390.png), [handling Run desktop](assets/durable-completion-inbox/handled-run-1440.png), [Admin wait mobile](assets/durable-completion-inbox/admin-wait-390.png). The asset directory contains desktop 1440×900, tablet 768×900 and mobile 390×844 views for all three states.

## Acceptance status

Closed on `9edccab46782d616b8b8f7464be9495933e8bdfd` with [workflow 37799412089](https://github.com/trannamtrung1st/agent-core/actions/runs/37799412089): backend, frontend, Playwright core, Playwright acceptance and Compose smoke all completed successfully. Hosted backend: Domain 173, Infrastructure 874/15 optional skips, Application 1,333/2 skips, API 383/3 skips — 2,763 passed, 20 optional skips. Frontend: 749 passed. Core: 124 passed. Acceptance: 16 passed across seven projects. Compose: passed. The local order-event plugin suite also passed, four tests.

Local full core attempts remain recorded accurately: the first had 123 passed and one obsolete completion-label assertion, corrected and passed in isolation (`/tmp/inbox-admin-rerun2.log`). The clean repeat had 123 passed and one Event Source option-selection timeout (`/tmp/inbox-final-core-clean.log`); that unchanged scenario passed on isolated recheck (`/tmp/inbox-event-rerun.log`, 1 test) and in the exact-SHA hosted full run. It was not a completion/wait failure and no unrelated product change was made. All four new handoff/wait journeys passed in both local full attempts and hosted CI.

Canonical docs, product/design context, surface briefs, definition and report are synchronized. Checks cover 497 relative links/anchors before closure edits, balanced fences, complete JSON examples, definition/design JSON parsing and whitespace. Screenshot dimensions were checked for all nine assets. Task-owned native hosts were stopped; Compose task containers/network were removed, with the test volume retained as persistence evidence. Historical freezes remain immutable; P10/P11 stay unopened. Later documentation-only closure publication does not change the tested behavior SHA.


## Acceptance coverage map

| Proposal cases | Evidence |
| --- | --- |
| CI-01–04, CI-07 | Active handled/unhandled Synthetic journeys; pure reads and unacknowledged-terminal store parity. |
| CI-05–06 | Failed/cancelled/expired claims, atomic stale-answer rollback, duplicate take and take/report ordering tests. |
| CI-08–12 | Two-source batch/fairness tests plus existing quiet/attention/failure/follow-up, detached reporting and unavailable/policy owner regressions. |
| CI-13–18 | Same-Run wait reopen/wake/timeout/cancel parity, duration mailbox resume/reattach/Steer and invalid self/foreign child tests. |
| CI-19–21 | Explicit capability registrations, exact owner/profile/origin guards, existing pending input/admission coordination, 102-item cursor pages and wait count/time limits. |
| CI-22–24 | Existing Automation destination, Initiative-off requested reporting, explicit background reporting and direct conversation-target Synthetic scenarios. |
| CI-25 | Shared UI tests, actual MCP handoff/wait/handling navigation, keyboard dismissal and three viewport review. |
| CI-26 | Full backend/frontend, core browser/voice, seven acceptance projects, Compose volume and exact-SHA hosted workflow gates. |

## Post-closure claim renewal and unavailable wait targets (2026-10-08)

**Closed on verified behavior `5da84da16eea68274894d48202e422f14e2eb343` (`5da84da1`).** [Hosted Synthetic workflow `37805812128`](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128) passed all five jobs in its first attempt. Original feature closure remains historical evidence; this follow-up does not reopen the migration or P10/P11.

The bounded review reproduced eight failures across SQLite and InMemory before correction: four long-running consumption cases and four deleted-child wait cases. A parent renewed its execution lease, but its inbox claim expired at the original deadline and became Pending. A tombstoned child remained a wait target, producing no early wake and a timeout after the deadline. Code inspection also found strict recovery target resolution would throw if target metadata disappeared; the added regression exercises that boundary.

Valid parent transitions now extend an unexpired consumption claim to the current execution lease in the existing outcome/Run transaction, retaining token and provisional acknowledgment. Expired claims are released rather than revived; generation and revision checks remain in place. Wait admission still rejects unresolved or foreign targets. Recovery of an already admitted wait instead appends a normal unavailable result for missing or durably deleted targets, retaining Run, Response and attempt identity and excluding unavailable IDs from pending IDs. No schema migration, data reset or new execution owner is introduced.

`Renewed_parent_consumption_survives_original_expiry_and_settles_once` verifies acknowledgment both before and after renewal, passage beyond the original expiry, stale generation rejection, reopen, atomic successful answer and no remaining report candidate. `Suspended_wait_resolves_unavailable_child_before_or_after_deadline` verifies deletion before/after deadline, entirely missing target metadata, normal result contents, preserved identities and one wake. Both run against SQLite and InMemory.

Local gates:

- `dotnet test AgentCore.sln --no-restore --nologo -p:UseSharedCompilation=false -m:1`: 2,784 passed, 14 optional skips, including four nopCommerce plugin tests (`/tmp/inbox-hardening-backend.log`).
- Persistence admission parity: 106 passed after the correction and again with strengthened final assertions (`/tmp/inbox-hardening-store.log`, `/tmp/inbox-hardening-store-final.log`).
- Frontend: 749 tests across 98 files and production build passed on an archived exact-commit snapshot with Node 22.18.0, using `pnpm run test --run --maxWorkers=2` and `pnpm run build` (`/tmp/inbox-hardening-frontend-candidate.log`, `/tmp/inbox-hardening-web-build-candidate.log`). The shared checkout acquired unrelated concurrent changes, so the final frontend verification used `/tmp/inbox-hardening-candidate-5da84da1/web` with existing dependencies. No UI implementation changed.
- Earlier local frontend attempts remain recorded: the parallel run was interrupted after rendering timeouts; the one-worker run had 679 passed and 70 failed because pnpm selected Node 26.7.0 and its native Web Storage made jsdom localStorage unavailable. All 70 affected tests passed with that native API disabled (`/tmp/inbox-hardening-frontend-storage.log`), then the full isolated Node 22 run passed without changing assertions or test timeout.

Hosted acceptance on exact behavior SHA `5da84da1`:

| Gate | Result |
| --- | --- |
| [Backend](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128/job/113409859078) | 2,774 passed, 20 optional skips: Domain 173, Infrastructure 885/15 skips, Application 1,333/2 skips, API 383/3 skips. |
| [Frontend](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128/job/113409858235) | 749 tests across 98 files and production build passed. |
| [Playwright core](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128/job/113409858886) | All 124 scenarios passed. |
| [Playwright acceptance](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128/job/113409859350) | All 16 journeys across seven acceptance projects passed. |
| [Compose](https://github.com/trannamtrung1st/agent-core/actions/runs/37805812128/job/113409858654) | Owner-capability and SQLite volume/recreation smoke passed. |

Final metadata confirms completed/success and the full behavior SHA; logs are retained in `/tmp/inbox-hardening-hosted.log`. This run also covers startup repair `727865d1` and its three canonical migration regressions (upgrade, data preservation/reopen and untracked-schema rejection). The cancelled runs for `727865d1` and `c2639db0` are not acceptance evidence.
