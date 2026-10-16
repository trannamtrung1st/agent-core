# Explicit browser close operation — 2026-10-16

## Problem and change

A requested browser close can fail when a model invents arguments for the empty-object tool schema. Read-only inspection of the reported local Run's durable tool calls confirmed `{"request":{"kind":"browser.close"}}`, nested request wrappers and `{"request":{}}`; all were rejected before native dispatch. A later Run tried placeholder properties and eventually sent `{}`, which returned SDK-confirmed `closed`. There was no native close failure in that evidence.

`browser.close` now advertises an optional `operation` string whose only allowed value is `close`. The description and validation feedback show `{"operation":"close"}` and the compatible `{}` form explicitly, discouraging request wrappers and dummy fields. Both forms dispatch the same existing Session-owned `BrowserClose` command. No provider-specific DTO or new browser lifecycle path is introduced.

Blank/absent payload compatibility is retained only when the offered schema is a zero-property object or this exact optional fixed close selector. Other optional-parameter schemas, explicit null, arrays, malformed JSON, wrong/case-mismatched operations, duplicate selectors and additional fields remain rejected. Explicit provider string bytes, call identity and continuation are preserved. Existing authority, approval, cancellation, recovery budgets, native close confirmation and profile-retention semantics are unchanged. Closure still does not prove application sign-out.

## Focused local verification

The initial added explicit-operation executor case failed before implementation: one failure, five compatibility cases passed, with zero browser calls for the new form. After implementation the smallest changed executor/native runtime selection passed 11 cases.

Final checks ran from the repository root with no provider credentials or live generation:

- `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~BrowserToolTests|FullyQualifiedName~ToolArgumentPayloadTests|FullyQualifiedName~BrowserCloseRecoveryRuntimeTests|FullyQualifiedName~Recovered_owned_run_executes_pending_corrected_close_once_after_invalid_strategy_exhaustion|FullyQualifiedName~Owned_run_corrects_invalid_close_without_disabling_cleanup_or_unrelated_tools|FullyQualifiedName~Close_with_null_arguments_rejects_then_recovers_with_empty_object' --logger 'console;verbosity=minimal' -m:1 /nodeReuse:false` — **89 passed**.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Focused_browser_schemas_map_to_provider_function_parameters|FullyQualifiedName~Provider_preserves_close_argument_bytes_for_core_validation|FullyQualifiedName~Provider_distinguishes_absent_arguments_from_explicit_null|FullyQualifiedName~ResponsesTransportTests|FullyQualifiedName~BrowserCleanupTests' --logger 'console;verbosity=minimal' -m:1 /nodeReuse:false` — **48 passed**.
- `git diff --check` — passed for the scoped change.

| Exercised path | Expected and observed |
| --- | --- |
| Executor close with `{}`, whitespace, absent payload or `{"operation":"close"}` | Exactly one call reaches the Session's browser port; the same `closed` result is returned. |
| Wrapper/dummy/authority/script fields, null/arrays, malformed JSON, non-close operation or duplicate operation | Validation rejects the call and no native browser effect occurs. |
| Runtime receives repeated malformed request wrappers, then an unrelated authorized observation and corrected close | A real headless Chromium page is closed; the owned native context is released. Existing strategy blocking does not disable the corrected call. Normal and cleanup-budget cases complete within eight model steps. |
| Repeat close after SDK-confirmed closure | `already_closed`, with no second confirmed close effect; sign-out remains unverified. |
| Restore a checkpoint containing a pending corrected explicit close after invalid-strategy exhaustion | The same owned Run/Response resumes, preserving raw call identity and continuation. The native context closes and exactly one tool receipt is recorded for that pending call. |
| Chat Completions and Responses schemas/argument events | Both advertise only the optional fixed operation and reject additional schema properties. Both preserve raw arguments and call IDs for Core validation, including invalid transport types. |
| Existing cleanup failure and profile-retention cases | Closed/already-closed/failed/uncertain lifecycle distinctions and retained saved-profile behavior continue to pass. |

Native browser execution used the existing local Synthetic/scripted runtime fixtures and disposable Chromium contexts. No user browser context, saved profile, Definition, Session or schedule was changed. No new frontend behavior was introduced, so the affected use case was verified through the integrated runtime/native browser path rather than a UI-only check.

Hosted CI, full suites and live-model adherence to the revised tool schema were not run or requested. Existing published Definitions need no new version for this Core tool contract. The already-running application was left untouched; its backend must restart to load the updated schema and validator. This is a focused fix, not milestone acceptance.
