# Provider argument safety and scoped-recovery CI repair

Date: 2026-10-10. Baseline `51963073`; final candidate is the commit containing this report. Exact-commit hosted status is reported separately after push. Browser-close recovery from `24aa7bb7` is preserved.

## Observed root causes and repairs

The original Infrastructure test reproduced: it expected empty arguments but received `{"ids":["order.lookup"]}`. Both Chat Completions fragment mapping and Responses completed-call mapping used `GetRawText()` for non-string provider argument values. An object therefore became valid executable argument JSON. Empty arguments would also be unsafe for a zero-parameter tool because Core intentionally normalizes genuinely absent/blank payloads to `{}` only for offered zero-property schemas.

Both transports now share one argument-fragment reader with a sticky transport-type error flag. Strings retain their exact bytes; missing fields remain absent. Present non-string types become a content-free, nonblank invalid JSON fragment, rejected by Core rather than interpreted as an executable object or missing payload. Later valid Chat fragments cannot repair the poisoned capture. No provider content is copied into the malformed marker. IDs, registered tool-name mapping, continuation, bounded recovery, approvals, effect receipts and generation retry policy are unchanged.

The original Playwright test also reproduced: expected `invalidToolStrategy`, observed `finalizationToolCall`. Strategy exhaustion now correctly blocks equivalent malformed calls without globally failing the Run. The old Synthetic script intentionally keeps requesting tools until the ordinary step limit starts tool-free finalization, then requests another prohibited tool. The repaired terminal test asserts that actual failure, normal step bounds, copied diagnostic details and the same diagnostic ID after reload.

A separate Synthetic recovery script and browser test perform two malformed close calls, one blocked equivalent call, an unrelated observation and a corrected `{}` close, then finish. Assertions require exactly five consumed steps, one blocked outcome, a completed Run, provider-confirmed closure and persisted receipts after reload. This does not claim application logout. Existing Application tests additionally exercise real open-context closure, repeated invalid calls, pending corrected close after checkpoint restoration, unrelated tools, cleanup-phase deadlines, already-closed contexts and durable effect identity.

The failed baseline workflow `38066351750` also exposed a Secretary acceptance selector for the removed Allowed source kinds multiselect; that flow now uses Schedule and verifies Events stays unchecked. The complete four-test Secretary journey passes. Another backend failure, `DefinitionDraftEvaluationStoreTests.Sqlite_concurrent_upserts_do_not_corrupt_revision_or_scenarios`, reported SQLite Error 1 (cannot start a transaction within a transaction) during BeginTransaction. Inspection confirms separate per-operation contexts with a connection-string factory, not a shared connection. This failure did not reproduce in six isolated attempts or the full local suite. No unsupported production or fixture change was made; its exact hosted cause remains unresolved and must be assessed from final hosted evidence.

## Changed files

- `src/AgentCore.Infrastructure/Providers/OpenAICompatible/OpenAICompatibleLanguageModel.cs` and `.Responses.cs`: shared fail-closed transport type mapping.
- `src/AgentCore.Infrastructure/Providers/Synthetic/ScriptedLanguageModel.cs`: bounded recovery fixture.
- `tests/AgentCore.Infrastructure.Tests/ProviderArgumentTypeTests.cs`: both transports, zero-parameter/parameterized tools, six malformed values and mixed Chat fragments; Core close validation rejects every malformed payload.
- `ContinuationCapabilityProviderTests.cs`, `OpenAICompatibleLanguageModelTests.cs`, `ResponsesTransportTests.cs`: behavioral malformed-type assertions, preserved absent/blank/string coverage.
- `web/e2e/diagnostic-details.spec.ts`: scoped recovery and separate terminal diagnostics.
- `web/e2e/secretary-demo.spec.ts`: use the grouped Schedule checkbox and assert Events remains unselected, replacing a stale multiselect selector.
- `docs/04-backend-interfaces.md`, `docs/12-backend-implementation-spec.md`, `docs/16-testing-strategy.md`, this report: contract and verification synchronization. No UI presentation/design-system contract changed.

## Verification

| Check | Observed result |
| --- | --- |
| Original Infrastructure CI scenario before fix | Reproduced assertion failure and executable object coercion |
| Original browser CI scenario before test repair | Reproduced expected `invalidToolStrategy` / actual `finalizationToolCall` |
| Final affected provider tests | 142 passed, 5 opt-in skipped |
| Close/recovery/checkpoint/argument/logout Application tests | 44 passed, 2 opt-in paid skipped |
| Full Application suite | 1,643 passed, 7 opt-in skipped |
| Broader Infrastructure suite before final sticky-type refinement | 1,142 passed, 9 opt-in skipped; final affected provider tests rerun afterward |
| Final malformed-type matrix | 36 cases passed, included in the 142-test provider run |
| Full diagnostic browser file | 10 passed |
| Secretary acceptance journey | 4 passed |
| Additional hosted SQLite concurrency finding | Passed six isolated local attempts and the broader suite; hosted-only failure not reproduced |
| TypeScript and production build | Passed; existing large-chunk advisory |
| Changed Markdown links/fences and diff whitespace | Passed |

Backend commands use `dotnet test ... --no-restore --nologo`, with `--no-build` for the complete suites after compilation. Provider filters cover `ProviderArgumentTypeTests`, `ContinuationCapabilityProviderTests`, `ResponsesTransportTests`, and `OpenAICompatibleLanguageModelTests`. Application filters cover `BrowserCloseRecoveryRuntimeTests`, `InvalidToolCallRecoveryTests`, `ResponsesCheckpointRecoveryTests`, `ToolArgumentPayloadTests`, and `BrowserLogoutRecoveryTests`. Browser CLI uses Node 22 with `e2e/diagnostic-details.spec.ts --project synthetic` on disposable ports 5285/5185/5291, InMemory persistence, and Synthetic providers.

Playwright MCP independently selected a disposable General Assistant v21 Instance, sent the recovery marker through visible Chat controls, inspected native Run diagnostics, and reloaded. Observed receipts were `invalid, invalid, invalid_tool_strategy_blocked, provider_unavailable, already_closed`; the unrelated observation reached provider admission rather than strategy refusal. The completed Run consumed 5 of 48 steps and recorded `closureConfirmed=true`, `logoutVerified=false`. The closure confirmed an already-closed context; actual open-context closure is covered separately by Application and existing browser journeys. Reload retained the same receipts and closure badge. Console errors: zero. An initial pre-owner bootstrap models request returned 401 and subsequent authenticated reads returned 200; no failed flow request was observed. Disposable hosts were stopped; existing Real hosts and AHI UAT were untouched.

A first full Infrastructure run produced no per-test progress output and was interrupted for a diagnostic rerun with verbose progress and a 90-second no-progress watchdog, without heap dumps. The final sticky-type refinement was then checked by rerunning every affected provider test.

Initial new parser assertions used exact `JsonException`, but .NET returns its derived `JsonReaderException`; assertions now require the JSON exception family while retaining nonblank, unparseable, content-free and Core-rejection requirements. No deadline was increased, test removed, authorization assertion relaxed or safety check weakened.

The push triggers the complete existing Synthetic workflow on the final exact commit. Hosted acceptance remains pending until that workflow completes; this request explicitly waives waiting. Local verification does not establish CI green. Live/paid tests remain opt-in and no external model calls were made.
