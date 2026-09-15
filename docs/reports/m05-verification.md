# Milestone 5 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No microphone, speaker, GPU, or hosted keys.

## Browser SignalR text path

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 19, Infrastructure 25 passed + 1 skipped live smoke, Api 12 |
| `npm run test -- --run` | `web/` | 0 — 5 Vitest tests |
| `npm run build` | `web/` | 0 |
| `npx playwright test` | `web/` | 0 — 1 passed |

Observed: `/hubs/session` MessagePack leases, pending-voice timeout/disconnect, Kestrel JavaScript protocol fixtures, Playwright text exchange with Starting voice… / Cancel and no PCM.

## Compose and CI

| Command | Exit status |
| --- | --- |
| `docker compose up --build -d` | 0 |
| `curl -sS http://127.0.0.1:5080/health` | 0 — `{"status":"healthy","profile":"Synthetic","protocolVersion":1}` HTTP 200 |
| `curl` `GET /` | HTTP 200 (built SPA) |
| `curl` `GET /api/v1/missing` | HTTP 404 (SPA fallback does not swallow API) |
| `docker compose down` | 0 |

GitHub Actions: `.github/workflows/synthetic.yml` runs `dotnet test`, web unit tests/build, and synthetic Playwright. No OpenAI/OpenRouter keys, microphone, speaker, or GPU.

Native `dotnet run` + Vite remains the fast loop; Compose is optional integration/demo.
