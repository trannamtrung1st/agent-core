---
name: realtime
description: "Maintain Agent Core SignalR/MessagePack contracts, response identity, audio sequencing, full-duplex playback and reconnect semantics."
---

# Realtime

Read relevant sections of [Protocol](../../../docs/14-api-and-realtime-protocol.md), [Realtime Voice](../../../docs/06-realtime-voice.md) and [Event Model](../../../docs/07-event-model.md). Consult [Controller](../../../docs/05-interaction-controller.md) and [Frontend](../../../docs/13-frontend-implementation-spec.md) for their behavior.

- Keep /hubs/session on SignalR + MessagePack/WebSockets. Match specified method/event names, camelCase keys, GUID/timestamp strings, Uint8Array payloads, protocol version, limits and sequences exactly; C# property casing does not establish wire compatibility. Client envelopes omit `correlationId`/`causationId`; the server stamps EventContext.
- Validate attachment leases and sequence/offset rules. Reconnect requires Attach and an authoritative session.ready snapshot; reject old callbacks and discard old audio, with no event/audio replay.
- Carry responseId through text, Speech Segments, PCM and receipts. Supersede/tombstone first, cancel workers and reject late text/audio/terminal/progress on both sides. Only permitted diagnostic stop timing may survive supersession.
- Use canonical PCM16 little-endian mono 24 kHz and specified frame/segment/sample accounting. Read exact continuity/size limits from the specs. Binary audio stays outside the normal event log/mailbox and persistence.
- Keep microphone/STT active during TTS. Bound queues, preserve resampler state and use AudioWorklets. Underflow silence does not advance consumed samples; flush acknowledgement establishes stopped output.
- Separate model completion from playback completion. Heard context uses conservative validated progress; rendered text is not evidence of hearing. Stop pending segment/TTS jobs on response cancellation without ending recognition.
- Apply [testing](../testing/SKILL.md): real JavaScript MessagePack against loopback Kestrel, stale R1 after R2, lease/reconnect rejection, sequence gaps, and worklet flush/sample assertions.
