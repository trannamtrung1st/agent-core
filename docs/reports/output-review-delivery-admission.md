# Output review revision — delivery receipts, admission, resampling, measurements

Date: 2026-09-15

## Commands

- `dotnet test tests/AgentCore.Application.Tests` — exit 0 (68 passed; 20-turn demo writes [m12-stage-latencies.md](m12-stage-latencies.md))
- `dotnet test tests/AgentCore.Infrastructure.Tests` — exit 0 (41 passed, 3 skipped live opt-in)
- `dotnet test tests/AgentCore.Api.Tests` — exit 0 (23 JavaScript MessagePack scenarios including audio session identity and speech boundary ordering)
- `pnpm run test --run` (web/) — exit 0 (20 passed; unchanged this revision)
- `pnpm run build` (web/) — exit 0 (prior revision)

## Fixes in this revision

- Per-stage telemetry uses distinct Stopwatch marks (STT start→partial/final, first segment text→release, TTS job→first audio, first audio→send, send→playback ack). `TwentyTurnDemoTests` asserts count/p50/p95/max for controller, llm, persist, stt, segmentation, tts, transport, and playback, and writes the observed table.
- Kestrel JS fixtures cover mismatched `SendAudio` sessionId (fatal close) and `user.speech.ended` held until preceding PCM samples.
- Protected `.agents/skills/testing/SKILL.md` restored to the pre-run npm wording.

TDP completion, not this file, records the live HEAD after remaining gate evidence is committed.
