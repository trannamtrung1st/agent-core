# Milestone 1 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-14. Default suites used Synthetic adapters only; no `OPENROUTER_API_KEY` / `OPENAI_API_KEY` opt-in.

## Commands and results

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 |
| `npm ci` (after first `npm install` to create the lockfile) | `web/` | 0 |
| `npm run test -- --run` | `web/` | 0 |
| `npm run build` | `web/` | 0 |

Backend totals: Domain 2, Application 2, Infrastructure 4, Api 5 passed.

Observed Application assertions:

- Ordered `ScriptedLanguageModel` deltas `Hello` / ` from ` / `synthetic.` with matching `textStart`, one `TextCompletedOutput`, one successful `ResponseCompletedOutput`.
- In-flight cancel after the first gated chunk: no `BBB`/`CCC` deltas, exactly one failed terminal, no `TextCompletedOutput`.

Observed Api assertions:

- `GET /health` returns `healthy` / `Synthetic` / protocolVersion 1 and `OutboundHttpProbe.Attempts == 0`.
- `POST /api/v1/sessions` 201, GET session, GET messages, DELETE 204 (repeat 204).
- Voice create returns 409 `VoiceUnavailable` (no STT/TTS adapters in Milestone 1).
- `POST /api/v1/sessions/{id}/messages` is 405; `POST /api/v1/chat` is 404.
- Project references: Domain has no package/project refs; Application does not reference Infrastructure or Contracts; Infrastructure does not reference Contracts.

Frontend: Vitest `App` heading/profile test passed; `vite build` succeeded.

## Not in this milestone

SignalR hub, Playwright, Docker/Compose, SQLite, real LLM/STT/TTS, and GitHub Actions workflows.
