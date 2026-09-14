# System Architecture

## High-level architecture

```text
┌─────────────────────────────────────────────────────┐
│                     Web Client                      │
│                                                     │
│  Chat UI            Voice Call UI                   │
│  Text Input          Microphone / Playback          │
└───────────────────────────┬─────────────────────────┘
                            │
                            │ realtime transport
                            ▼
┌─────────────────────────────────────────────────────┐
│                   Session Server                    │
│                                                     │
│  ┌───────────────────────────────────────────────┐  │
│  │             Interaction Controller            │  │
│  │                                               │  │
│  │ speech activity / partial text / timers       │  │
│  │ interruption / queue / ignore / inject        │  │
│  └──────────────────────┬────────────────────────┘  │
│                         │ normalized events          │
│                         ▼                            │
│  ┌───────────────────────────────────────────────┐  │
│  │                 Agent Runtime                 │  │
│  │                                               │  │
│  │ identity / policy / state / generation        │  │
│  └──────────────────────┬────────────────────────┘  │
│                         │                            │
│           ┌─────────────┼─────────────┐              │
│           ▼             ▼             ▼              │
│        Memory        Event Bus     Output Router      │
└───────────┬─────────────┬─────────────┬──────────────┘
            │             │             │
            ▼             ▼             ▼
        LLM Adapter    STT Adapter   TTS Adapter
            │             │             │
            └─────────────┴─────────────┘
                    Provider Layer
```

## Architectural principle

The domain/core layer should not import a vendor SDK.

Concrete provider SDKs belong in adapter modules. Core logic should depend on small interfaces.

Example dependency direction:

```text
Domain / Core
    ↓ depends on interfaces only
Application / Runtime
    ↓
Provider Interfaces
    ↑ implemented by
Adapters
    ├── OpenAI-Compatible
    ├── Synthetic
    ├── Fake / Test
    ├── Local Model
    └── Future Providers
```

This makes the runtime:

- testable;
- swappable;
- easier to benchmark;
- easier to run offline or synthetically;
- less dependent on one provider's feature model.

## Major modules

### Agent Definition

Static/reusable configuration describing identity and policy.

Suggested concepts:

```text
AgentDefinition
├── identity
├── goals
├── behaviorPolicy
├── conversationPolicy
├── initiativePolicy
├── voiceConfig
└── modelPreferences
```

### Agent Runtime

One active agent instance in a session.

Responsibilities:

- consume normalized events;
- update conversation/session state;
- build model context;
- decide whether to respond;
- start/cancel generation;
- emit response events;
- coordinate memory writes;
- expose state to the controller.

### Interaction Controller

Independent loop that arbitrates live input and environment signals.

Responsibilities:

- speech activity tracking;
- interruption classification;
- turn ownership;
- queueing;
- idle timers;
- external event normalization;
- proactive triggers.

### Event Bus

A typed internal stream connecting runtime components.

The bus should support:

- ordered events within a session;
- event identifiers;
- timestamps;
- optional causation/correlation IDs;
- cancellation/supersession;
- instrumentation hooks.

For the MVP this can be in-process. It does not need Kafka, Redis Streams, or another distributed broker.

### Output Router

Converts agent intentions into output channels.

Examples:

- text delta -> web client;
- text response -> chat history;
- speech request -> TTS;
- audio chunks -> client;
- stop speech -> playback cancellation;
- UI state -> client.

### Memory

Use an interface so storage can change later.

MVP implementations can be:

- in-memory for tests;
- SQLite/Postgres for sessions;
- simple JSON summary fields.

### Provider Layer

Provider adapters implement generic runtime interfaces.

The first adapter should support an OpenAI-compatible HTTP API. This should not leak provider-specific request/response objects into the core runtime.

## Suggested backend package layout

```text
backend/
├── core/
│   ├── agent/
│   ├── interaction/
│   ├── events/
│   ├── conversation/
│   └── memory/
│
├── application/
│   ├── session/
│   ├── orchestration/
│   └── services/
│
├── ports/
│   ├── language-model.ts
│   ├── speech-recognizer.ts
│   ├── speech-synthesizer.ts
│   ├── realtime-transport.ts
│   ├── memory-store.ts
│   ├── clock.ts
│   └── id-generator.ts
│
├── adapters/
│   ├── openai-compatible/
│   ├── synthetic/
│   ├── memory/
│   └── transport/
│
├── api/
│   ├── http/
│   └── websocket/
│
└── tests/
    ├── unit/
    ├── integration/
    └── conversation/
```

The exact language/framework is open. The important boundary is ports/interfaces in the center and adapters at the edge.

