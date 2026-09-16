# Product Vision

## What Agent Core is

Agent Core is a reusable agent harness for running an AI identity as a persistent conversational participant.

The identity might be:

- a customer service representative;
- an examiner;
- an interviewer;
- a tutor;
- a receptionist;
- a friend or companion;
- another role defined by product-specific behavior.

The identity is not only a system prompt. It includes behavioral policy, conversational style, initiative rules, interruption behavior, voice configuration, and runtime state.

A useful mental model is:

> Experienced identity = reusable configuration + behavioral policy + runtime state.

The static Agent Definition stores only configuration and policy. Mutable runtime state belongs to the session.

## Core experience

The user interacts with an agent in a simple personal-chat interface.

Two interaction modes are required on **one** personal conversation:

- text conversation;
- voice conversation.

The user can enter and leave voice without starting a second unrelated session or losing history. Voice should feel closer to a natural call than to a traditional record-submit-wait pipeline. The user should be able to speak while the agent is speaking. The system then decides whether that speech should interrupt the agent, be ignored, be queued, or be treated as a non-disruptive acknowledgement.

The MVP realizes this experience through a composed, text-first pipeline with independently replaceable Speech Recognizer, Language Model and Speech Synthesizer. Continuous input and output preserve conversational presence; native speech-to-speech reasoning is not required. [System Architecture](03-system-architecture.md) defines the implementation.

## The two-loop model

The MVP has two logical activities: conversational reasoning and live interaction observation. They share one Session Runtime state owner; model/provider work runs asynchronously around its mailbox.

### Agent Runtime

The Agent Runtime is the identity itself. It:

- receives meaningful interaction events;
- maintains conversational context;
- generates responses;
- follows the configured identity and policy;
- decides whether and how to respond;
- may initiate an interaction when appropriate.

### Interaction Controller

The Interaction Controller is a logically separate live policy component, evaluated in the Session Runtime mailbox loop while provider operations run asynchronously. It does not require a dedicated thread or independently mutate shared state. It observes the live interaction environment and provides normalized information to the Agent Runtime.

It may receive:

- user text;
- microphone activity;
- partial transcripts;
- completed transcripts;
- whether the agent is currently speaking;
- timers and silence duration;
- application state;
- external events;
- metadata;
- future tool or system events.

It is responsible for conversational traffic management, especially:

- interruption detection;
- turn-taking;
- background/noise filtering;
- queuing;
- idle detection;
- proactive-interaction triggers.

A useful separation is:

> Interaction Controller: what happened?
>
> Agent Runtime: what should I do about it?

## Proactive interaction

The system is not limited to a strict user -> agent -> user -> agent request/response loop.

It should also support:

```text
environment -> interaction controller -> agent runtime -> user
```

Examples:

- an examiner notices that the user has been silent and offers a hint;
- a support agent receives an order-status update and informs the user;
- an agent returns to an unfinished topic after an appropriate delay;
- a receptionist reacts to a new appointment-state event.

Proactivity must be restrained. The agent should know when not to speak.

## Product principle

The first MVP should optimize for conversational presence, not feature count.

A mediocre reasoning model with excellent latency, interruption handling, and timing can feel more natural than a stronger model with awkward turn-taking.

For this reason, implementation priority should be:

1. latency;
2. interruption;
3. turn-taking;
4. identity consistency;
5. proactive behavior;
6. conversation continuity.

## Post-MVP planned until verified

Accepted product behavior beyond historical MVP: multiple persistent chats with agent selection; file/image attachments; rich replies; versioned role environments (Support, Compliance, examiner); durable isolated session workspaces; session-owned artifacts with explicit attachment materialization; bounded typed tools for Support/Compliance; Docker `sandbox.run` behind the same capability boundary (not host process/shell). Phase I WorkItems remain conditional. Details: [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified) and [Implementation Plan](18-implementation-plan.md#post-mvp-phases-planned-until-verified).

