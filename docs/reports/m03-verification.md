# Milestone 3 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Default suite is Synthetic/offline. Live OpenRouter smoke is skipped unless `AGENTCORE_LIVE_PROVIDER_TESTS=1` and `OPENROUTER_API_KEY` are both set.

## Commands and results

| Command | Exit status |
| --- | --- |
| `dotnet test AgentCore.sln --nologo` | 0 — Domain 3, Application 6, Infrastructure 25 passed + 1 skipped (`OpenRouter_free_smoke_is_opt_in_only`), Api 6 |

Observed:

- `LanguageModelAdapterFactory` / profile selection run `ScriptedLanguageModel` (Synthetic, including when OpenAI-compatible options are present) or `OpenAICompatibleLanguageModel` (Real + Adapter=OpenAICompatible) through the same `SessionRuntime` mailbox.
- Offline handler fixtures cover chunk/UTF-8 splits, CRLF comments, role/usage/reasoning ignore, missing finish, cancellation, 429 Retry-After, midstream error, malformed JSON, oversized SSE, 401 and 503. Each `GenerateAsync` issues one POST; a later generate is a new POST, not a replay of partial text.
- Committed `appsettings.json` uses `Adapter: Scripted` and does not set `openrouter/free`. Real/demo `DefaultModel` is documented as an operator-fixed OpenRouter id; `openrouter/free` is smoke-only.
- `OPENROUTER_API_KEY` binds only in the API host into Infrastructure `ApiKey`. Frontend and Contracts contain no provider keys.

Live smoke (`OpenRouter_free_smoke_is_opt_in_only`) is not part of the default filter; it does not assert phrasing or routed-model quality.
