# Milestone 9 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No speaker, GPU, or hosted keys. Playwright uses Chromium fake media device flags.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 49, Infrastructure 33 passed + 3 skipped live smokes, Api 12 |
| `npm run test -- --run` | `web/` | 0 — 14 Vitest tests |
| `npm run build` | `web/` | 0 |
| `CI=1 npx playwright test` | `web/` | 0 — 5 passed |

Observed: Wait barge-in emits `playback.stop` then `agent.response.interrupted` while STT stays active; late R1 playback does not revise heard offsets; no-timing-mark credit excludes the partial current segment from the next prompt; speechActivity interrupts at the degraded deadline; local duck/restore and worklet flush prevent stale R1 audio from rendering as R2.
