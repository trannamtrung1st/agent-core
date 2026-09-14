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

While speaking with the support agent, use the in-process synthetic environment driver to submit an `EnvironmentUpdate` trigger with kind `order_status_changed`, orderReference `DEMO-001`, and status `shipped`. [Event Model](07-event-model.md) defines this application input; it is not an arbitrary public wire event.

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

## Demo 8 - Reconnect and heard context

Interrupt a long response after its first phrase, disconnect and reconnect. The interrupted text remains visibly marked in history, audio does not replay, and the next answer uses only the conservative heard prefix. Repeat after restarting the backend with SQLite enabled.

## Demo preparation and capability caveat

Use Synthetic first to verify scenarios without credentials; scripted STT supplies transcript content and synthetic TTS emits tones/silence for transport checks. Real mode prefers independently selected streaming STT/TTS with OpenRouter text through the compatible adapter; native speech-to-speech is excluded. Semantic backchannel demonstration requires partial-capable STT; batch STT intentionally uses speech-activity interruption and may interrupt on acknowledgements. Show that trade-off honestly in the developer panel.

Measure the interruption and playback timeline using [Operations](17-observability-and-operations.md). [Testing Strategy](16-testing-strategy.md) turns these scenarios into deterministic regression tests. Do not substitute a live-provider phrasing assertion for a synthetic correctness test.

## Demo 9 - Streaming speech and continuous listening

Use a long scripted answer. Confirm that a natural first Speech Segment starts playback before the LLM finishes, and that STT continues receiving input during that playback. Show “okay” as a backchannel, then “wait” as a confirmed interruption. Assert that pending R1 segments never start and late playback events cannot alter R2. Repeat in hardware-free Synthetic Mode with simulated sample counters.

## Demo success feeling

The audience should come away thinking:

> This feels like talking to an agent that is present and listening, not sending prompts to a chatbot.

That perception is the actual MVP objective.
