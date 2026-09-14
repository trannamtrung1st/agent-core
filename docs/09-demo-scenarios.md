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

While speaking with the support agent, inject:

```json
{
  "type": "environment.external_update",
  "payload": {
    "source": "order_system",
    "kind": "order_status_changed",
    "data": {
      "status": "shipped"
    }
  }
}
```

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

No Agent Runtime code should change.

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

## Demo success feeling

The audience should come away thinking:

> This feels like talking to an agent that is present and listening, not sending prompts to a chatbot.

That perception is the actual MVP objective.

