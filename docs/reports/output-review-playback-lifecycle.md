# Output review — playback response lifecycle

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Addresses whole-output findings `sf-001`, `sf-002`, and `sf-003` (family-playback-response-lifecycle). Synthetic/offline only.

## Behavior

- The output AudioWorklet admits a new `responseId` only after the previous response is closed (final queued sample consumed) or flushed. Flush and response begin reset `_consumed`.
- The browser sends `playback.completed` only when `isFinal` has arrived, queued depth is 0, and consumed samples cover every sent sample. Completion then clears response-local playback state so the next turn initializes independently.
- Voice terminal heard offsets use `SpokenUntilAccumulator.Credit` from acknowledged samples. A `playback.completed` report with consumed samples below `_sentSamples` does not finish the response.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter FullyQualifiedName~SpeechPlaybackTests` | repository root | 0 — 5 passed, including premature-completion heard credit |
| `npm run test -- --run` | `web/` | 0 — 18 Vitest tests |
| `npm run build` | `web/` | 0 |
| `CI=1 npx playwright test e2e/voice-playback.spec.ts e2e/voice-interrupt.spec.ts` | `web/` | 0 — 3 passed (one-turn capture-during-playback, two completed voice turns, R1 flush before R2 render with no post-flush R1 samples) |
