# Event Model

## Principle

Agent Core should be event-driven internally.

Inputs from text, audio, timers, external systems, and future tools should be normalized into events rather than directly calling each other through provider-specific APIs.

This keeps the runtime decoupled from the UI and from individual providers.

## Base event

A useful generic shape:

```ts
export interface BaseEvent<TType extends string, TPayload> {
  id: string;
  type: TType;
  sessionId: string;
  timestampMs: number;
  payload: TPayload;
  correlationId?: string;
  causationId?: string;
  metadata?: Record<string, unknown>;
}
```

## User events

### Text message

```json
{
  "type": "user.message",
  "payload": {
    "text": "Hello"
  }
}
```

### Speech started

```json
{
  "type": "user.speech_started",
  "payload": {
    "timestamp_ms": 18482
  }
}
```

### Partial transcript

```json
{
  "type": "user.partial_transcript",
  "payload": {
    "text": "wait I actually..."
  }
}
```

### Final transcript

```json
{
  "type": "user.final_transcript",
  "payload": {
    "text": "Wait, what was the second one?"
  }
}
```

## Agent events

### Generation started

```json
{
  "type": "agent.generation_started",
  "payload": {
    "response_id": "resp_123"
  }
}
```

### Text delta

```json
{
  "type": "agent.text_delta",
  "payload": {
    "response_id": "resp_123",
    "text": "There are "
  }
}
```

### Speech started

```json
{
  "type": "agent.speech_started",
  "payload": {
    "response_id": "resp_123"
  }
}
```

### Speech progress

```json
{
  "type": "agent.speech_progress",
  "payload": {
    "response_id": "resp_123",
    "spoken_char_index": 58
  }
}
```

### Interrupted

```json
{
  "type": "agent.interrupted",
  "payload": {
    "response_id": "resp_123",
    "reason": "user_barge_in",
    "spoken_text": "There are three things I would recommend. First..."
  }
}
```

### Completed

```json
{
  "type": "agent.completed",
  "payload": {
    "response_id": "resp_123"
  }
}
```

## Controller events

### Interruption decision

```json
{
  "type": "controller.interruption_decision",
  "payload": {
    "decision": "interrupt",
    "confidence": 0.93
  }
}
```

### Idle timeout

```json
{
  "type": "controller.idle_timeout",
  "payload": {
    "duration_ms": 8000
  }
}
```

### Initiative request

```json
{
  "type": "controller.initiative_requested",
  "payload": {
    "reason": "long_silence"
  }
}
```

## Environment events

External systems should enter the runtime through the same normalized mechanism.

Example:

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

The controller or application layer can decide whether this event should be surfaced immediately to the Agent Runtime.

## UI / transport events

Examples:

```text
session.connected
session.disconnected
session.reconnected
client.mic_muted
client.mic_unmuted
client.call_ended
```

These events can be useful to the runtime without exposing transport implementation details.

## Event causality

`correlationId` and `causationId` become useful quickly.

Example:

```text
user.final_transcript event A
      ↓ causes
controller.interruption_decision event B
      ↓ causes
agent.interrupted event C
      ↓ causes
agent.generation_started event D
```

These IDs are valuable for debugging race conditions and latency.

## Event ordering

Within one session, assign a monotonically increasing sequence number if practical.

```ts
interface SequencedEvent {
  sequence: number;
}
```

This makes logs easier to reason about when network and async tasks complete out of order.

## Event log for debugging

During MVP development, keep a developer-only event timeline.

Example:

```text
12:01:04.100 user.speech_started
12:01:04.240 user.partial_transcript "wait"
12:01:04.248 controller.interruption_decision interrupt
12:01:04.255 agent.interrupted resp_123
12:01:04.310 user.final_transcript "wait, what was the second one?"
12:01:04.330 agent.generation_started resp_124
12:01:04.610 agent.text_delta "The second one was..."
12:01:04.720 agent.speech_started resp_124
```

This will be one of the most useful tools for improving conversational feel.

## Avoid over-distribution

An event-driven design does not imply distributed infrastructure.

For the MVP:

- keep the event bus in-process;
- persist only what is needed;
- log important events;
- add external queues only when scaling requirements justify them.

