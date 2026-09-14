# Realtime Voice and Conversation

## Target experience

Voice mode should feel like a live call, not a sequence of recorded voice notes.

Traditional pattern to avoid:

```text
record
  ↓
stop recording
  ↓
transcribe
  ↓
LLM
  ↓
synthesize
  ↓
play
  ↓
record again
```

Desired model:

```text
microphone ───────────────► system
                               │
                               │ continuously processed
                               │
agent audio ◄─────────────────┘
```

The user may start speaking while agent audio is still playing.

## Full-duplex principle

The audio input path should remain active while the output path is active.

This is necessary for natural barge-in.

At a high level:

```text
Browser microphone
      │
      ▼
Audio transport
      │
      ├──► VAD / STT / controller
      │
      ▼
Interaction decisions

Agent Runtime
      │
      ▼
TTS / realtime model
      │
      ▼
Audio transport
      │
      ▼
Browser playback
```

## Barge-in behavior

When the user meaningfully interrupts:

1. stop or quickly fade current agent playback;
2. mark the active response as interrupted/superseded;
3. cancel model/TTS work when economical;
4. retain an approximation of what was actually heard;
5. finalize/process the user's utterance;
6. generate the next agent response.

## Spoken-until tracking

The conversation state should distinguish:

- what text was generated;
- what audio was synthesized;
- what audio was actually played to the user.

This matters during interruption.

Example:

```text
Generated:
"There are three things I would recommend. First... Second... Third..."

Actually heard:
"There are three things I would recommend. First..."
```

The next model turn should preferably know the user did not hear the rest.

A practical MVP approximation is enough. Exact phoneme-level synchronization is not required.

## Latency budget mindset

Perceived latency comes from a chain:

```text
speech detection
+ partial/final transcription
+ controller decision
+ model first token
+ TTS first audio
+ network/playback buffering
```

Optimize first-byte / first-token / first-audio latency rather than only total response duration.

## Streaming

Prefer streaming at every stage that supports it:

- microphone chunks;
- partial STT;
- model text deltas;
- TTS audio chunks;
- client playback.

Do not wait for a full model paragraph before beginning speech unless needed for quality.

## Transport

For the MVP, reasonable choices include:

### WebSocket-first

Pros:

- simple application architecture;
- easy typed event stream;
- works well for text and binary audio chunks;
- straightforward for a demo.

Cons:

- media quality and jitter handling are more manual;
- not as naturally optimized for realtime audio as WebRTC.

### WebRTC media + data channel

Pros:

- designed for realtime media;
- good latency and network behavior;
- natural path toward call-like UX.

Cons:

- more infrastructure complexity;
- signaling/session setup is more involved.

For a fast MVP, either approach can work. Keep transport behind an interface so the rest of Agent Core is not tied to the first choice.

## Browser UX states

Useful states:

```text
Connecting
Listening
User speaking
Thinking
Agent speaking
Interrupted
Reconnecting
Ended
```

Do not expose unnecessary internal complexity to the user.

## Echo and accidental self-interruption

When using speakers instead of headphones, agent audio may enter the microphone.

Mitigations can include:

- browser echo cancellation;
- server-side audio correlation where needed;
- ignoring transcripts that strongly match current agent output;
- minimum user-speech confidence/duration;
- using client audio constraints.

This should be accounted for early because it directly affects the demo experience.

## Voice pipeline modes

Agent Core should support at least two conceptual modes over time.

### Composed pipeline

```text
STT -> Agent model -> TTS
```

This is flexible and provider-agnostic.

### Native realtime provider

```text
audio <-> realtime model session
```

This can reduce latency when a provider supports speech input/output natively.

Both modes should surface normalized events to the Interaction Controller and session runtime.

## Cancellation

Cancellation must flow downward.

```text
user interrupts
      │
      ▼
abort active response
      │
      ├── model generation
      ├── TTS generation
      └── client playback queue
```

An `AbortSignal`-style pattern is a good generic primitive.

## Voice quality vs conversation quality

For the MVP, prioritize:

- fast start;
- reliable interruption;
- understandable speech;
- stable playback;
- natural timing.

Perfect prosody is secondary.

