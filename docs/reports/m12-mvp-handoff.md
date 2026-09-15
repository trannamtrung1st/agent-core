# MVP handoff (Milestone 12)

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline default. No hosted keys, microphone, speaker, or GPU were required.

## Twelve mandatory offline gates

| Milestone | Owner report | Observed gate |
| --- | --- | --- |
| 1 | [m01-verification](m01-verification.md) | `dotnet test` + web unit/build |
| 2 | [m02-verification](m02-verification.md) | two identities, one mailbox |
| 3 | [m03-verification](m03-verification.md) | offline SSE; live OpenRouter skipped |
| 4 | [m04-verification](m04-verification.md) | controller races |
| 5 | [m05-verification](m05-verification.md) | SignalR Playwright + Compose smoke |
| 6 | [m06-verification](m06-verification.md) | synthetic STT / preflight |
| 7 | [m07-verification](m07-verification.md) | segmentation + TTS playback |
| 8 | [m08-verification](m08-verification.md) | full-duplex mute/disconnect |
| 9 | [m09-verification](m09-verification.md) | spoken-until / barge-in |
| 10 | [m10-verification](m10-verification.md) | idle/environment initiative |
| 11 | [m11-verification](m11-verification.md) | SQLite crash reopen |
| 12 | [m12-demo-verification](m12-demo-verification.md) + this report | measurements + Compose volume |

This packaging batch:

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 64, Infrastructure 41 passed + 3 skipped live smokes, Api 14 |
| `npm run test -- --run` | `web/` | 0 — 15 Vitest tests |
| `./scripts/compose-sqlite-volume.sh` | repository root | 0 — session `dacaccc4-9980-4ede-ba64-cf991dfd8f62` survived `--force-recreate`; `/` HTTP 200; `/api/v1/missing` HTTP 404 |

Playwright (`CI=1 npx playwright test`, 5 passed) was last recorded on the measurement item; this packaging change did not alter `web/` sources.

## Measured latencies

Synthetic 20-turn `AgentCore.Runtime` histograms with observed count/p50/p95/max: [m12-stage-latencies.md](m12-stage-latencies.md) and [m12-demo-verification.md](m12-demo-verification.md). Values are process-local Stopwatch samples on Scripted/Synthetic adapters, not SLAs.

## Commit map (implementation milestones)

| Milestone | Commit |
| --- | --- |
| 1 | `c6521d5` |
| 2 | `1ea5a4f` |
| 3 | `dce61da` |
| 4 | `1ebcf31` |
| 5 | `0650edb`, `9ab07e0` |
| 6 | `32dc8f2` |
| 7 | `c5cfc02` |
| 8 | `bbaf0d5` |
| 9 | `e688d6a` |
| 10 | `5aa0f75` |
| 11 | `e5921f2` |
| 12 demo | `e2eb3c3` |
| 12 packaging | this commit (`feat(m12): harden Compose SQLite volume`) |

## Output review remainder

Whole-output findings for receipts, parallel admission, EF migrate reopen, profile seed races, SQLite retry/failed-end, isolated stage tables, and `playback.stopped` consumed position are recorded in [output-review-gate-evidence.md](output-review-gate-evidence.md). TDP completion records the live Git HEAD; this file does not self-hash that commit.

## Files by concern (packaging)

- One container + SQLite volume: `Dockerfile`, `docker-compose.yml`, `scripts/compose-sqlite-volume.sh`
- Shutdown drain: `SessionHost.DrainAsync`, `SessionShutdownHostedService`
- Backup/restore: `SqliteMemoryStore.BackupToAsync`, `MemoryStoreContractTests.Backup_restore_reopens_the_session`

## Deferred / opt-in

- Live OpenRouter/OpenAI adapter smokes (`AGENTCORE_LIVE_PROVIDER_TESTS`, `AGENTCORE_LIVE_OPENAI_TTS`) — skip without keys
- Manual headset/speaker pass — deferred without `OPENAI_API_KEY`
- Real/hosted Compose — documented environment overrides only; default compose remains Synthetic/scripted

## Limitations

Single process owns SessionManager; do not replica-scale against one SQLite file. Authentication is out of MVP. Native speech-to-speech is excluded. `docker compose down -v` deletes durable demo data.
