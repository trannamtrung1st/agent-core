# Frontend Implementation Specification

## Baseline and modules

React SPA, Vite, pnpm, strict TypeScript, Zustand, @microsoft/signalr, @microsoft/signalr-protocol-msgpack, browser fetch, Ant Design v6 (product components import AntD directly), and minimal app-specific CSS. Vitest and React Testing Library test behavior; Playwright tests synthetic end-to-end flows. No SSR, Next.js, heavy data cache, Ant Design Pro/ProComponents/X, or a second component/CSS framework.

Shipped web modules include `app` (routing/composition, `AppShell`, session path helpers), `features/chat`, services (`api`, `realtime`, `attachments`, `catalog`, `audio`), and `state/sessionStore`. Wire types are hand-maintained against [Protocol](14-api-and-realtime-protocol.md) with serialization fixtures; no generated types are created during this docs task. Browser services own long-lived connections/audio; React components subscribe, issue commands and render state.

Future modules may add `features/voice` composition splits and `contracts` packaging without changing the routing model below.

Zustand contains session descriptor, attachmentId, connection state, input/output state, mode, pendingMode, entry map/order, liveResponseId, response terminal statuses, transcripts, mute and safe errors. PCM buffers, AudioContext, stream readers and HubConnection objects live in services, never reactive store state. Reducer guards attachmentId, responseId, event sequence and text offsets before appending output. Discard superseded response chunks even when they arrive after R2 started. Keep tombstones for all responses in the attached session; bounded by the session entry limit, reset only after authoritative snapshot replacement.

## Primary screens

