# Output review revision — remaining required gates

Date: 2026-09-15

Closes remaining required members of dual-attach, realtime command validation, admission concurrency, disconnect-during-playback, reconnect voice preflight, output-rate conversion, `playback.stopped` flushed consumed, mute/unmute input reset, playback text offsets, full-host SQLite recovery, terminal end/detach ordering, ordered control dispatch, post-commit receipts, and isolated stage-latency provenance.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 4, Application 75, Infrastructure 45 passed + 3 skipped live smokes, Api 35 |
| `pnpm run test --run` | `web/` | 0 — 34 Vitest tests |
| `pnpm run build` | `web/` | 0 |
| `CI=1 pnpm run test:e2e` | `web/` | 0 — 8 passed |
| `./scripts/compose-sqlite-volume.sh` | repository root | 0 — session `d5e76900-423f-461c-94b3-14209fc77864` survived `--force-recreate` |
| `git diff --check` | repository root | 0 |

Isolated latency rewrite (`AGENTCORE_WRITE_STAGE_LATENCIES=1`) was not repeated; [m12-stage-latencies.md](m12-stage-latencies.md) already records Browser/device: N/A.

## Coverage bound to findings

- Dual-attach: `SessionHost.AttachAsync` rejects a connection that already owns another session; Kestrel JS `dual-attach` scenario.
- Realtime command validation: playback `textEndExclusive` monotonic and ≤ generated text; sample bounds; output audio identity/continuity, 60 ms early-audio buffer, cumulative 2 s queue.
- Admission: FIFO per-attachment dispatcher enqueues in admitted sequence; `CommandAdmissionTests` gated parallel mute commands keep both event IDs and leave the later sequence as final mute state.
- Disconnect during playback: `capture.release()` resolves flush waiters; `interruptPlayback` clears `flushing` in `finally`; Playwright reconnect-after-disconnect playback.
- Reconnect voice: no `capture.start()` without prepared resources; durable voice still requires Voice click; Mute/Listening only when capture is live.
- Output resampling: stateful 24 kHz → device rate; canonical consumed advances only from rendered device frames; exact 44.1 kHz and 48 kHz worklet counts.
- `playback.stopped`: worklet captures consumed after the extra render quantum, returns it in `flushed`; `flushPlayback` uses the acknowledgement.
- Mute/unmute: `streamGeneration` stops admission before `speechEnded`; gated in-flight frames are dropped across mute/unmute.
- Playback text offsets: `reportCommittedEntries` runs from a ChatApp `useLayoutEffect`; playback receipts stay per-response and monotonic.
- Voice received prefix: playback reports update `ReceivedTextEndExclusive` independently of heard; reconnect public history keeps the rendered voice text.
- Output overflow: worklet admission is authoritative before sent-sample completion; cumulative multi-frame overflow fails/flushes rather than wedging `playback.completed`.
- Terminal end: HandleDetach does not persist `Paused` after `Ended`/`Ending`; PersistAsync retries 1/2/5 s (max 3); later SessionManager save failure plus process restart still rejects attach.
- Full-host SQLite: lost-ack waits for durable user history then drops the process before CommandAck; retry after reconstruction keeps one user entry; failed end save does not accept or persist `Ended`.
- Profile insert conflict translation uses SQLite unique-key extended codes 1555/2067 only.
- Isolated latency generation uses `AGENTCORE_WRITE_STAGE_LATENCIES=1`; the default suite does not rewrite the table; Browser/device provenance is N/A for in-process FakeTimeProvider samples.
