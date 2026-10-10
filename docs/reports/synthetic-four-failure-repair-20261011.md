# Synthetic four-failure repair — October 11, 2026

Scope: repair the four findings from workflow [38066351750](https://github.com/trannamtrung1st/agent-core/actions/runs/38066351750), starting at `51963073` and retaining the browser-close recovery and grouped Definition permission UI. The provider and initial E2E repairs landed in `76fd69b6`; this follow-up completes bounded SQLite write handling and stronger acceptance fixtures.

## Root causes and behavior

1. **Provider transport was a production safety defect.** Chat Completions and Responses converted non-string `arguments` into raw JSON. An object could therefore become a valid executable call. Both transports now retain genuine string payloads and absent arguments, but produce a sticky, content-free, invalid-JSON payload for explicit non-string types. Later fragments cannot repair a malformed type. Tool-call identity, continuation and replay guards are retained. The 36-case matrix covers both transports, parameterized and zero-parameter tools, objects/arrays/null/Boolean/numbers and mixed Chat fragments; Core rejects every malformed close. Existing missing/blank zero-argument behavior remains covered.

2. **SQLite evaluation writes had no serialized write admission and incomplete conflict mapping.** Each operation already had its own DbContext and connection-string-created connection. The hosted error occurred in `BeginTransactionAsync`, before the SaveChanges concurrency catch. The installed Microsoft.Data.Sqlite transaction implementation starts with `BEGIN IMMEDIATE`; SQLite permits one writer. The singleton evaluation store now admits one revision/scenario write before creating its context, bounds admission and database waits using the configured busy timeout, honors cancellation, and retains the complete expected-revision check plus revision/scenario mutation in one transaction. Busy/locked failures across transaction start/save/commit return Conflict; there are no transaction retries and unexpected SQLite errors still escape visibly. See the [provider transaction documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

   A broad native run additionally reproduced `database is locked` during ordinary draft edit racing publication. `UpdateDraftAsync` previously caught only optimistic concurrency failures. Its known busy/locked SaveChanges failures now return Conflict without changing candidate or revision. The new external-writer integration case checks both evaluation and ordinary draft editing.

   **Diagnostic limit:** the original SQLite Error 1, “cannot start a transaction within a transaction,” did not reproduce in one initial Linux x64 run plus 30 repetitions. Separate connection ownership was confirmed; a shared connection was not established. Bounded writer admission removes competing evaluation transaction startup, but this evidence does not establish the precise internal/native mechanism behind the original Error 1. It is deliberately not caught as a normal contention response or hidden by retries.

3. **The invalid-tool E2E assumed removed global exhaustion.** Equivalent invalid strategies are blocked locally; valid corrected and unrelated calls remain admitted. The corrected-call journey checks completed Run receipts and provider-confirmed closure, independently of logout. Terminal coverage still forces a genuine `finalizationToolCall`, checks budget termination, copies its diagnostic, and verifies persistence after reload. The earlier follow-up hosted workflow [38068535768](https://github.com/trannamtrung1st/agent-core/actions/runs/38068535768) passed six jobs, but this terminal case remained active beyond its 15-second assertion while consuming the default 48-step budget. Its isolated Instance now uses the supported eight-step custom cap with the normal 300-second interactive-browser duration. The test requires exactly eight consumed steps, `stepLimit`, and the same terminal diagnostic. No assertion timeout or production limit was increased. An initial 60-second fixture duration entered finalization immediately because existing tool/finalization reserves consumed the available work window; the fixture was corrected rather than changing runtime admission.

4. **The Secretary selector targeted the removed source-kind multiselect.** The journey uses the grouped Schedule checkbox and leaves Events unchecked. Both Save responses and the published immutable version must grant exactly `schedule` with Automation enabled. All four steps remain required: author/publish/create Morgan; completed-work Experience deduplication/recall/suppression; shared Chat/Admin Automations with provenance/outcomes/mobile controls; configured browser lookup. UI/UX and independent Built-in/Webhook grants are unchanged.

## Changed files since the reviewed commit

- `src/AgentCore.Infrastructure/Providers/OpenAICompatible/OpenAICompatibleLanguageModel.cs` and `.Responses.cs`: shared fail-closed argument-type handling.
- `src/AgentCore.Infrastructure/Providers/Synthetic/ScriptedLanguageModel.cs`: scoped recovery fixture.
- `src/AgentCore.Infrastructure/Persistence/SqliteDefinitionDraftEvaluationStore.cs`: bounded admission, database wait limits, known contention mapping and unchanged atomic transactions.
- `src/AgentCore.Infrastructure/Persistence/SqliteAgentDefinitionAdminStore.cs`: known edit contention returns Conflict.
- `src/AgentCore.Infrastructure/InfrastructureServiceCollectionExtensions.cs`: evaluation admission uses configured `BusyTimeoutMs`.
- `tests/AgentCore.Infrastructure.Tests/ProviderArgumentTypeTests.cs`, `ContinuationCapabilityProviderTests.cs`, `OpenAICompatibleLanguageModelTests.cs`, `ResponsesTransportTests.cs`: transport type, zero-argument and continuation/replay coverage.
- `tests/AgentCore.Infrastructure.Tests/DefinitionDraftEvaluationStoreTests.cs`: five 20-worker races with exact one-winner/19-conflict counts, atomic fresh removal, controlled admission/cancellation, external SQLite contention and fresh recovery. Its test factory now implements the actual Task-returning EF factory method rather than shadowing it with ValueTask.
- `web/e2e/diagnostic-details.spec.ts` and `support/instance-identity.ts`: isolated configured budget, scoped recovery and persisted real terminal diagnostics.
- `web/e2e/secretary-demo.spec.ts`: checkbox interaction plus saved/published policy assertions.
- `docs/04-backend-interfaces.md`, `docs/12-backend-implementation-spec.md`, `docs/15-persistence-and-configuration.md`, `docs/16-testing-strategy.md`, the prior transport report and this report: canonical contracts and verification evidence. No design-system presentation contract changed.

## Verification

| Check | Result |
| --- | --- |
| Original malformed provider scenario before repair | Reproduced executable object coercion and failing test |
| Initial provider/scoped recovery checks | 142 provider tests and 44 Application recovery tests passed; opt-in external tests skipped |
| Repeated native provider + evaluation suite | 152 passed, 5 opt-in skipped |
| Evaluation plus edit/publication race after contention mapping | 12 passed |
| Linux x64 combined provider/concurrency suite | Five runs, each 154 passed and 5 opt-in skipped; 25 independent 20-worker revision races |
| Final Linux x64 external-writer/edit regression | 12 passed |
| Full rebuilt Application suite | 1,643 passed, 7 opt-in skipped |
| Admin API integration subset | 81 passed |
| Full rebuilt API suite | 431 passed, 2 opt-in skipped |
| Full rebuilt Infrastructure suite | 1,160 passed, 9 opt-in skipped; complete native browser, persistence, provider and unchanged filter-memory coverage |
| Complete diagnostic + four-step Secretary files | Final 14 passed, including exact-budget finalization and saved/published Schedule-only policy; complete files also passed earlier repetitions |
| Final exact-budget and corrected-close cases | Three repetitions each, 6 passed |
| TypeScript and whitespace checks | Passed |

The first broad Infrastructure run exposed the now-corrected draft edit/publish contention and an unchanged restricted-filter stress case returning `filter-timeout` instead of its expected evaluation error while concurrent Linux emulation/browser workloads were active. Neither its safety deadline nor assertion was altered; the complete final suite passed with all final binaries, including that unchanged filter-memory case. A temporary publication-read assertion used an invalid source-kind name; the test now uses the existing `ForkDurable` API contract. All failures remain recorded, rather than skipped or relaxed.

Playwright MCP independently navigated the disposable Synthetic Admin inventory and opened the Secretary's actual durable v1 publication created by the full journey. Six initial owner-bootstrap reads returned 401 before authenticated inventory/version reads recovered; no additional console errors appeared during that inspection. The MCP page was returned to `about:blank`. CLI journeys separately enforce their existing page-error, failed-request and server-error checks. Existing Real hosts and AHI UAT were not touched; all provider tests stayed offline/opt-in.

Commands use `dotnet test tests/AgentCore.Infrastructure.Tests`, `tests/AgentCore.Application.Tests` and `tests/AgentCore.Api.Tests`, with focused filters before complete suites. Linux repetitions use the official .NET 10 SDK x64 container and a disposable tracked-source snapshot with read-only package cache. Browser files run using Node 22, fresh disposable SQLite databases and isolated loopback ports; recovery repetitions use `--repeat-each=3` with no retries. Hosted workflow acceptance must be checked on the exact final implementation commit after push. Local verification and earlier workflows do not establish that final CI is green.
