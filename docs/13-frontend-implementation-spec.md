# Frontend Implementation Specification

## Baseline and modules

React SPA, Vite, pnpm, strict TypeScript, Zustand, @microsoft/signalr, @microsoft/signalr-protocol-msgpack, browser fetch, plain modern CSS/CSS Modules. Vitest and React Testing Library test behavior; Playwright tests synthetic end-to-end flows. No SSR, Next.js, heavy data cache or large component framework.

Future web/src modules: app (routing/composition, AppShell), features/chat, features/voice, services/api, services/realtime, services/audio (capture, encoding, VAD, queue, worklets, progress), state/sessionStore, contracts. Wire types are hand-maintained against [Protocol](14-api-and-realtime-protocol.md) with serialization fixtures; no generated types are created during this docs task. Browser services own long-lived connections/audio; React components subscribe, issue commands and render state.

Zustand contains session descriptor, attachmentId, connection state, input/output state, mode, pendingMode, entry map/order, liveResponseId, response terminal statuses, transcripts, mute and safe errors. PCM buffers, AudioContext, stream readers and HubConnection objects live in services, never reactive store state. Reducer guards attachmentId, responseId, event sequence and text offsets before appending output. Discard superseded response chunks even when they arrive after R2 started. Keep tombstones for all responses in the attached session; bounded by the session entry limit, reset only after authoritative snapshot replacement.

## Primary screens

