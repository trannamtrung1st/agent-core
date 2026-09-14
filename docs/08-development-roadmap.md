# Development Roadmap

## Guiding principle

Build from interaction mechanics outward.

Do not begin with a large platform. Prove the live conversational loop first.

## Phase 0 - Project skeleton

Create the basic application structure.

Deliverables:

- web client;
- backend session server;
- typed event definitions;
- provider ports/interfaces;
- fake/synthetic provider implementations;
- minimal agent definition format;
- logging and session IDs.

Success condition:

A synthetic agent can exchange text events through the complete stack without calling any external AI provider.

## Phase 1 - Text Agent Runtime

Implement:

- Agent Definition loading;
- conversation state;
- Agent Runtime;
- generic `LanguageModel` port;
- OpenAI-compatible language-model adapter;
- streaming text output;
- cancellation/supersession;
- simple chat UI.

Success condition:

Two agent definitions produce noticeably different behavior through the same runtime.

Suggested initial agents:

- examiner;
- customer support representative.

## Phase 2 - Synthetic Interaction Controller

Before real voice, make interruption behavior testable using synthetic events.

Implement:

- controller state machine;
- event queue;
- idle timers;
- `INTERRUPT / CONTINUE / QUEUE / IGNORE` decisions;
- fake clock;
- scripted transcripts;
- response IDs and supersession.

Success condition:

Automated tests can reproduce user interruption and ensure stale responses are discarded.

## Phase 3 - Voice pipeline

Implement:

- microphone capture;
- streaming audio transport;
- generic `SpeechRecognizer` port;
- generic `SpeechSynthesizer` port;
- first STT/TTS adapters;
- partial transcript events;
- streaming TTS playback;
- call UI states.

Success condition:

The user can have a low-latency voice conversation without pressing a record/submit button for every turn.

## Phase 4 - Barge-in

Implement:

- continuous listening during agent speech;
- semantic interruption classification;
- agent audio stop/fade;
- cancellation propagation;
- spoken-until tracking;
- echo/self-transcript protection;
- backchannel handling.

Success condition:

"Wait, stop" interrupts immediately, while "mhm" usually does not.

This phase is the heart of the MVP.

## Phase 5 - Proactive interaction

Implement:

- controller idle events;
- initiative policy;
- rate limiting / minimum interval;
- external environment event injection;
- `stay_silent` as an explicit valid agent decision.

Success condition:

The agent can react naturally to silence or a simulated external update without becoming annoying.

## Phase 6 - Basic persistence

Implement:

- session persistence;
- minimal user profile;
- session summary;
- resume/reconnect behavior.

Avoid sophisticated memory infrastructure unless required by the demo.

## Phase 7 - Demo polish

Focus on perceived quality:

- reduce first-token latency;
- reduce first-audio latency;
- refine silence thresholds;
- refine interruption heuristics;
- improve cancellation behavior;
- make reconnects graceful;
- tune agent prompts/policies;
- add developer event timeline;
- clean up UI.

## Recommended test pyramid

### Unit tests

Test pure controller/runtime logic with fake providers.

### Deterministic conversation tests

Use scripted model/STT/TTS adapters to replay scenarios.

### Provider integration tests

Test the OpenAI-compatible adapter separately from core runtime behavior.

### Manual conversational tests

Use real microphone/speakers and test:

- speaking over the agent;
- short acknowledgements;
- background noise;
- long pauses;
- quick successive interruptions;
- slow provider responses;
- reconnects.

## Important engineering metrics

Capture timestamps for:

- user speech start;
- partial transcript availability;
- final transcript availability;
- interruption decision;
- model request start;
- model first token;
- TTS request start;
- first audio chunk;
- client playback start;
- cancellation requested;
- audio actually stopped.

These measurements will help improve experience more than generic request-duration metrics.

## Suggested first milestone

A strong first milestone is:

```text
Text chat
+ reusable AgentDefinition
+ OpenAI-compatible model adapter
+ synthetic model adapter
+ event-driven session runtime
+ fake Interaction Controller events
```

Then add live audio after the boundaries are stable.

