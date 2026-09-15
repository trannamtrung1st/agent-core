# Milestone 12 demo verification (measurement item)

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No speaker, GPU, or hosted keys. Headset/manual pass: deferred. Compose SQLite volume survival: not in this item.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 64, Infrastructure 40 passed + 3 skipped live smokes, Api 13 |
| `npm run test -- --run` | `web/` | 0 — 15 Vitest tests |
| `npm run build` | `web/` | 0 |
| `CI=1 npx playwright test` | `web/` | 0 — 5 passed |

## Observed stage latencies

`TwentyTurnDemoTests` runs 20 scripted text turns per identity (examiner, customer-support) plus voice duplex, interruption, idle or environment initiative, and detach/reattach. Durations are `Stopwatch` samples on `AgentCore.Runtime` (`controller`, `llm`, `persist`, `brain`, `stt`, `segmentation`, `tts`, `transport`, `playback` when that stage ran). On this host the 20-turn loop finished in well under one second total (Application suite 190 ms including other tests); per-turn scripted LLM first-token time is typically under 10 ms. These are measurements, not SLAs. Sample: Synthetic profile, in-process FakeTimeProvider, no network.

## Generated / received / heard

Assistant entries keep `Text` (generated), `ReceivedTextEndExclusive` (delivered), and `HeardTextEndExclusive` (playback-credited). Text-mode turns credit received as heard. Voice turns credit heard from spoken-until / playback acks. The 20-turn test asserts generated ≥ received ≥ heard on the last assistant entry.

## Remaining Milestone 12

Container SQLite volume across recreate, restore/restart packaging checks, and the full MVP handoff report.
