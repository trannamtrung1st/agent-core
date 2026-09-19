# Product

This file is tooling context for Impeccable and UI agents.

It is NOT the canonical Agent Core product specification.

Canonical product behavior, architecture and implementation requirements live in `/docs`.

If this file conflicts with `/docs`, `/docs` wins.

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Primary users are operators and demo participants having a live conversation with one AI identity in a personal-chat interface. Typical situations: examining, customer-support, tutoring, interviewing, or similar roles defined by a versioned Agent Definition. The job is to talk in text or voice, interrupt naturally, and stay in one continuous conversation.

Other audiences (implementers, CI, Synthetic testers) use the same UI without hosted keys. They are not a second product.

## Product Purpose

Agent Core is a reusable harness for running an AI identity as a persistent conversational participant. The MVP thesis is conversational presence: low latency, natural turn-taking, interruption, identity consistency, restrained proactivity, and continuity.

Success is a simple personal-chat UI where text and voice are modes of **one** Session, the microphone stays active while the agent speaks, and shipped demo identities (examiner, customer support, compliance) feel different without changing the runtime.

## Positioning

The distinctive mechanism is one Session Runtime mailbox owning mutable conversation state, with a separate Interaction Controller for live turn-taking, plus independently replaceable STT, LLM and TTS on a composed text-first pipeline. It is not a general autonomous-agent platform, marketplace, or native speech-to-speech product.

## Operating Context

- Canonical specs: `/docs` (especially vision, MVP scope, frontend implementation, protocol, testing).
- Fast loop: native .NET + Vite; Synthetic profile is the default key-free path.
- Demo target: current Chromium-family desktop (Chrome/Edge).
- Identities load from versioned JSON under `agents/` (`examiner.json`, `customer-support.json`, `compliance.json`; see [docs/12](../../docs/12-backend-implementation-spec.md)).
- `voiceAvailable` on GET `/api/v1/agents` and on `session.ready` agent uses the same backend gate ([docs/04](../../docs/04-backend-interfaces.md)); the composer hides Voice when it is false. Synthetic profile advertises voice for voice-enabled agents; Real profile keeps voice hidden until hosted STT/TTS is wired, even if synthetic speech adapters are registered for development.
- Browser talks to `/hubs/session` (SignalR + MessagePack); HTTP is for agent/session/history and health, not live chat text.
- Persistence/reconnect restores the active conversation; ended catalog rows open a read-only history without attach; durable sessions use the v2 catalog rail (see [docs/13](../../docs/13-frontend-implementation-spec.md)), not a separate inbox product.

## Capabilities and Constraints

Confirmed MVP UI/product facts (see `/docs`; do not extend here):

- Choose an identity, open or resume a durable session from the catalog rail, or open an ended session as read-only history, and talk in one live conversation at a time.
- Text chat: agent header, scrollable conversation, composer/send, voice control when `voiceAvailable`, connection/error indicator.
- Voice is a **mode transition on the same session**, not a new conversation. Preflight microphone/AudioContext/worklets under user activation; send PCM only after Mode=voice. Pending voice shows Starting voice… and Cancel. After reconnect the user must press Voice again.
- Mute is input-only. The labeled composer stays available during voice (current session still accepts user text). Cancel voice returns to the same conversation’s text composer. Header End keeps `/c/{id}` as read-only history without attach.
- Visible status such as Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting (see [docs/13](../../docs/13-frontend-implementation-spec.md)). Assistant content renders as sanitized Markdown and blocks per docs/13; interrupted/failed entries keep a status label.
- Session catalog rail, attachment picker, and authorized blob fetch for history chips are shipped MVP UI (see [docs/13](../../docs/13-frontend-implementation-spec.md)); artifact refs show fixture labels only.
- Synthetic mode must not pretend to transcribe arbitrary speech; it is scripted and hardware-free by default.

Explicitly out of this UI foundation and out of MVP product UI:

- a separate admin dashboard, multi-tenant inbox, or second UI framework;
- unsanitized HTML injection, raw provider secrets in the UI, or a second component framework;
- agent workspaces as a full product surface, unrestricted autonomous tooling, or post-MVP proactive policy changes beyond current docs;
- authentication, WebRTC, native speech-to-speech, Ant Design Pro/ProComponents/X.

Stack (existing codebase, not a greenfield choice): React SPA, Vite, pnpm, strict TypeScript, Zustand, Ant Design v6 imported directly in product components, minimal `app.css`, `@microsoft/signalr` + MessagePack. No SSR or Next.js.

## Brand Commitments

- Name: Agent Core.
- Feel: personal messaging / live conversation, not an admin dashboard.
- Writing tone: direct, operator-facing, no invented marketing claims.
- Synthetic profile must remain labeled in the developer/demo UI.
- Do not fabricate testimonials, customers, or SLAs.

## Evidence on Hand

- Official docs under `/docs` (source of truth).
- Implemented SPA in `web/` uses Ant Design v6 plus the presentation policy in DESIGN.md (composer Model chip with in-button reasoning level, Spoken inset (first in assistant turn when it differs from display; TTS uses that projection only), compact 8px composer/overlay shells, 8/12/16px rhythm, 8px control inner padding matching session rows). `/docs` still owns behavior.
- Demo narratives in `docs/09-demo-scenarios.md`.
- No brand illustration pack, logo lockup, or photography set. Do not invent them.

## Product Principles

1. Conversational presence over feature count.
2. One Session for text and voice; do not split identity or history across screens.
3. Presentation must not own connection, audio, or session semantics.
4. Restrained proactivity and honest Synthetic labeling.
5. Accessibility and keyboard use are part of the product, not polish-later.

## Accessibility & Inclusion

Labeled controls, keyboard access, visible focus, and actionable errors are required ([docs/13-frontend-implementation-spec.md](../../docs/13-frontend-implementation-spec.md)). Status must not rely on color or animation alone. Honor `prefers-reduced-motion`. Contrast follows Ant Design defaults and DESIGN.md. Primary supported demo browser is Chromium desktop; do not depend on Chromium-only APIs when standard APIs suffice.