The UI derives one status label with [controller precedence](05-interaction-controller.md#state-representation): connection (Connecting, Ready, Reconnecting…, Connection failed…), pending voice (Starting voice…), then input/output activity from `session.state.changed` (`inputState` / `outputState`) as User speaking, Speaking…, Thinking…, Generating response…, Reading attachments…, Running tools…, Interrupted, or Listening…. Do not invent User speaking without `inputState=userSpeaking`. Conversation activity is shown in the thread, not as a HUD of Tags. Connection failure uses an `Alert` with Retry; reconnecting is a quiet inline status. Ant Design owns the verified MVP generic UI system ([Technology Decisions](10-technology-decisions.md#decision-ant-design-v6-as-mvp-generic-ui-system)). ChatGPT is a layout/hierarchy reference only. [.agents/context/DESIGN.md](../.agents/context/DESIGN.md) provides only lightweight project UI guidance. If UI/design guidance conflicts with behavioral specifications in `/docs`, `/docs` wins. This document owns screens and behavior. `AppShell` wraps a session rail plus conversation. The rail lists durable sessions with catalog commands; it is not an inbox of bubbles. On viewports 768px and wider the rail is a persistent sider (240px between 768px and 1199px, 280px at 1200px and above). Below 768px the list opens from a Drawer. Product components import Ant Design primitives directly; `web/src/app.css` is limited to shell sizing, overflow, product layout, preview sizing, and accessibility.

Text chat: compact agent header, scrollable conversation (user messages as right-aligned bubbles; assistant messages as open sanitized Markdown), composer/send, voice-call button (hidden or disabled when `voiceAvailable` is false), connection/error indicator. Before attach, read `voiceAvailable` from GET `/api/v1/agents` for the selected identity; after attach, read it from the `session.ready` agent descriptor (same formula as the catalog). Historical MVP opened one conversation from identity pick with no list. The shipped post-MVP catalog rail lists durable sessions. New chat shows agent Identity selection and the composer; the first send, attach, or Voice click creates the v2 session and pins the chosen agent version. Partial assistant text appears incrementally as Markdown; interrupted/failed entries retain a subtle status label. Drafts survive reconnect; submitting is disabled until ready. First-party Send stays available during an active text response and always emits `user.text` `behavior=queue` (including when idle); it is not labeled Interrupt. Repeated Send queues multiple messages after each admitted send. Stop appears while a response is live, sends `agent.response.cancel` for the currently rendered `responseId`, creates no user entry, does not end the session, and does not discard queued history. Stale Stop completion must not corrupt a newer live response. Enter sends; Shift+Enter inserts a newline. Send, Stop, and Resume are keyboard-operable; focus returns to Message after Stop or a successful Resume.

Voice call: agent name, single human-readable status (Listening…, User speaking, Thinking…, Speaking…, Interrupted, Reconnecting…), conversation history, Mute and End. Microphone remains active while the agent speaks. Keep the call layout simple, accessible buttons with labels, keyboard support and visible focus. Error text explains the next action. An optional developer-only panel shows event timeline, latencies, IDs, transcript evidence and controller decisions without dominating the call UI.

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

The Sessions rail is `UpdatedAt` descending with cursor **Load more**, an empty state (“No chats yet.”), and pinned agent **name · role** (never instructions, secrets, or filesystem paths). Session row actions (Rename, Archive/Unarchive, Delete) live in an overflow menu; archived rows are reached from a secondary list filter rather than a permanent checkbox. **New chat** returns to the empty composer and refreshes the catalog after detach so the previous row is no longer Open. The first send, attach, or Voice click creates a v2 session that pins the chosen agent version. Opening a created/paused session calls reopen (new runtime epoch when no live runtime) then attach; an already-attached session is opened by attach only. If that attach/reopen fails and GET session reports `ended`, the browser falls back to the read-only history view instead of treating the row as unavailable. Archived rows cannot be activated until **Unarchive**. Irreversible v1 **Ended** rows are labeled Ended and offer no reopen, rename, archive, unarchive, or activate. Opening an Ended row, or `/c/{sessionId}` for that session, loads GET `/api/v1/sessions/{id}` plus GET `/messages` as a read-only history view without hub attach; the composer, Voice, Attach, Send, and header **End** are absent. Catalog **Delete** uses a confirm dialog, then calls versioned `DELETE /api/v2/sessions/{id}?expectedRevision=`, including for Ended rows. Deleting the open session returns to the empty composer. Header overflow **End** remains v1 terminal-end and then stays on the same `/c/{id}` read-only view. Catalog mutation success and failure use a toast; blocking composer, picker, and owner-capability errors remain on-page alerts.

## Client routing (observed)

The active durable session is reflected at `/c/{sessionId}` (lowercase GUID). `/` is the new-chat composer. Opening a catalog row **pushes** history; creating a session on first send **replaces** the current entry so one composer step does not stack history. **New chat** pushes `/`. Deleting the open catalog row and invalid archived deep links **replace** `/` so browser Back does not return to a dead `/c/...` URL. Header **End** keeps `/c/{sessionId}` and shows the read-only history. Reload or direct navigation to `/c/{sessionId}` loads agents and the catalog, then reopens/attaches when the session is available; an ended session loads the same read-only history without attach. Archived or unknown/failed live opens show a dismissible inline notice and stay on `/`. A failed ended-history load stays on `/c/{sessionId}` with Retry. `popstate` re-syncs attachment from the URL without rewriting it. Vite dev/preview and the API host’s SPA fallback (`MapSpaFallback` when `wwwroot/index.html` is present) serve `index.html` for non-API paths.

## Attachment picker (observed)

Composer **Attach** (plus drag/drop on the form and paste on the Message field) uploads over HTTP only: `POST /api/v2/sessions/{id}/attachments` with `X-AgentCore-Owner-Capability`. Pending chips live inside the message stack (safe display name, percent while uploading, Retry/Remove); there is no separate attachment dock. Send stays disabled until the connection is ready and every queued file is `ready`; text-only send still works; attachment-only send is allowed when at least one ready id exists. `user.text` carries `attachmentIds`. Voice stages ready ids with `POST .../attachments/stage`. History chips show the safe name and authorized content via authenticated blob fetch (not bare `<img src>`). Pending unbound files never enter reasoning.

## Rich blocks (observed)

Live `agent.block.upsert` and reconnect `session.ready` history may include Markdown, attachment/artifact references, and unknown fallbacks. Assistant text and Markdown blocks render through a sanitized `react-markdown` pipeline (`remark-gfm`, `rehype-sanitize`; no raw HTML). `javascript:` and credentialed URLs never become `href`. Unknown blocks show `fallbackText`. Attachment refs reuse authenticated blob fetch. Artifact refs render an authorized fixture label (`Artifact · {id}`) without generating artifacts. `session.ready` replaces the entry list, so reconnect replays only server-projected visible blocks. Tombstoned responses ignore late `agent.block.upsert`. Display receipts (`response.received` with `textEndExclusive` and optional `blockIds`) do not imply speech was heard.

## Post-MVP planned until verified

Workspace physical provisioning and generated artifacts are observed. Typed tools and `sandbox.run` are observed on the runtime; shipped Support/Compliance allowlists still omit process/shell and sandbox. The identity picker starts Support and Compliance as separate durable catalog rows. No extra sandbox chrome is required in the composer. Phase I adds no WorkItem UI.

## Synthetic playback simulation

Synthetic Mode can use a browser service implementation that advances canonical sample counters with controlled time and emits the same Playback Progress/stop acknowledgements without an AudioContext, microphone or speaker. When /health reports profile=Synthetic, default the demo audio service to simulation; a developer-only toggle can opt into fake/real worklet transport testing. The simulation sends silent PCM and scripted boundaries through SignalR on controlled scenario time, while the server synthetic driver supplies matching transcripts. Scripted input thus crosses the same connection/session boundaries. Keep this simulation confined to the Synthetic/test profile; it does not replace production AudioWorklet playback. Separate Playwright media tests exercise real worklet execution with fake devices. See [Testing](16-testing-strategy.md#synthetic-full-stack-mode).