The UI derives one HUD status label with [controller precedence](05-interaction-controller.md#state-representation): connection (Connecting, Ready, Reconnecting, Connection failed…), pending voice (Starting voice…), then input/output activity from `session.state.changed` (`inputState` / `outputState`) as User speaking, Agent speaking, Thinking, Interrupted, or Listening. Do not invent User speaking without `inputState=userSpeaking`. Visual tokens, type, spacing and Pixel Dialogue Field presentation live in [.agents/context/DESIGN.md](../.agents/context/DESIGN.md); this document owns screens and behavior. `AppShell` wraps a session rail plus conversation. The rail is a beveled Sessions window, not an inbox of bubbles.

Text chat: agent header, scrollable conversation, composer/send, voice-call button (hidden or disabled when `voiceAvailable` is false), connection/error indicator. Historical MVP opened one conversation from identity pick with no list. The shipped post-MVP catalog rail lists durable sessions. Main reply text stays escaped. Assistant Markdown/reference blocks use a sanitized renderer (no HTML injection). Partial assistant text appears incrementally, interrupted/failed entries retain a subtle status label. Drafts survive reconnect; submitting is disabled until ready. Sending another user text intentionally supersedes the active response. Enter sends; Shift+Enter inserts a newline.

Voice call: agent name, single human-readable status (Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting), transcript/history disclosure, Mute and End call. Microphone remains active while the agent speaks. Keep the call layout simple, accessible buttons with labels, keyboard support and visible focus. Error text explains the next action. An optional developer-only panel shows event timeline, latencies, IDs, transcript evidence and controller decisions without dominating the call UI.

The voice-call control is a **mode transition on the same session**, not a new conversation. Follow [voice preflight](#voice-preflight) so browser user activation is used before any async wait. End call (or Cancel while Starting voice…) sends `session.mode.set(text)`, disposes prepared tracks/AudioContext/worklets, and returns to the same conversation's text composer. History, drafts and identity remain. Mute only affects input. If voice is unavailable, show `VoiceUnavailable` and stay in text. Follow [mode-transition safety](05-interaction-controller.md#mode-transitions): the server may queue voice until a live text response ends; leaving voice while the agent is speaking supersedes that voice response.

## Voice preflight

User activation may expire before a queued `pendingMode=voice` wait finishes. The Voice click must therefore prepare audio **locally first**, then ask the server to change mode, and only then send PCM.

```text
User clicks Voice
    ↓
local audio preflight while the gesture is active
    ├── request microphone permission
    ├── create/resume AudioContext
    ├── initialize worklets
    └── DO NOT send PCM yet
    ↓
send session.mode.set(voice)
    ↓
if pending:
    show Starting voice…
    retain prepared audio resources
    offer Cancel (session.mode.set(text))
    ↓
server Mode becomes voice (streamId issued)
    ↓
start sending microphone PCM
    ↓
start STT / playback normally
```

If preflight fails (permission denied, missing AudioWorklet, suspended context that cannot resume), do not send `session.mode.set`. If the mode request fails, is cancelled, the connection drops, or `PendingVoiceTimeoutMs` elapses, release prepared resources and return to the text composer. After reconnect, `pendingMode` is null; the user must press Voice again so a new gesture can preflight.

## Connection service

Register event handlers before starting HubConnection. Enable MessagePack and WebSockets. Use reconnect delays 0/2/5/10 seconds within a 60-second UI retry budget, including initial start failures; after budget exhaustion offer explicit Retry. Each new connection invokes Attach; reconnect alone does not restore attachment. session.ready is an authoritative snapshot, not an ordinary delta. Stop all playback immediately on connection loss, discard input buffers, release any preflight audio resources, and show Reconnecting. Do not replay microphone frames or proactive timers.

Use fetch for agent/session/history endpoints and GET `/health` for the public profile label. Default same-origin relative URLs; Vite proxies /api, /hubs (WebSocket upgrade) and /health to localhost:5080 in development. Never put API keys in frontend configuration. A new attachment invalidates old callbacks by captured attachmentId; service cleanup unregisters handlers and aborts old requests. React StrictMode mount/unmount must not create duplicate microphone tracks or hub subscriptions.

## Browser support (MVP)

Primary supported demo target: **current Chromium-family desktop browsers (Chrome and Edge)**. Firefox and Safari may be exercised as best-effort until AudioWorklet, autoplay, microphone permission and SignalR/WebSocket behavior are verified. Do not write Chromium-only application logic when standard browser APIs suffice; this is a support and test-scope decision.

## Audio services

[Realtime Voice](06-realtime-voice.md) specifies DSP, queues, formats and tracking. The UI presents a continuous call over the composed text-first pipeline, not voice notes or native speech-to-speech reasoning. Run [voice preflight](#voice-preflight) from the call button's user gesture; send PCM only after the server applies voice mode. Handle denied permission, missing AudioWorklet, suspended AudioContext and device loss as actionable audio errors. Do not claim Listening until Mode is voice, the recognition stream is ready, and capture is sending. If the device rate is 44.1/48 kHz, resample continuously with preserved filter state; never assume 24 kHz device support.

The output worklet validates its active response epoch and stops at the next render quantum on flush, with a short fade where practical. It reports consumed samples independently of UI rendering. The connection service emits playback acknowledgements every 100 ms, and text mode emits response.received every 100 ms and at final render. These receipts are advisory delivery evidence, validated on the server. Queue underflow outputs silence without advancing consumed samples. Queue overflow triggers a recoverable AudioBackpressure error and stops that response.

Local VAD ducking is an optional, independently disableable UX optimization; restore gain after controller Continue/Ignore or timeout. Only authoritative supersession/stop/end/disconnect tombstones a response. Never mute microphone automatically to solve speaker echo.

## Frontend acceptance

Tests must cover stream append by textStart, duplicate delivery, late R1 data, control sequence gaps, `session.mode.set` continuity, voice preflight (no PCM until Mode=voice), pending voice start (Starting voice… / Cancel), pending-voice release on disconnect/timeout/failure, audio ordering/flush, permission denial, mute/unmute stream changes, reconnect snapshot replacement, uncertain text retry and graceful error rendering. Playwright runs against Synthetic without keys, using a fake media device and explicit scripted speech fixture for transcript content. Synthetic STT does not pretend to recognize arbitrary real microphone speech. A manual real-device pass on the primary Chromium demo target is required for echo, AudioContext/autoplay behavior and audible stop latency; these are not guaranteed by DOM tests. Firefox/Safari checks are best-effort until verified.

## Session catalog (observed)

The browser obtains a loopback owner capability, stores it in `localStorage` as `agent-core.owner-capability`, restores it across reload, and sends `X-AgentCore-Owner-Capability` on catalog/lifecycle and hub attach. Missing, revoked, or mismatched grants fail closed: the session list is emptied and an alert is shown; other owners' sessions are not listed.

The Sessions rail is `UpdatedAt` descending with cursor **Load more**, an empty state (“No sessions yet.”), and pinned agent **name · role** (never instructions, secrets, or filesystem paths). **New chat** returns to the identity picker. **Start conversation** creates a v2 session that pins the chosen agent version. Opening a created/paused session calls reopen (new runtime epoch when no live runtime) then attach; an already-attached session is opened by attach only. Archived rows cannot be activated until **Unarchive**. Irreversible v1 **Ended** rows are labeled Ended and offer no reopen, rename, archive, unarchive, activate, or durable delete. Catalog **Delete** confirms, then calls versioned `DELETE /api/v2/sessions/{id}?expectedRevision=`. Composer **End** remains v1 terminal-end.

## Attachment picker (observed)

Composer **Attach** (plus drag/drop on the form and paste on the Message field) uploads over HTTP only: `POST /api/v2/sessions/{id}/attachments` with `X-AgentCore-Owner-Capability`. Pending chips live inside the message stack (safe display name, percent while uploading, Retry/Remove); there is no separate attachment dock. Send stays disabled until the connection is ready and every queued file is `ready`; text-only send still works; attachment-only send is allowed when at least one ready id exists. `user.text` carries `attachmentIds`. Voice stages ready ids with `POST .../attachments/stage`. History chips show the safe name and authorized content via authenticated blob fetch (not bare `<img src>`). Pending unbound files never enter reasoning.

## Rich blocks (observed)

Live `agent.block.upsert` and reconnect `session.ready` history may include Markdown, attachment/artifact references, and unknown fallbacks. Markdown is tokenized to React nodes (`**`, `*`, `` ` ``, `[label](https|http)`); `javascript:` and credentialed URLs never become `href`. Unknown blocks show `fallbackText`. Attachment refs reuse authenticated blob fetch. Artifact refs render an authorized fixture label (`Artifact · {id}`) without generating artifacts. `session.ready` replaces the entry list, so reconnect replays only server-projected visible blocks. Tombstoned responses ignore late `agent.block.upsert`. Display receipts (`response.received` with `textEndExclusive` and optional `blockIds`) do not imply speech was heard.

## Post-MVP planned until verified

Workspace physical provisioning, generated artifacts, and typed tools remain later phases.
## Synthetic playback simulation

Synthetic Mode can use a browser service implementation that advances canonical sample counters with controlled time and emits the same Playback Progress/stop acknowledgements without an AudioContext, microphone or speaker. When /health reports profile=Synthetic, default the demo audio service to simulation; a developer-only toggle can opt into fake/real worklet transport testing. The simulation sends silent PCM and scripted boundaries through SignalR on controlled scenario time, while the server synthetic driver supplies matching transcripts. Scripted input thus crosses the same connection/session boundaries. Keep this simulation confined to the Synthetic/test profile; it does not replace production AudioWorklet playback. Separate Playwright media tests exercise real worklet execution with fake devices. See [Testing](16-testing-strategy.md#synthetic-full-stack-mode).
