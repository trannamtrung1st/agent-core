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

Success is a simple personal-chat UI where text and voice are modes of **one** Session, the microphone stays active while the agent speaks, and two identities (examiner and customer support) feel different without changing the runtime.

## Positioning

The distinctive mechanism is one Session Runtime mailbox owning mutable conversation state, with a separate Interaction Controller for live turn-taking, plus independently replaceable STT, LLM and TTS on a composed text-first pipeline. It is not a general autonomous-agent platform, marketplace, or native speech-to-speech product.

## Operating Context

- Canonical specs: `/docs` (especially vision, MVP scope, frontend implementation, protocol, testing).
- Fast loop: native .NET + Vite; Synthetic profile is the default key-free path.
- Demo target: current Chromium-family desktop (Chrome/Edge).
- Identities load from `agents/examiner.json` and `agents/customer-support.json`.
- Browser talks to `/hubs/session` (SignalR + MessagePack); HTTP is for agent/session/history and health, not live chat text.
- Persistence/reconnect restores the active conversation; there is no session-list product.

## Capabilities and Constraints

Confirmed MVP UI/product facts (see `/docs`; do not extend here):

- Select one of two identities, then open **one** conversation. No conversation-list/inbox UI.
- Text chat: agent header, scrollable conversation, composer/send, voice control when `voiceAvailable`, connection/error indicator.
- Voice is a **mode transition on the same session**, not a new conversation. Preflight microphone/AudioContext/worklets under user activation; send PCM only after Mode=voice. Pending voice shows Starting voice… and Cancel. After reconnect the user must press Voice again.
- Mute is input-only. The labeled composer stays available during voice (current session still accepts user text). End/Cancel returns to the same conversation’s text composer. History and drafts remain.
- Visible status such as Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting (see [docs/13](../../docs/13-frontend-implementation-spec.md)). Partial assistant text streams as escaped plain text. Interrupted/failed entries keep a status label.
- Synthetic mode must not pretend to transcribe arbitrary speech; it is scripted and hardware-free by default.

Explicitly out of this UI foundation and out of MVP product UI:

- persistent multi-session UI, session sidebar, chat history management;
- file/image attachments, rich assistant blocks, artifact panels, markdown HTML injection;
- agent workspaces, autonomous tools, sandboxing, post-MVP proactive changes;
- authentication, WebRTC, native speech-to-speech, large component frameworks.

Layout may later accommodate `session navigation | conversation` without implementing that navigation now.

Stack (existing codebase, not a greenfield choice): React SPA, Vite, pnpm, strict TypeScript, Zustand, plain CSS/CSS Modules, `@microsoft/signalr` + MessagePack. No SSR, Next.js, or large UI kit.

## Brand Commitments

- Name: Agent Core.
- Feel: personal messaging / live conversation, not an admin dashboard.
- Writing tone: direct, operator-facing, no invented marketing claims.
- Synthetic profile must remain labeled in the developer/demo UI.
- Do not fabricate testimonials, customers, or SLAs.

## Evidence on Hand

- Official docs under `/docs` (source of truth).
- Implemented SPA in `web/` ships the Pixel Dialogue Field / Obsidian Mint world recorded in DESIGN.md.
- Demo narratives in `docs/09-demo-scenarios.md`.
- No brand illustration pack, logo lockup, or photography set. Do not invent them.

## Product Principles

1. Conversational presence over feature count.
2. One Session for text and voice; do not split identity or history across screens.
3. Presentation must not own connection, audio, or session semantics.
4. Restrained proactivity and honest Synthetic labeling.
5. Accessibility and keyboard use are part of the product, not polish-later.

## Accessibility & Inclusion

Labeled controls, keyboard access, visible focus, and actionable errors are required ([docs/13-frontend-implementation-spec.md](../../docs/13-frontend-implementation-spec.md)). Status must not rely on color or animation alone. Honor `prefers-reduced-motion`. Body-text contrast follows DESIGN.md (at least 4.5:1 on field and panel). Primary supported demo browser is Chromium desktop; do not depend on Chromium-only APIs when standard APIs suffice.
