# Automation authorization provider wiring correction

Date: 2026-10-16. Focused bug fix; no milestone acceptance or CI requested.

## Cause and correction

The reported request, “everyday at 9am, think about how you can consolidate your memory, experience”, falls outside the intentionally bounded timed imperative recognizer and uses model classification. Real DI supplied the unbound primary `ILanguageModel`, whereas ordinary Chat resolves its model through the catalog. The primary options need not declare developer-role support: that declaration and transport belong to the selected catalog descriptor. The authorization request contains Runtime/developer messages, so the raw adapter rejected it locally as UnsupportedCapability before HTTP. The classifier surfaced this as `authorization_classifier_unavailable` / ProviderFailure. Local development logs showed repeated failures in milliseconds between successful Chat HTTP requests.

The new offline regression reproduced five failures before correction: even configured allow, deny, ambiguous and malformed responses appeared unavailable, and the fake HTTP handler received no request. Real authorization now pins the operator catalog default through `SessionModelBinder`, resolves it through `ILanguageModelResolver` with purpose `TriggerAuthorization`, and passes its validated reasoning effort. This preserves declared transport, separate Core/system and Runtime/developer roles, default model identity and immutable provider options. The existing semantic wrapper passes the classifier's plain JSON through because this request has no response contract. Synthetic continues to use heuristics without credentials. No heuristic vocabulary, authorization gate, timeout, retry or token limit changed.

## Runtime evidence

The disposable SQLite journey installs production Real authorization/model registrations in the existing Synthetic host. Conversation and work decisions are scripted; the actual provider adapter sends requests only to an in-process HTTP/SSE fixture.

The exact request creates one daily background Automation at 09:00 in the fixture's initial UTC timezone. The next turn, “use vietname timezone”, classifies with the saved referent and updates that same Automation to Asia/Ho_Chi_Minh. Instructions, original authorization provenance, background destination and no-report-back setting remain intact. After reopening SQLite with the same catalog, a scan one second before 02:00 UTC admits nothing; the due scan admits and completes one quiet NoAction Run. Repeated scanning/intake/execution produces no duplicate. The next day's slot repeats the same result, for two distinct completed occurrences and no completion summary in the original Chat.

Separate denied, malformed and unavailable classifier responses return their respective safe errors and create no Automation. Existing recurring Chat consolidation tests also exercise current consolidation permission and revocation; the new wiring fixture itself performs no Memory/Experience mutation.

This is an execution test of wiring, persistence, timezone updates and scheduling. Classifier decisions and model tool arguments are fixtures, so it does not establish hosted model judgment or replay the original failed conversation into a nonexistent saved referent. An installation still running the old assembly needs an intentional restart and a fresh complete scheduling request.

## Focused verification

All commands run from the repository root. Prefix: `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0`. Build-enabled commands also use `--no-restore -m:1 -p:UseSharedCompilation=false --nologo`.

- Infrastructure: `dotnet test tests/AgentCore.Infrastructure.Tests` with filter `FullyQualifiedName~AutomationAuthorizationProviderTests|FullyQualifiedName~ModelCatalogFactoryTests|FullyQualifiedName~AutomationActivationToolTests|FullyQualifiedName~ThreeLayerProviderTests`: **65 passed, one explicit live-provider test skipped**. Includes both Chat Completions and Responses, catalog model/effort selection, distinct outcomes, hierarchy preservation and activation boundaries.
- Application: `dotnet test tests/AgentCore.Application.Tests` with filter `FullyQualifiedName~ModelTriggerCommandAuthorizerTests|FullyQualifiedName~TriggerCommandAuthorizerTests|FullyQualifiedName~ScheduleDraft|FullyQualifiedName~SessionModelBinderTests`: **123 passed**.
- Runtime/API: `dotnet test tests/AgentCore.Api.Tests --no-build --no-restore --nologo` with filter `FullyQualifiedName~AutomationAuthorizationJourneyTests|FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~ChatAutomationJourneyTests|FullyQualifiedName~SchedulerReadinessTests`: **45 passed**. The four new journeys also passed separately after fixture setup was corrected.
- Relative file links in the three edited canonical documents resolve; focused diff whitespace check passes.

The sandbox initially blocked the test runner's loopback socket. The same focused checks ran successfully with the required execution permission. No hosted provider requests, browser/UI changes, running-host restart, user-data mutation or CI run occurred. Concurrent workspace/Sandbox and frontend changes belong to separate work and are excluded from this fix's commit.
