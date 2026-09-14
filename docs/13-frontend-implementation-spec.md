# Frontend Implementation Specification

## Baseline and modules

React SPA, Vite, strict TypeScript, Zustand, @microsoft/signalr, @microsoft/signalr-protocol-msgpack, browser fetch, plain modern CSS/CSS Modules. Vitest and React Testing Library test behavior; Playwright tests synthetic end-to-end flows. No SSR, Next.js, heavy data cache or large component framework.

Future web/src modules: app (routing/composition), features/chat, features/voice, services/api, services/realtime, services/audio (capture, encoding, VAD, queue, worklets, progress), state/sessionStore, contracts. Wire types are hand-maintained against [Protocol](14-api-and-realtime-protocol.md) with serialization fixtures; no generated types are created during this docs task. Browser services own long-lived connections/audio; React components subscribe, issue commands and render state.

Zustand contains session descriptor, attachmentId, connection state, input/output state, mode, pendingMode, entry map/order, liveResponseId, response terminal statuses, transcripts, mute and safe errors. PCM buffers, AudioContext, stream readers and HubConnection objects live in services, never reactive store state. Reducer guards attachmentId, responseId, event sequence and text offsets before appending output. Discard superseded response chunks even when they arrive after R2 started. Keep tombstones for all responses in the attached session; bounded by the session entry limit, reset only after authoritative snapshot replacement.

## Primary screens

Text chat: agent header, scrollable conversation, composer/send, voice-call button (hidden or disabled when `voiceAvailable` is false), connection/error indicator. Select one of two identities on entry, then open **one** conversation. There is no MVP conversation-list/inbox UI. Text stays plain escaped text for MVP; no HTML injection/markdown renderer needed. Partial assistant text appears incrementally, interrupted/failed entries retain a subtle status label. Drafts survive reconnect; submitting is disabled until ready. Sending another user text intentionally supersedes the active response.

Voice call: agent name, single human-readable status (Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting), transcript/history disclosure, Mute and End call. Microphone remains active while the agent speaks. Keep the call layout simple, accessible buttons with labels, keyboard support and visible focus. Error text explains the next action. An optional developer-only panel shows event timeline, latencies, IDs, transcript evidence and controller decisions without dominating the call UI.

The voice-call control is a **mode transition on the same session**, not a new conversation. From text chat, it sends `session.mode.set(voice)`, then initializes microphone/STT/audio output from that user gesture **after** the server applies voice mode. While `pendingMode` is `voice` (queued because a text response is still live), show **Starting voice…**, disable the voice-call button, and do not claim the transition failed. End call sends `session.mode.set(text)`, disposes tracks/AudioContext/worklets, and returns to the same conversation's text composer. History, drafts and identity remain. Mute only affects input. If voice is unavailable, show `VoiceUnavailable` and stay in text. Follow [mode-transition safety](05-interaction-controller.md#mode-transitions): wait out a live text response before entering voice; leaving voice while the agent is speaking supersedes that voice response.

## Connection service

Register event handlers before starting HubConnection. Enable MessagePack and WebSockets. Use reconnect delays 0/2/5/10 seconds within a 60-second UI retry budget, including initial start failures; after budget exhaustion offer explicit Retry. Each new connection invokes Attach; reconnect alone does not restore attachment. session.ready is an authoritative snapshot, not an ordinary delta. Stop all playback immediately on connection loss, discard input buffers and show Reconnecting. Do not replay microphone frames or proactive timers.

Use fetch only for agent/session/history endpoints. Default same-origin relative URLs; Vite proxies /api, /hubs (WebSocket upgrade) and /health to localhost:5080 in development. Never put API keys in frontend configuration. A new attachment invalidates old callbacks by captured attachmentId; service cleanup unregisters handlers and aborts old requests. React StrictMode mount/unmount must not create duplicate microphone tracks or hub subscriptions.

## Browser support (MVP)

Primary supported demo target: **current Chromium-family desktop browsers (Chrome and Edge)**. Firefox and Safari may be exercised as best-effort until AudioWorklet, autoplay, microphone permission and SignalR/WebSocket behavior are verified. Do not write Chromium-only application logic when standard browser APIs suffice; this is a support and test-scope decision.

## Audio services

[Realtime Voice](06-realtime-voice.md) specifies DSP, queues, formats and tracking. The UI presents a continuous call over the composed text-first pipeline, not voice notes or native speech-to-speech reasoning. Initialize capture/playback from the call button's user gesture. Handle denied permission, missing AudioWorklet, suspended AudioContext and device loss as actionable audio errors. Do not claim Listening until recognition stream ready and capture active. If the device rate is 44.1/48 kHz, resample continuously with preserved filter state; never assume 24 kHz device support.

The output worklet validates its active response epoch and stops at the next render quantum on flush, with a short fade where practical. It reports consumed samples independently of UI rendering. The connection service emits playback acknowledgements every 100 ms, and text mode emits response.received every 100 ms and at final render. These receipts are advisory delivery evidence, validated on the server. Queue underflow outputs silence without advancing consumed samples. Queue overflow triggers a recoverable AudioBackpressure error and stops that response.

Local VAD ducking is an optional, independently disableable UX optimization; restore gain after controller Continue/Ignore or timeout. Only authoritative supersession/stop/end/disconnect tombstones a response. Never mute microphone automatically to solve speaker echo.

## Frontend acceptance

Tests must cover stream append by textStart, duplicate delivery, late R1 data, control sequence gaps, `session.mode.set` continuity, pending voice start (Starting voice… / disabled control), audio ordering/flush, permission denial, mute/unmute stream changes, reconnect snapshot replacement, uncertain text retry and graceful error rendering. Playwright runs against Synthetic without keys, using a fake media device and explicit scripted speech fixture for transcript content. Synthetic STT does not pretend to recognize arbitrary real microphone speech. A manual real-device pass on the primary Chromium demo target is required for echo, AudioContext/autoplay behavior and audible stop latency; these are not guaranteed by DOM tests. Firefox/Safari checks are best-effort until verified.


## Synthetic playback simulation

Synthetic Mode can use a browser service implementation that advances canonical sample counters with controlled time and emits the same Playback Progress/stop acknowledgements without an AudioContext, microphone or speaker. When /health reports profile=Synthetic, default the demo audio service to simulation; a developer-only toggle can opt into fake/real worklet transport testing. The simulation sends silent PCM and scripted boundaries through SignalR on controlled scenario time, while the server synthetic driver supplies matching transcripts. Scripted input thus crosses the same connection/session boundaries. Keep this simulation confined to the Synthetic/test profile; it does not replace production AudioWorklet playback. Separate Playwright media tests exercise real worklet execution with fake devices. See [Testing](16-testing-strategy.md#synthetic-full-stack-mode).
