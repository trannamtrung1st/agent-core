# MVP Scope

## MVP question

The MVP should answer one question convincingly:

> Can a user have a natural, persistent, interruptible conversation with an AI agent that has a recognizable identity and can occasionally initiate interaction itself?

If the answer is yes, the Agent Core concept is proven.

## In scope

### Agent definition

A reusable configuration object should define an agent's identity and behavioral policy.

Example:

```yaml
agent:
  id: english_examiner

  identity:
    name: Alex
    role: IELTS speaking examiner
    personality:
      warmth: 0.4
      formality: 0.8
      patience: 0.7

  goals:
    - conduct a realistic speaking examination
    - keep the candidate talking
    - evaluate answers silently

  behavior:
    interruption_style: acknowledge_then_continue
    initiative_level: medium
    silence_threshold_ms: 5000

  conversation:
    response_length: concise
    ask_one_question_at_a_time: true

  voice:
    voice_id: default
    speaking_rate: 1.0
```

The schema can change. The important point is that identity should be first-class data rather than a single prompt string.

### Persistent session runtime

A running session should contain:

```text
Session
├── Agent Runtime
├── Interaction Controller
├── Conversation State
├── Event Bus
├── Audio / Realtime Pipeline
└── Provider Adapters
```

### Text conversation

The user can send text messages and receive agent responses.

### Voice conversation

The user can enter a call-like mode where microphone input and agent output coexist in real time.

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

Use only three conceptual layers initially:

```text
Working state
Current live conversation and immediate runtime state

Session summary
Important information from the current conversation

Agent/user profile
Small persistent set of useful preferences or facts
```

Advanced vector memory is not required for the MVP.

### Simple web UI

The UI should look and feel like a personal messaging app.

Required screens/states:

- conversation list or direct session entry;
- text chat;
- voice-call mode;
- microphone mute;
- end call;
- visible status such as Listening / Thinking / Speaking.

## Explicitly out of scope

Do not spend MVP time on:

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

