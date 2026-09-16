# HTTP API and Realtime Protocol

This document owns wire protocol version 1. Contracts contains closed typed DTOs; Api validates/maps them. C# property names may be PascalCase, but JSON uses camelCase and MessagePack uses explicit string keys in camelCase. Configure these independently: System.Text.Json settings do not configure MessagePack. Serialize GUIDs as lowercase dashed strings and timestamps as UTC ISO-8601 strings, avoiding cross-language Guid/date extensions. Integers are nonnegative and <= JavaScript Number.MAX_SAFE_INTEGER; rotate/end before overflow. Unknown type/version is rejected; do not deserialize arbitrary CLR type names.

## HTTP

REST handles creation, discovery, history, state and terminal ending. Live user text, audio and playback use SignalR. All examples are JSON, including the JSON-equivalent view of MessagePack messages. Nullable fields are serialized explicitly as null, except optional HTTP validation errors/extensions. Content-Type is application/json. IDs below are illustrative; creation generates fresh IDs.

| Method | Request | Success | Errors |
| --- | --- | --- | --- |
| GET /api/v1/agents | No body | 200 list below | 503 definitions unavailable |
| GET /api/v1/agents/{agentId} | Optional `version` positive integer; latest if absent | 200 public descriptor | 404 unknown ID/version |
| POST /api/v1/sessions | Create body below | 201 session view; Location=/api/v1/sessions/{id} | 400 validation; 404 agent; 409 VoiceUnavailable when initial mode is voice but voice is not available; 503 storage |
| GET /api/v1/sessions/{sessionId} | No body | 200 session view | 404 unknown |
| GET /api/v1/sessions/{sessionId}/messages | `after=0&limit=50`, limit 1..100 | 200 history page | 400 invalid cursor; 404 unknown |
| DELETE /api/v1/sessions/{sessionId} | No body | 204 after terminal save; repeated known end also 204 | 404 unknown; 503 durable save failed |
| GET /health | No body | 200 health below | 503 if store/definition startup failed |

Agent list (GET detail returns one item with the same fields):

```json
{"agents":[{"id":"compliance","version":1,"name":"Jordan","role":"Compliance reviewer","description":"Review simulated policy questions without changing live records.","voiceAvailable":true},{"id":"customer-support","version":1,"name":"Sam","role":"Customer support representative","description":"Resolve a simulated support issue.","voiceAvailable":true},{"id":"examiner","version":1,"name":"Alex","role":"Speaking examiner","description":"Practice a speaking examination.","voiceAvailable":true}]}
```

