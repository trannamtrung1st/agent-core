# Output review revision — delivery receipts, admission, resampling, migrations

Date: 2026-09-15

## Commands

- `dotnet test tests/AgentCore.Application.Tests` — exit 0 (69 passed)
- `dotnet test tests/AgentCore.Infrastructure.Tests` — exit 0 (41 passed, 3 skipped live opt-in)
- `dotnet test tests/AgentCore.Api.Tests` — exit 0 (21 passed, including 13 JavaScript MessagePack scenarios)
- `pnpm run test --run` (web/) — exit 0 (20 passed)
- `pnpm run build` (web/) — exit 0

## Fixes in this revision

- `response.received` is sent from the browser every 100 ms and on final text render. The host validates identity/monotonic offsets and the runtime persists received/heard only from those receipts (and playback credit), not from generated length.
- Post-attach controls require the current `attachmentId`. Audio `sessionId` must match the attached session. Control admission is locked per attachment with a 1,024-entry eventId/fingerprint/ack table. Mailbox rejection returns `Backpressure` instead of a false accept. Hub methods use typed payload DTOs, check `type` against the method, and fatal protocol errors abort the HTTP connection after the ack is sent.
- Input worklet resampling keeps fractional phase and a one-pole filter across quanta; long-run 44.1 kHz tests cover count and alias energy.
- SQLite startup uses `Database.MigrateAsync` with an InitialCreate EF migration. Sessions associate the local MVP profile (`language`, `preferredName`) and both stores validate the allowlist.

Remaining review families (measurement numeric table, completion HEAD rebind) are not claimed in this batch.
