# Memory close and forget verification

Local verification on 2026-10-09. This is a focused behavior correction, not acceptance of a new milestone or a replacement for historical freeze evidence.

## Corrected behavior

- Closing an exact-subject OpenLoop stores `Resolved`, removes it from active recall and pending work, and preserves content, provenance and workspace files. Resolution is atomic across the current Session and permitted promoted scopes, and repeated close requests do not update an already-resolved item. Independently owned copies in other Sessions retain their own lifecycle.
- Model-envelope deletion is rejected for both conversational sources. A model's `userExplicit` classification cannot authorize forgetting. The existing `memory.forget` path still requires approval for the exact owned item and action, including retained resolved items.
- Receipts distinguish missing memory, unavailable storage, rejected deletion, approval required and partial trusted deletion. Close success displays `Closed`. Subsequent eligible completed-turn context carries bounded Core outcomes so the assistant can acknowledge an attempted deletion accurately. Delivered text and speech projections are unchanged.
- Provider schemas offer upsert/resolve. Legacy delete payloads remain parseable for a visible rejection. Invalid numeric scope hints are rejected. Existing enum values and receipt fields are preserved; Resolved is additive and requires no SQLite schema migration.

## Executed journeys

Synthetic SessionRuntime tests created research, closed it, and repeated the close. Expected and observed: no active loop remained in the affected scopes, retained content/provenance matched the original, and the repeated close preserved its timestamp. Both userExplicit and agentInferred delete proposals failed without mutation. Missing loops and invalid kinds/scopes did not create or delete memory.

InMemory/SQLite contract tests resolved three scopes together, checked owner isolation, released active capacity, reopened a new loop and subsequently deleted retained resolved items. The API journey reopened SQLite and verified retained content, rejected missing/mismatched approval grants, accepted exact-approved forgetting in each scope, and preserved workspace report bytes.

Playwright MCP and the repeatable CLI E2E exercised the running Synthetic app on disposable data. Visible Chat controls submitted research creation, a legacy delete proposal, an explanation of the receipt, and `Close the Vietnam car prices research work; keep the report.` Expected and observed: approval-required deletion receipt, truthful next-turn explanation, `Closed` indicator, zero active IdentityUser loops, owned-ID status `Resolved`, unchanged research content and unchanged report bytes. Reload preserved the receipt; repeated close preserved the timestamp; closing an absent loop showed a precise missing-loop message. The CLI E2E also checked browser errors/failed requests; MCP reported no console errors.

## Checks

Backend commands used `--no-restore --nologo -m:1 -nr:false -p:UseSharedCompilation=false`. Full Application and Infrastructure runs additionally used `--blame-hang --blame-hang-timeout 5m`.

| Check | Result |
| --- | --- |
| Full `AgentCore.Domain.Tests` | 173 passed |
| Full `AgentCore.Application.Tests` | 1,375 passed; 2 opt-in checks skipped |
| Full `AgentCore.Infrastructure.Tests` final run | 905 passed; 9 skipped; 1 Docker cancellation-cleanup failure |
| Focused Application memory admission, receipts and prompt context | 58 passed |
| Focused Infrastructure memory contracts, semantic parsing and SQLite Admin history fixture | 45 passed |
| Affected API filter: `IdentityMaintenanceJourneyTests`, `AdminMemory`, `ContinuityEnhancementJourneyTests`, `ContinuityBoundaryTests` | 31 passed |
| `pnpm exec vitest run src/state/sessionStore.test.ts src/features/chat/Conversation.test.tsx` in web | 78 passed |
| `pnpm run build` in web | Passed; existing Rollup annotation/chunk-size warnings |
| `pnpm exec playwright test --config playwright.memory-semantics.local.config.ts` in web | 1 passed against disposable Synthetic hosts on ports 5198/5188 |
| Playwright MCP interactive memory journey | Passed |
| Isolated `DockerSandboxExecutorTests.Cancel_kills_and_reaps_the_container` rerun | 1 passed |
| Changed canonical documents: local Markdown targets/fences; `git diff --check` | Passed |

The first broad Infrastructure run exposed a SQLite fixture isolation failure: a pooled handle was disposed while another parallel fixture cleared global pools. The affected Admin history fixture now uses unpooled connections and does not clear global pools. Its focused rerun and final broad-suite execution passed. The final broad suite instead failed `DockerSandboxExecutorTests.Cancel_kills_and_reaps_the_container` because one container remained after cancellation. The unchanged Docker test passed in isolation (`dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~DockerSandboxExecutorTests.Cancel_kills_and_reaps_the_container --nologo -m:1 -nr:false`), but that does not erase the broad-suite failure. Docker cancellation cleanup remains outside this memory correction and is not claimed verified under broad-suite load.

Hosted-model natural-language classification, Real-provider calls and hosted CI were not exercised. The runtime evidence is deterministic Synthetic behavior; it does not prove a particular hosted model will always choose resolve for a natural-language close request. The whole API suite and unrelated browser scenarios were not rerun for this focused correction.
