# Output review revision — remaining required gates

Date: 2026-09-15

Closes remaining required members of dual-attach, realtime command validation, admission concurrency, disconnect-during-playback, reconnect voice preflight, output-rate conversion, `playback.stopped` flushed consumed, mute/unmute input reset, playback text offsets, full-host SQLite recovery, and isolated stage-latency provenance.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 4, Application 73, Infrastructure 45 passed + 3 skipped live smokes, Api 34 |
| `pnpm run test --run` | `web/` | 0 — 33 Vitest tests |
| `pnpm run build` | `web/` | 0 |
| `CI=1 pnpm run test:e2e` | `web/` | 0 — 8 passed |
| `./scripts/compose-sqlite-volume.sh` | repository root | 0 — session `c5a5e188-9305-40c5-aa86-63f431d11b53` survived `--force-recreate` |
| `AGENTCORE_WRITE_STAGE_LATENCIES=1 dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~Twenty_turn_synthetic_demo_records_observed_stage_latencies --nologo` | repository root | 0 — 1 passed; rewrote [m12-stage-latencies.md](m12-stage-latencies.md) |
| `git diff --check` | repository root | 0 |

## Coverage bound to findings

- Dual-attach: `SessionHost.AttachAsync` rejects a connection that already owns another session; Kestrel JS `dual-attach` scenario.
- Realtime command validation: playback `textEndExclusive` monotonic and ≤ generated text; sample bounds; output audio identity/continuity, 60 ms early-audio buffer, cumulative 2 s queue.
- Admission: `TryAdmitAsync` releases `Admission` before mailbox/persist; in-flight TCS preserves dedupe; `CommandAdmissionTests` barrier retries both accepted event IDs.
- Disconnect during playback: `capture.release()` resolves flush waiters; `interruptPlayback` clears `flushing` in `finally`; Playwright reconnect-after-disconnect playback.
- Reconnect voice: no `capture.start()` without prepared resources; durable voice still requires Voice click; Mute/Listening only when capture is live.
- Output resampling: stateful 24 kHz → device rate; consumed counts are canonical samples; 44.1 kHz and 48 kHz worklet coverage.
- `playback.stopped`: worklet captures consumed after the extra render quantum, returns it in `flushed`; `flushPlayback` uses the acknowledgement.
- Mute/unmute: input worklet pause/reset; unmute starts a fresh stream so pre-mute audio cannot enter.
- Playback text offsets: acknowledgements send the highest rendered text offset; heard credit remains sample/timing based.
- Full-host SQLite: HTTP/SignalR lost-ack retry after host reconstruction keeps one user entry; failed end save does not accept or persist `Ended`.
- Isolated latency generation uses `AGENTCORE_WRITE_STAGE_LATENCIES=1`; the default suite does not rewrite the table.
