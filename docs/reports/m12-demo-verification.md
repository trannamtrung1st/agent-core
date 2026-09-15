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

`TwentyTurnDemoTests` runs 20 scripted text turns per identity (examiner, customer-support) plus voice duplex, interruption, idle or environment initiative, and detach/reattach. Stage durations are `Stopwatch` deltas on `AgentCore.Runtime` (not placeholders). Durable table: [m12-stage-latencies.md](m12-stage-latencies.md). Host sample recorded 2026-09-15, Synthetic profile, in-process FakeTimeProvider, no network:

| Stage | Count | p50 (ms) | p95 (ms) | max (ms) |
| --- | ---: | ---: | ---: | ---: |
| controller | 46 | 0.005 | 0.815 | 1.415 |
| llm | 50 | 0.001 | 0.030 | 0.959 |
| persist | 176 | 0.001 | 0.004 | 0.049 |
| stt | 2 | 0.111 | 2.400 | 2.400 |
| segmentation | 4 | 0.038 | 3.101 | 3.101 |
| tts | 4 | 0.024 | 3.945 | 3.945 |
| transport | 4 | 0.029 | 0.675 | 0.675 |
| playback | 1 | 10.714 | 10.714 | 10.714 |

These are measurements, not SLAs. Re-run `dotnet test --filter Twenty_turn_synthetic_demo_records_observed_stage_latencies` to refresh the table file.

## Generated / received / heard

Assistant entries keep `Text` (generated), `ReceivedTextEndExclusive` (delivered), and `HeardTextEndExclusive` (playback-credited). Text-mode turns credit received as heard. Voice turns credit heard from spoken-until / playback acks. The 20-turn test asserts generated ≥ received ≥ heard on the last assistant entry.

## Remaining Milestone 12

None for this measurement item. Container SQLite volume, restore/restart packaging, and the MVP handoff report are in [m12-mvp-handoff](m12-mvp-handoff.md).
