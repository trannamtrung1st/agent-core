# Milestone 11 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No speaker, GPU, or hosted keys. Playwright uses Chromium fake media device flags.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 57, Infrastructure 40 passed + 3 skipped live smokes, Api 12 |
| `npm run test -- --run` | `web/` | 0 — 14 Vitest tests |
| `npm run build` | `web/` | 0 |
| `CI=1 npx playwright test` | `web/` | 0 — 5 passed |

Observed: SQLite temp-file tests share revision/idempotency/conflict with InMemoryMemoryStore; crash reopen pauses Attached sessions, interrupts Streaming entries, and clears PendingMode; streaming checkpoints skip unchanged completed rows; user turns wait for SaveAsync before IAgentBrain; GET/session.ready public history remains received-clamped without summary. Default Synthetic profile stays InMemory; `Persistence:Provider=Sqlite` creates schema at startup and recovers crashed sessions. WAL companions must be included in backups.
