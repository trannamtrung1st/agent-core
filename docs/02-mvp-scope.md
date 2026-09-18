# MVP Scope

## MVP question

The MVP should answer one question convincingly:

> Can a user have a natural, persistent, interruptible conversation with an AI agent that has a recognizable identity and can occasionally initiate interaction itself?

If the answer is yes, the Agent Core concept is proven.

## In scope

### Agent definition

A reusable configuration object should define an agent's identity and behavioral policy.

The versioned JSON schema and two complete definitions are specified in [Backend Implementation](12-backend-implementation-spec.md#agent-definition). Identity is first-class data, separate from mutable session state. YAML is not used.

### Persistent session runtime

A running session should contain:

```text
Session
├── Agent Runtime
├── Interaction Controller
├── Conversation State
├── Session Mailbox
├── Audio / Realtime Pipeline
└── Provider Adapters
```

### Text conversation

The user can send text messages and receive agent responses.

### Voice conversation

The user can enter a call-like mode where microphone input and agent output coexist in real time. The canonical MVP path is streaming STT → Interaction Controller → text Agent Runtime / LLM → speech segmentation → streaming TTS. The microphone remains active while playback runs; batch-only providers follow a degraded capability policy, never an alternating record/play UX.

### Barge-in / interruption

While the agent is speaking, user speech continues to be processed.

The system should be able to:

- stop agent audio quickly;
- cancel or supersede current generation when useful;
- preserve what the user actually heard;
- process the interrupting utterance;
- continue naturally.

### Semantic interruption handling

Not every detected sound should stop the agent.

Examples:

- "mhm" -> likely continue;
- "yeah, yeah" -> maybe continue;
- "wait" -> interrupt;
- "hang on, what do you mean?" -> interrupt;
- unrelated background speech -> likely ignore.

### Proactive interaction

The agent can respond to controller-generated events such as silence or external state changes.

### Basic memory

Use four deliberately small memory categories initially:

```text
Working state
Current live conversation and immediate runtime state

Conversation/session history
Persistent user and assistant entries

Session summary
Important information from the current conversation

Agent/user profile
Small persistent set of useful preferences or facts
```

Advanced vector memory is not required for the MVP.

### Simple web UI

The UI should look and feel like a personal messaging app.

Required screens/states:

- agent picker plus one direct active conversation;
- text chat;
- voice-call mode on the same conversation;
- microphone mute;
- end call (return to text mode, not a different session);
- visible status such as Listening / Thinking / Speaking.

A conversation-list / history-browser / inbox UI is not required. Persistence and reconnect exist for continuity of the active conversation, not as a session-management product.

## Explicitly out of scope

Do not spend MVP time on:

- native speech-to-speech/realtime model implementation;
- agent marketplace;
- visual agent builder;
- arbitrary workflow automation;
- multi-agent orchestration;
- complex browser automation;
- generalized tool ecosystems;
- plugin marketplaces;
- sophisticated RAG pipelines;
- advanced long-term memory infrastructure;
- animated avatars;
- complex administration dashboards;
- billing systems;
- extensive analytics;
- multi-tenant enterprise management.

These can come later once the interaction model is proven.

## MVP success criteria

A successful build should demonstrate all of the following:

- the same runtime can host two noticeably different agent identities;
- text mode works;
- voice mode works;
- user speech is accepted while the agent is speaking;
- meaningful interruption stops the agent quickly;
- backchannel speech does not always stop the agent;
- an idle or external event can trigger a proactive response;
- the UI remains simple and understandable;
- model and voice providers can be replaced without rewriting Agent Core.


## Implementation limits

The baseline is a modular monolith with SQLite and a React SPA. [Technology Decisions](10-technology-decisions.md) owns the complete non-goal list. Proactivity operates only during an attached active session; push notifications, SMS, email, and background mobile services are excluded. Authentication and public multi-user hosting are deferred.

**Historical MVP wording above remains the MVP acceptance record.** Observed post-MVP trusted-local owner capability ([R1](10-technology-decisions.md#decision-trusted-local-owner-capability-r1)) is a local-owner check (loopback, plus Compose published-port gateway when configured) for catalog, hub attach, uploads, and content. It is not tenant isolation, OAuth, or public multi-user hosting, and it does not rewrite this MVP exclusion.
