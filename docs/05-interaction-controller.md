# Interaction Controller

## Purpose

The Interaction Controller is a separate live event-processing loop that observes user activity and environment state while the Agent Runtime is operating.

It is best understood as a conversation traffic controller.

Its job is not to generate the agent's personality or content. Its job is to determine what interaction event is actually happening and how that event should affect the live conversation.

## Primary inputs

The controller may observe:

- microphone voice activity;
- partial speech transcripts;
- final speech transcripts;
- text messages;
- agent speaking state;
- agent generation state;
- output playback position;
- silence / idle timers;
- UI state;
- external application events;
- tool completion events;
- future sensor or environment signals.

## Primary decisions

For the MVP, keep the decision vocabulary small:

```text
INTERRUPT
CONTINUE
QUEUE
IGNORE
INJECT_EVENT
REQUEST_AGENT_INITIATIVE
```

### INTERRUPT

Stop or supersede current agent output and process the new user utterance.

### CONTINUE

The new signal is acknowledged internally but should not stop the current agent turn.

Typical example: "mhm" during agent speech.

### QUEUE

Preserve the event and handle it after the current turn or at the next safe boundary.

### IGNORE

Treat the signal as irrelevant noise or background activity.

### INJECT_EVENT

Convert external state into a normalized event for the Agent Runtime.

### REQUEST_AGENT_INITIATIVE

Ask the Agent Runtime whether it wants to proactively say something.

This does not mean the controller itself speaks.

## Important separation

The controller should detect and classify events, but the agent should normally decide conversational meaning and content.

Example:

```text
Controller:
"The user has been silent for 8 seconds."

Agent Runtime:
"Given my identity and context, should I say anything?"
```

Avoid hardcoding:

```text
8 seconds -> automatically say "Are you still there?"
```

That would mix interaction mechanics with identity behavior.

## Interruption flow

Example:

```text
Agent starts speaking
      │
      ▼
Controller continues receiving microphone input
      │
      ▼
User speech detected
      │
      ▼
Partial transcript available
      │
      ▼
Interruption classifier
      │
      ├── CONTINUE -> keep playing agent audio
      │
      └── INTERRUPT
             │
             ├── stop/fade audio playback
             ├── cancel or supersede model generation
             ├── capture spoken-until position
             ├── emit user interruption event
             └── allow Agent Runtime to respond
```

## Backchannel handling

The controller should distinguish conversational backchannels from genuine turn-taking where possible.

Likely continue:

- "mhm";
- "uh-huh";
- "yeah";
- short acknowledgment sounds.

Likely interrupt:

- "wait";
- "stop";
- "hold on";
- "what do you mean?";
- "no, that's not what I said";
- longer semantic utterances.

This classification should not rely only on text. Useful signals include:

- speech duration;
- volume / confidence;
- partial transcript;
- whether the agent is speaking;
- timing relative to sentence boundaries;
- recent conversation context.

## Decision strategy

Use layered decision-making for speed.

```text
1. deterministic checks
2. audio/VAD heuristics
3. transcript heuristics
4. fast model classification only when ambiguous
```

Examples of deterministic checks:

```text
if no agent output is active:
    do not classify as barge-in

if speech duration is below minimum threshold and no transcript exists:
    ignore

if explicit stop phrase is detected:
    interrupt immediately
```

A model should not be required for every tiny controller event.

## Idle and proactive behavior

The controller owns clocks and thresholds, while the Agent Runtime owns behavior.

Example event:

```json
{
  "type": "idle_timeout",
  "duration_ms": 8000,
  "session_id": "..."
}
```

The runtime can then decide:

- say nothing;
- offer help;
- repeat a question;
- change topic;
- end the session politely.

## Initiative policy

Agent definition can include a policy such as:

```yaml
initiative:
  enabled: true
  level: medium
  min_interval_ms: 30000
  triggers:
    - long_silence
    - important_environment_event
    - unfinished_task
```

The controller enforces mechanical constraints such as timing and duplicate suppression.

The runtime evaluates whether speaking is appropriate.

## Concurrency model

The controller should conceptually run independently from model generation.

It should not block while waiting for the main LLM.

An implementation may use:

- async tasks;
- actors;
- channels/queues;
- an event loop;
- lightweight threads;
- coroutines.

The exact mechanism is less important than the logical independence.

## Supersession

Every generated response should have an ID.

If a new meaningful user turn supersedes the previous response, later chunks from the old generation must be discarded.

Example:

```text
response A starts
user interrupts
response A marked superseded
response B starts
late chunk from A arrives -> discard
```

This prevents stale speech from leaking into the call.

## Controller state

A minimal state model:

```text
agent_output:
  idle | generating | speaking

user_input:
  idle | speaking | finalizing

turn_owner:
  none | user | agent

active_response_id:
  string | null

last_user_activity_at:
  timestamp

last_agent_activity_at:
  timestamp

last_proactive_event_at:
  timestamp | null
```

This is enough to implement a large portion of the MVP behavior.

