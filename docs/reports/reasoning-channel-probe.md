# Real-provider reasoning channel probe

Date: 2026-09-19. Commit: 9316f34 plus native Real config alignment.

## Configuration under test

- Model: `deepseek/deepseek-v4.1-flash` (pinned Real default, not `openrouter/free`).
- Reasoning effort: `medium`.
- Wire: `ReasoningObjectWire=true`, `ExcludeVisibleReasoning=true` (reasoning object with `exclude: true`).
- Native `http-openrouter` launch profile and `docker-compose.real.yml` now carry identical reasoning settings.

## Probe

`OpenAICompatibleLanguageModelTests.DeepSeek_v41_conversational_probe_keeps_reasoning_out_of_display_and_history`
(opt-in: `AGENTCORE_LIVE_PROVIDER_TESTS=1` + `OPENROUTER_API_KEY`).

Two bounded conversational turns through the full `SessionRuntime` (not adapter-only):

| Agent | User turn | Result |
| --- | --- | --- |
| `general-assistant` (Riley) | "Hello" | Pass |
| `examiner` (Alex) | "Hi" | Pass |

Verified per probe:

- SSE wire diagnostics observed choice fields (content and/or reasoning) for both agents.
- `ModelCompleted` received; finish reason mapped by the runtime.
- Separate reasoning fields, when present, arrived as `ModelReasoningDelta` and never as `ModelTextDelta`.
- Public assistant text equals concatenated `TextDeltaOutput` content; no reasoning text in display, history entries, or `SpeechText` (null in text mode).
- No provider/model planning was observed inside `delta.content` in this run. If a future run shows planning in `delta.content`, that is provider/model behavior; do not add regex/content heuristics to reclassify it.

Companion adapter evidence: `DeepSeek_v41_reasoning_probe_records_wire_field_presence` (pass, same configuration).

## Deterministic coverage (key-free, always on)

- `ReasoningChannelRuntimeTests`: reasoning deltas never become assistant display text.
- `SpokenOutputTests`: fenced, table, indented, and un-fenced technical regions resolve no-speech without explicit `speechText`; explicit `speechText` stays authoritative.
- `VoiceRealtimeRegressionTests` / `DisplayPipelineRegressionTests`: display pipeline and voice fallback boundaries.

## Known pre-existing issue

`InteractionControllerTests`, `SpeechPlaybackTests`, `BargeInTests`, `ToolWorkflowRuntimeTests` show wall-clock
timeout failures (10s output waits) on this machine at HEAD 9316f34, identical with and without this change
(7 failed / 4 passed in `InteractionControllerTests` in both directions, clean environment). Not introduced by
the reasoning isolation or rich-display fallback work; tracked separately.
