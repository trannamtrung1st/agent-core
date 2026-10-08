# Durable completion inbox, result handoff and wait verification

Successor authorized on 2026-10-08, starting at `513cbed0`. The preceding Automation destinations/report-back enhancement is complete on behavior `53c0c2fe9f87f28e866e80a2ff48127b5dbda3e8`; [all five hosted jobs](https://github.com/trannamtrung1st/agent-core/actions/runs/37781579245) are historical baseline evidence. Earlier milestone reports and freeze SHAs remain immutable. P10/P11 remain unopened.

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

Implementation is reviewable; closure remains pending all five hosted Synthetic/Compose jobs on the exact behavior SHA. The initial full core run finished with 123 passed and 1 failed (`/tmp/inbox-final-core.log`, 124 selected cases). It exposed one stale presentation assertion (Completion report versus the new shared Completion label). Its affected Automation journey passed after correction; the original failed run remains recorded as failed. All seven local acceptance projects passed, 16 tests total (`/tmp/inbox-final-acceptance.log` and per-project logs). Changed-document checks passed: 497 relative links/anchors, balanced fences, complete JSON examples and git diff whitespace. This report will be updated with the verified candidate SHA and exact hosted workflow link before closure.


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
