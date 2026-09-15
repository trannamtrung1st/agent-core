# Milestone 8 verification

Branch: `codex/tdp-mvp-run-20260914T184310-67b372`

Recorded 2026-09-15. Synthetic/offline only. No speaker, GPU, or hosted keys. Playwright uses Chromium fake media device flags (no physical microphone or speaker wait).

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | repository root | 0 — Domain 3, Application 42, Infrastructure 33 passed + 3 skipped live smokes (OpenRouter, OpenAI STT, OpenAI TTS), Api 12 |
| `npm run test -- --run` | `web/` | 0 — 14 Vitest tests |
| `npm run build` | `web/` | 0 |
| `CI=1 npx playwright test` | `web/` | 0 — 4 passed (text pending-voice, fake-device capture PCM, output worklet playback while capture stays active, duplex mute/disconnect) |

Observed: PCM ingress and Partial/Final transcripts continue while unacked R1 audio plays; slow TTS does not block STT; mute drops input frames without AudioDiscontinuity and without stopping playback acknowledgements; unmute issues a new streamId; detach stops recognition and further TTS jobs; Mute/Unmute is input-only in the browser; disconnect releases capture; `.github/workflows/synthetic.yml` runs Playwright voice scenarios without keys.
