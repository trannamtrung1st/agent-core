# Milestone 5 verification (browser SignalR text path)

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No microphone, speaker, GPU, or hosted keys.

This report covers the first Milestone 5 work item (hub, leases, pending voice, Playwright). Compose and GitHub Actions remain a separate work item.

## Commands and results

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 19, Infrastructure 25 passed + 1 skipped live smoke, Api 12 |
| `npm ci` / `npm install` | `web/` | 0 |
| `npm install` | `tests/realtime-js/` | 0 |
| `npm run test -- --run` | `web/` | 0 — 5 Vitest tests |
| `npm run build` | `web/` | 0 |
| `npx playwright test` | `web/` | 0 — 1 passed |

## Observed

- `/hubs/session` uses SignalR + MessagePack. Hub methods forward to `SessionHost`.
- JavaScript MessagePack client covers protocol version mismatch, client `correlationId` rejection, second-connection `SessionInUse`, backwards sequence `StaleCommand`, `SessionCapacityExceeded` with `retryAfterMs=5000` while HTTP create still succeeds, and a synthetic text round-trip.
- Pending voice: `PendingVoiceTimeoutMs` on `FakeTimeProvider` clears `pendingMode`, leaves `Mode=Text`, no STT (`InputActivity.Idle`).
- Reconnect attach emits `session.ready` with received-clamped history, `deliveryMode`, null `pendingMode` / `activeResponseId`, and no session summary field.
- Frontend reducers reject late R1 text after tombstone and flag control-sequence gaps.
- Playwright: text exchange, Starting voice… / Cancel, zero `SendAudio` frames, disconnect shows Reconnecting.

## Not in this work item

Dockerfile, Compose smoke, and GitHub Actions workflows.