Expose only descriptors, not systemInstructions, full definitions, provider configuration or credentials. `voiceAvailable` is the public formula in [Interfaces](04-backend-interfaces.md#independent-speech-ports): Voice.Enabled and resolved STT/TTS adapters whose effective capabilities support the canonical format. Synthetic voice counts as available. Text-only definitions return false without failing backend startup.

POST request:

```json
{"agentId":"examiner","agentVersion":1,"mode":"text"}
```

`agentId` required; `agentVersion` optional, resolved to the latest and then pinned. `mode` is optional and defaults to `text`; allowed values `text|voice`. Mode is the session's **current** interaction mode, not a second conversation. Voice on create is rejected with `VoiceUnavailable` when `voiceAvailable` is false. No automatic opening greeting: ready waits for user input or an eligible initiative trigger. The voice-call button issues `session.mode.set` on the **same** session after attach, after local [audio preflight](13-frontend-implementation-spec.md#voice-preflight); it must not create a fresh unrelated voice session or drop text history. Do not send `audio.input` until Mode is voice.

201 / GET session response:

```json
{"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","agentId":"examiner","agentVersion":1,"mode":"text","pendingMode":null,"status":"created","createdAt":"2026-09-15T00:00:00.000Z","updatedAt":"2026-09-15T00:00:00.000Z","lastEntrySequence":0,"activeResponseId":null,"protocolVersion":1}
```

status is `created|attached|paused|ending|ended`; `pendingMode` is `text|voice` while a mode change is queued, otherwise null. activeResponseId is required and nullable. GET reads the current runtime projection when active, durable snapshot otherwise. Session summary is server-only and never appears on this view. Initial mode is honored only after attach; creation performs no provider calls.

History response (all listed entry fields required; nullable responseId/sourceEventId):

```json
{"items":[{"entryId":"019944af-0000-7000-8000-000000000010","sequence":1,"sourceEventId":"019944af-0000-7000-8000-000000000010","role":"user","text":"Please explain.","responseId":null,"status":"completed","deliveryMode":"text","heardTextEndExclusive":15,"receivedTextEndExclusive":15,"createdAt":"2026-09-15T00:00:01.000Z"},{"entryId":"019944af-0000-7000-8000-000000000011","sequence":2,"sourceEventId":null,"role":"assistant","text":"There are three points.","responseId":"019944af-0000-7000-8000-000000000012","status":"interrupted","deliveryMode":"voice","heardTextEndExclusive":9,"receivedTextEndExclusive":23,"createdAt":"2026-09-15T00:00:02.000Z"}],"nextAfter":2,"hasMore":false}
```

Roles `user|assistant`; statuses `completed|interrupted|failed|streaming`; deliveryMode `text|voice` is the mode in which that entry was produced (prompt builder: text assistant entries use received prefix; voice assistant entries use heard prefix). A pending local user entry is reconciled by sourceEventId (the original user.text eventId or voice utteranceId); assistant entries are reconciled by responseId. Public assistant text is limited to ReceivedTextEndExclusive; backend-only generated tails are never exposed by reconnect. Public heardTextEndExclusive is clamped to the projected text length; the internal heard offset remains available for model context even if a last text receipt was lost. History is ordered by stable entry sequence; a streaming entry is updated in place, so reconnect fetches a fresh snapshot, not merely entries after its old sequence. `after` is an entry sequence, not an event cursor. No persisted PCM or speculative partial user transcript appears here.

Health: `{"status":"healthy","profile":"Synthetic","protocolVersion":1}`. Check local startup/SQLite access, not remote LLM billing or an active model call. Use built-in ASP.NET Core OpenAPI at /openapi/v1.json in development; no admin UI required.

Validation/provider errors use Problem Details on HTTP:

```json
{"type":"about:blank","title":"Invalid session request","status":400,"code":"ValidationError","detail":"mode must be text or voice.","traceId":"demo-trace","errors":{"mode":["Unsupported value."]}}
```

errors is optional and contains only safe field-level messages. Never include provider response bodies, stack traces or keys.

## Hub and encoding

Endpoint `/hubs/session`; require MessagePack Hub Protocol v1 application messages (distinct from SignalR's own protocol version). One connection attaches one session and a session has one owning connection. The hub validates, associates the connection lease, and forwards to application services. Runtime logic stays outside hub methods. Audio DTOs use `byte[]`/Uint8Array encoded as MessagePack binary, never base64 or a generic event payload.

```csharp
// AgentCore.Contracts representative boundary signatures, not runtime ports.
public sealed record ClientEnvelope(int ProtocolVersion, string SessionId,
    string EventId, long Sequence, string? Timestamp, string? ResponseId,
    string? AttachmentId, string Type);
public sealed record InputAudioDto(int ProtocolVersion, string SessionId,
    string AttachmentId, string StreamId, long FrameSequence,
    long SampleOffset, byte[] Data);
public sealed record OutputAudioDto(int ProtocolVersion, string SessionId,
    string AttachmentId, string ResponseId, long FrameSequence,
    long SampleOffset, bool IsFinal, byte[] Data);
```

Envelope record describes common metadata; each command has a statically typed payload DTO following the table, not an arbitrary dictionary. Implement SignalR hub `Attach(AttachCommand)`, `SendText(UserTextCommand)`, `SetMode(SetModeCommand)`, `SpeechStarted(SpeechStartedCommand)`, `SpeechEnded(SpeechEndedCommand)`, `SendAudio(InputAudioDto)`, `PlaybackStarted(PlaybackCommand)`, `PlaybackProgress(PlaybackCommand)`, `PlaybackCompleted(PlaybackCommand)`, `PlaybackStopped(PlaybackCommand)`, `ResponseReceived(ResponseReceiptCommand)`, `SetMuted(MuteCommand)`, `EndSession(EndCommand)`. All return `Task<CommandAck>` except SendAudio returns Task after local ingress admission. Browser uses ordered `connection.send("SendAudio", dto)` calls, not a round-trip `invoke` for every frame; audio rejection is reported through SessionEvent error. CommandAck is `{eventId,accepted,error}` with error nullable; a non-null error uses the category/code/message/fatal/retryAfterMs payload from the error table. Accepted means admitted/deduplicated, not durably completed.

The client registers `SessionEvent` for typed server envelope maps and `AudioOutput` for OutputAudioDto. Hub sends one event argument, not positional payload fields. Include serialization fixtures to verify exact keys/casing/binary bytes. Use WebSocket transport and MaximumParallelInvocationsPerClient=4 so an audio method cannot monopolize control handling. Audio ingress uses immediate bounded TryWrite and reports overflow; it never waits for STT/network work. Control sequencing still comes from the browser connection service and mailbox admission. Configure maximum receive message size 32 KiB and reject text >8,000 UTF-16 code units or frame >1,920 bytes at application validation.

## Envelope requirements and ordering

| Field | Client control | Server control |
| --- | --- | --- |
| protocolVersion | Required integer 1, including attach | Required 1 |
| sessionId | Required UUID | Required UUID |
| eventId | Required unique client UUID, reused for exact retry | Required new server UUID |
| sequence | Attach=0; subsequent controls start at 1 per attachment | Starts at 1 for ready, increases per attachment at actual send |
| timestamp | Optional UTC ISO string; advisory only | Required server TimeProvider UTC ISO string |
| correlationId | **Omitted.** If present, ProtocolError; never copied into EventContext | Required; server-stamped. Root may use the accepted client eventId as a correlation seed |
| causationId | **Omitted.** If present, ProtocolError; never copied into EventContext | Nullable; immediate producing event |
| responseId | Required for playback/receipt, null otherwise | Required for all agent.*, playback.*, null for session/transcript/error unless response-scoped |
| attachmentId | Null on attach; required subsequently | Required after attach; protocol rejection may be null |
| type / payload | Required discriminant and typed payload | Required discriminant and typed payload |

`responseId` is the generation identity; there is no competing generationId. Transcript payloads require utteranceId. Audio deliberately omits eventId/correlation/timestamp/global sequence to keep high-frequency frames separate; required fields are exactly the audio DTO fields above. Correlate audio via responseId on output or streamId on input. Frame sequences/sample offsets provide audio ordering. Audio output identity is verified against the response lifecycle control stream, never inferred from arrival time.

Control sequences increase in send order, not when work was produced, so priority stop can bypass queued audio/text without producing descending counters. Sender drops stale response data before assigning its control sequence. Only `playback.stop`/`playback.gain`/error/lifecycle controls may bypass queued deltas; `agent.text.completed` and successful `agent.response.completed` are ordering fences and wait for all preceding text/audio sends for that response. Client ignores duplicate server control sequences; a gap forces resynchronization (stop output, close the connection, then reconnect and reattach). Audio has independent counters. Buffer unknown-response audio at most 60 ms until `agent.response.started` arrives, otherwise reject; never play unknown identity. Control sender must send `agent.response.started` before submitting that response's first audio.

Client serializes control calls in its connection service; API accepts increasing sequence values, tolerates gaps caused by a rejected command, rejects backwards values except exact known eventId retry. Store dedupe outcomes for the attachment (maximum 1,024 controls; reject older unknown retries as StaleCommand). Repeated eventId with different payload is ProtocolError. A new attachment resets command sequencing. Text eventId is also stored as ConversationEntry sourceEventId; a retry after reconnect is deduplicated by that ID with fresh attachment/sequence metadata. Keep at most one unacknowledged text submission in the browser; audio and speech boundaries are never replayed. Non-text commands are not retried across attachment changes.

Example attach command (sent to Attach as one MessagePack map):

```json
{"protocolVersion":1,"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","eventId":"019944af-0000-7000-8000-000000000020","sequence":0,"timestamp":"2026-09-15T00:00:00.100Z","responseId":null,"attachmentId":null,"type":"session.attach","payload":{"lastServerSequence":null}}
```

## Client events

Every control uses metadata above plus payload below. Empty payload is `{}`.

| Logical type | Hub method | Exact payload fields |
| --- | --- | --- |
| session.attach | Attach | lastServerSequence: integer\|null (diagnostic) |
| session.mode.set | SetMode | mode: text\|voice |
| user.text | SendText | text: string <=8,000 (blank allowed only with bindable `attachmentIds`); attachmentIds?: UUID[] max 10 |
| user.speech.started | SpeechStarted | streamId: UUID, utteranceId: UUID, sampleOffset: integer, activityScore: number 0..1 |
| user.speech.ended | SpeechEnded | streamId, utteranceId, sampleOffset: integer, durationMs: nonnegative number |
| audio.input | SendAudio | Dedicated InputAudioDto, no envelope |
| playback.started | PlaybackStarted | consumedSamples: 0, textEndExclusive: integer |
| playback.progress | PlaybackProgress | consumedSamples: integer, textEndExclusive: integer |
| playback.completed | PlaybackCompleted | consumedSamples: total, textEndExclusive: integer |
| playback.stopped | PlaybackStopped | consumedSamples: final actual position, textEndExclusive: integer |
| response.received | ResponseReceived | textEndExclusive: highest rendered UTF-16 **display** offset; blockIds?: string[] |
| session.mute | SetMuted | muted: boolean |
| session.end | EndSession | reason: userEnded |

Playback textEndExclusive is the speech-coordinate offset of synthesized speech (display text when `reply.speech` is absent). Display receipts stay on `reply.text` and must not be copied onto heard. Receipt/progress values must be <= emitted display/speech/samples and monotonic. Ignore playback.started/progress/completed/stopped and text receipts after response supersession or disconnect for state/history/context. A stopped acknowledgement may record diagnostic stop latency only. Completed successful responses may still accept a final text receipt to acknowledge already delivered text; this cannot authorize more output. When muting, client sends ended boundary before mute; unmute produces session.ready-like stream data via session.state.changed with a fresh streamId before accepting PCM. Stream IDs are server-issued on ready/unmute/recovery and after a max-utterance restart.

Speech boundaries include sampleOffset in the same stream coordinate as audio. Audio ingress defers a boundary until preceding samples are admitted; application receives speech observation promptly but recognizer flush waits for the ordered marker. This avoids separate hub invocation ordering corrupting STT buffering. One browser audio producer sends ordered frames; duplicates are ignored, gaps fail the stream.

## Server events

| Type | Required payload fields |
| --- | --- |
| session.ready | mode, pendingMode: text\|voice\|null, status, agent descriptor, streamId: UUID\|null, audioFormat, capabilities, lastEntrySequence, history: entry array (latest 50, public projection), activeResponseId: null |
| transcript.partial | utteranceId, revision: integer, text |
| transcript.final | utteranceId, text, entryId: UUID\|null, entrySequence: integer\|null |
| agent.response.started | entryId: UUID, entrySequence: integer, trigger: userTurn\|longSilence\|environmentUpdate\|unfinishedInteraction |
| agent.text.delta | text, textStart: UTF-16 offset of **display** text |
| agent.text.completed | textLength: integer (display) |
| agent.block.upsert | blockId, kind: markdown\|attachment\|artifact\|unknown, text, fallbackText, attachmentId?, artifactId? |
| audio.output | Dedicated OutputAudioDto |
| playback.gain | gain: 0.2\|1.0, rampMs: 20, candidateId: UUID |
| playback.stop | reason: interrupted\|disconnected\|ended\|providerFailed\|audioFailed\|modeChange |
| agent.response.interrupted | reason: userBargeIn\|newText\|disconnected\|ended\|modeChange, heardTextEndExclusive: integer |
| agent.response.completed | status: completed\|failed, heardTextEndExclusive: integer |
| session.state.changed | status, mode: text\|voice, pendingMode: text\|voice\|null, inputState, outputState, muted: boolean, streamId: UUID\|null |
| error | category, code, message, fatal: boolean, retryAfterMs: integer\|null |

For transcript.final, entryId/entrySequence are null for ignored backchannels/noise and present for an accepted user turn; SourceEventId for that entry is utteranceId. Its persistence acknowledgement gates generation. `agent.response.started` identifies the new assistant entry even before its first periodic checkpoint.

session.ready audioFormat is `{encoding:"pcm_s16le",sampleRateHz:24000,channels:1,frameDurationMs:20}` in voice, null in text. capabilities is `{stt:{streamingAudio,partialTranscripts,speechBoundaryEvents,cancellation},tts:{streamingAudio,timingMarks,cancellation,voiceSelection,speakingRate,supportedFormats},bargeInPolicy:"semantic"|"speechAndFinal"|"speechActivity"|"none"}` and contains **effective** adapter capabilities only. Speech booleans are false and supportedFormats is empty in text mode. In voice mode supportedFormats lists canonical encoding/sampleRateHz/channels records that the adapter can produce. ready has no live response: attaching never replays or continues interrupted output. Ready history items use the same public fields as GET `/messages` (including `deliveryMode` and both text-end offsets); it is not a persistence snapshot and must not include session summary.

Example server control (other events use identical metadata with their table payload):

```json
{"protocolVersion":1,"sessionId":"873f07d1-e264-4c81-a31b-7e59e940b842","attachmentId":"019944af-0000-7000-8000-000000000001","eventId":"019944af-0000-7000-8000-000000000014","sequence":4,"timestamp":"2026-09-15T00:00:02.100Z","correlationId":"019944af-0000-7000-8000-000000000010","causationId":"019944af-0000-7000-8000-000000000013","responseId":"019944af-0000-7000-8000-000000000012","type":"agent.text.delta","payload":{"text":"Hello","textStart":0}}
```

Terminal interruption uses `agent.response.interrupted`, never a later `agent.response.completed` event. Failed response uses error then `agent.response.completed(status=failed)`; it does not also emit interrupted. On any terminal failed/interrupted event client tombstones the response and flushes playback. On success client retains text and rejects additional output. `agent.text.completed` does not imply audio has finished.

## Connection lifecycle

Session IDs act as local/demo bearer capabilities: use cryptographically random UUIDv4 session IDs (122 random bits), never sequential IDs. Production event/response IDs use UUIDv7. `IIdGenerator.NewSessionId()` and `NewId()` are specified in the C# port so deterministic tests cover both. Do not log session URLs publicly. This is not a full authentication system. **Planned until verified:** post-MVP trusted-local owner capability replaces SessionId-as-credential for catalog, attach, upload, bind, artifacts, and content ([R1](10-technology-decisions.md#decision-trusted-local-owner-capability-r1)); historical MVP still uses session IDs as demo bearers until that phase is verified.

Attach atomically acquires a fresh attachmentId lease, activating an in-memory runtime. If `MaxActiveSessions` would be exceeded, reject with recoverable `SessionCapacityExceeded` and `retryAfterMs` defaulting to 5000; the durable session remains and may be retried. An exact repeated Attach eventId on the same connection returns the original acknowledgement/ready snapshot and lease; it never creates a second owner. A new Attach on an already attached connection is rejected until it disconnects. A still-attached session rejects a second connection with SessionInUse; no silent takeover. Disconnect invalidates the old lease, supersedes output, closes STT, clears `pendingMode` (do not carry `PendingMode=Voice` into reconnect), and pauses initiative. If the server hasn't detected disconnect yet, reconnect retries SessionInUse with bounded backoff rather than stealing the session. Configure SignalR keepalive 10 seconds/client timeout 30 seconds. Grace retention is 60 seconds after disconnect is observed.

Attach loads/reconciles the durable snapshot if necessary and returns ready with current history/status/**mode**/pendingMode; it does not replay event/audio buffers. lastServerSequence helps diagnose a gap only. Before attach the browser discards all playback buffers and marks old active responses interrupted. Once ready arrives it replaces its history projection by entryId and may fetch older pages. A recently modified interrupted entry must be refreshed even if its entry sequence predates the cursor. User unsent text remains in the composer; retry uncertain accepted text only with original eventId. An ended session cannot be resumed; create a new one explicitly. `session.mode.set` follows [controller mode rules](05-interaction-controller.md#mode-transitions). Re-entering voice after reconnect requires a new Voice click (preflight + `session.mode.set`); ready `pendingMode` is null after disconnect/crash recovery even if a voice request was queued.

ProtocolVersion !=1 yields ProtocolVersionMismatch before attachment or audio acceptance, reports supportedVersions=[1] in the safe error extension, then closes the connection. No automatic version downgrade. Clean End/DELETE first disables input and supersedes output, then persists status Ended, sends state changed, releases lease. If persistence fails, remain Ending with retryable SessionPersistenceUnavailable; do not falsely report successful durable end.

```mermaid
sequenceDiagram
    participant B as Browser
    participant H as Hub / SessionManager
    participant S as Session Runtime
    participant D as SQLite
    B--xH: Connection lost
    B->>B: Stop playback, discard PCM, show Reconnecting
    H->>S: Detach old lease
    S->>S: Supersede R1, cancel providers, clear pendingMode, pause initiative
    S->>D: Save interrupted history + paused snapshot
    B->>H: Reconnect then Attach(sessionId, cursor)
    H->>S: Acquire new lease, restore if evicted
    S->>D: Load checkpoint if needed
    S-->>B: session.ready(new attachment, snapshot, new streamId)
    B->>B: Replace history; no old audio replay
```

## Error policy

Categories: Provider, Session, Protocol, Audio, Validation, Connection, Transport. User-safe code/message/fatal are required. Authentication, RateLimited, Timeout, Cancelled, InvalidRequest, Unavailable, UnsupportedCapability and Unknown are provider codes normalized by the adapter. Session codes include SessionBusy, SessionInUse, SessionPersistenceUnavailable, SessionCapacityExceeded, VoiceUnavailable and StaleCommand. Transport codes include recoverable `AudioDiscontinuity` (sequence/offset/gap; recognition restarts on a new streamId) and recoverable `MaxUtterance` (forced end after `Voice.MaxUtteranceSeconds`, default 30; recognition restarts on a new streamId). Expected user cancellation emits interruption, not an alarming error toast. A midstream provider failure keeps the partial entry marked failed, stops TTS/playback, and permits a new user turn; never auto-replay the request. STT/TTS failure disables voice input/output for that session until explicit reconnect or a mode reset to text; the same session's text mode remains usable. Session storage corruption/ownership failure is fatal; temporary provider/connection errors are recoverable. No automatic Real→Synthetic fallback that would mislead the user.

## Post-MVP planned until verified

Phases A–H are observed on the runtime (including Docker `sandbox.run`). Phase I WorkItems are not-applicable until the future trigger in [Technology Decisions](10-technology-decisions.md#post-mvp-planned-until-verified). Full R1–R6 text is in that same section. Phase D initiative/deactivation and Phase F workspace execution view are observed above.

**Lease versus Attachment.** Wire `attachmentId` is the hub **connection lease**. User-uploaded files are HTTP `Attachment` records with `AttachmentId`. Never send attachment binaries or base64 on SignalR.

**Trusted-local owner capability.** Catalog, lifecycle, attach, upload, bind, artifact, and content routes require `X-AgentCore-Owner-Capability` (hub attach includes the same token). `SessionId` is not a credential. Loopback `POST /api/v1/local/owner-capability`; browser restore from `localStorage`; hashed grant survives API restart. Fail closed without leaking other sessions.

**Additive lifecycle (keep v1 DELETE).** Versioned routes:

| Method | Meaning |
| --- | --- |
| GET /api/v2/sessions | Catalog, `UpdatedAt` descending, stable cursor pagination; includes labeled Ended rows |
| POST /api/v2/sessions | Create with chosen `agentId`/`agentVersion`; pin; workspace-ownership record |
| POST /api/v2/sessions/{id}/rename \| archive \| unarchive | Persist catalog mutation in the runtime revision stream |
| POST /api/v2/sessions/{id}/reopen | New runtime epoch if no live runtime; not the same as attaching an already-active session |
| POST /api/v2/sessions/{id}/deactivate | Runtime deactivation: cancel live output, persist Paused, increment RuntimeEpoch; not archive and not v1 end; idempotent |
| GET /api/v2/sessions/{id}/knowledge/{identity} | Approved knowledge retrieval with citation metadata; 403 if the pinned role does not allow `knowledge.retrieve` or the identity |
| GET /api/v2/sessions/{id}/workspace | Execution-view listing (`prefix` query, default `/`); owner capability |
| GET /api/v2/sessions/{id}/workspace/content?path= | Read logical path (`/agent`, `/attachments`, `/workspace`); host paths never returned |
| PUT /api/v2/sessions/{id}/workspace/content?path= | Write under `/workspace/working|artifacts|state` only; 250 MiB session cap; 403 for RO overlays, secrets, traversal, symlinks |
| POST /api/v2/sessions/{id}/attachments/{attachmentId}/materialize | Explicit working copy plus Artifact metadata; originals unchanged; SHA-256 preserved; deterministic `name-2` collisions |
| GET /api/v2/sessions/{id}/artifacts | List session-owned artifacts; owner capability |
| GET /api/v2/sessions/{id}/artifacts/{artifactId} | Artifact metadata; cross-session 404 |
| GET /api/v2/sessions/{id}/artifacts/{artifactId}/content | Authorized download; SessionId is not a credential |
| DELETE /api/v2/sessions/{id} | Versioned durable deletion of session-owned data including workspace files and artifact blobs |
| DELETE /api/v1/sessions/{id} | Unchanged terminal-end |

Ended rows: reopen/rename/archive/unarchive/versioned-delete fail closed or no-op without resurrecting a runtime.

**Rich envelope (observed).** Parent `ResponseId` carries `reply.text`, optional `reply.speech`, Markdown, attachment/artifact reference blocks, independent display and speech receipts. Unknown blocks fallback. Artifact refs in C use fixtures (`fixture-artifact-1`). Reconnect history is the received display prefix plus display-delivered blocks only.

**Uploads (observed store).** HTTP multipart/streaming only under `/api/v2/sessions/{id}/attachments` (POST pending, GET collection, GET metadata, GET `/content`, DELETE pending, POST `/stage` for the next speech turn). Unread storage of types outside the supported processor set follows the pinned role `environment.attachments.allowUnreadUnsupportedTypes` (shipped fixtures reject-at-upload). Client headers cannot widen that policy. `user.text` may include `attachmentIds` (UUIDs, max 10); empty text is allowed only with at least one bindable id. Bind runs in the runtime persist callback after the user entry is durable. Processors then extract off the mailbox; `session.state.changed` may emit `outputState=processingAttachments` while that work runs. Typed tools may emit `outputState=runningTools` while bounded tool steps run. Caps in the [resource table](10-technology-decisions.md#planned-resource-limits). OCR and Office readers remain out of scope.
