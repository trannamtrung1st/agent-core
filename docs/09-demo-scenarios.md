# Demo Scenarios

The MVP demo should be short and interaction-focused.

## Demo 1 - Reusable identity

Run the same Agent Core with two different Agent Definitions.

### Examiner

Expected behavior:

- formal tone;
- one question at a time;
- restrained feedback;
- waits for user answers;
- may prompt after excessive silence.

### Customer support agent

Expected behavior:

- warmer and task-oriented;
- asks for missing information;
- summarizes next steps;
- can react to a simulated order-status event.

The point is to show that identity lives above the underlying provider.

## Demo 2 - Basic interruption

Agent begins a longer response.

User says:

> Wait, stop.

Expected result:

- current speech stops quickly;
- stale generated audio does not continue;
- the agent acknowledges the interruption naturally.

## Demo 3 - Semantic interruption

Agent is speaking.

User says:

> Mhm.

Expected result:

The agent continues.

Then the user says:

> Hang on, what do you mean by that?

Expected result:

- agent stops;
- controller emits an interruption event;
- agent answers the new question.

This demonstrates that the controller is not simply reacting to any microphone activity.

## Demo 4 - Proactive interaction

The examiner asks a question and the user remains silent.

After an appropriate threshold the controller emits an idle event.

The Agent Runtime may choose to say:

> Would you like a little more time, or should I repeat the question?

The exact wording should come from the agent identity, not from the controller.

## Demo 5 - Environment event

While speaking with the support agent, use the in-process synthetic environment driver through `IEnvironmentEventIngress` to submit kind `order_status_changed`, orderReference `DEMO-001`, and status `shipped`. [Event Model](07-event-model.md) defines this application input; it is not an arbitrary public wire event.

Expected result:

The Agent Runtime decides whether the event is important enough to mention and, if so, informs the user naturally.

## Demo 6 - Provider swap

Run the same conversation tests with:

```text
ScriptedLanguageModel
```

and then:

```text
OpenAICompatibleLanguageModel
```

Configure OpenAICompatibleLanguageModel first for OpenRouter hosted text, then a compatible local endpoint fixture or available local server. STT/TTS remain independently selected. No Agent Runtime or Interaction Controller code should change. A local server is optional for this demonstration; offline fixtures prove the boundary without a GPU.

This demonstrates that provider abstraction is real rather than cosmetic.

## Demo 7 - Cancellation correctness

Simulate a slow model stream.

Sequence:

```text
response A starts
user interrupts
response A is superseded
response B starts
late chunk from A arrives
```

Expected result:

The late chunk from A is discarded and never reaches the user.

This proves the runtime handles concurrency rather than only the happy path.

## Demo 8 - Reconnect, mode switch and heard context

Have a text exchange, start voice on the **same** session (preflight then `session.mode.set`), interrupt a long voice response after its first phrase, disconnect and reconnect. Interrupted text remains visibly marked, audio does not replay, and the next answer uses only the conservative heard prefix for that voice-delivered entry. A voice request that was still pending when the connection died does not resume; press Voice again. Return to text mode without creating a new session. Repeat after restarting the backend with SQLite enabled.

## Synthetic 20-turn measurement

Run the same identities through twenty synthetic text turns, then a full-duplex voice exchange, an interruption, restrained idle or environment initiative, and detach/reattach. Record per-stage `AgentCore.Runtime` durations as observed numbers (not SLAs). Distinguish generated, received, and heard text on assistant entries. The automated path is `TwentyTurnDemoTests`; a headset pass remains optional and key-gated.

## Demo preparation and capability caveat

Use Synthetic first to verify scenarios without credentials; scripted STT supplies transcript content and synthetic TTS emits tones/silence for transport checks. Real/demo text uses OpenRouter with a **fixed** operator-selected default model ID when `OPENROUTER_API_KEY` is supplied; `openrouter/free` is for adapter smoke and an explicit catalog choice, not the system default. Live OpenAI realtime transcription STT (recommended `gpt-live-transcribe`) and TTS may wait for `OPENAI_API_KEY`. Native speech-to-speech is excluded. Semantic backchannel demonstration requires partial-capable STT; batch STT intentionally uses speech-activity/final-transcript interruption and may interrupt on acknowledgements. Show that trade-off honestly in the developer panel.

Measure the interruption and playback timeline using [Operations](17-observability-and-operations.md). [Testing Strategy](16-testing-strategy.md) turns these scenarios into deterministic regression tests. Do not substitute a live-provider phrasing assertion for a synthetic correctness test.

## Demo 9 - Streaming speech and continuous listening

Use a long scripted answer. Confirm that a natural first Speech Segment starts playback before the LLM finishes, and that STT continues receiving input during that playback. Show “okay” as a backchannel, then “wait” as a confirmed interruption. Assert that pending R1 segments never start and late playback events cannot alter R2. Repeat in hardware-free Synthetic Mode with simulated sample counters.

## Demo success feeling

The audience should come away thinking:

> This feels like talking to an agent that is present and listening, not sending prompts to a chatbot.

That perception is the actual MVP objective.

## P5 schedule management (observed)

On Riley (`general-assistant` v8) with a trusted profile timezone, “remind me tomorrow” persists one owner-scoped one-shot schedule without an approval dialog. The chat header **Schedules** drawer lists intent, schedule, timezone, status, and next occurrence, and cancels the active row after confirmation. A second session for the same instance and profile sees the same row. Customer support does not offer user scheduling. There is no calendar and no admin trigger console.

## Post-MVP planned until verified

Observed later demos: durable multi-chat Support and Compliance flows with attachments, bounded work, artifacts, rich presentation, deactivation, and reopen (`SupportComplianceWorkflowTests`). Docker `sandbox.run` is a runtime capability, not a separate UI demo. Examiner MVP conversational demos above remain.
