# Product

This file is tooling context for Impeccable and UI agents.

It is NOT the canonical Agent Core product specification.

Canonical product behavior, architecture and implementation requirements live in `/docs`.

If this file conflicts with `/docs`, `/docs` wins.

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Primary users are operators and demo participants having a live conversation with one AI identity in a personal-chat interface. Typical situations: examining, customer-support, tutoring, interviewing, or similar roles defined by a versioned Agent Definition. The job is to talk in text or voice, interrupt naturally, and stay in one continuous conversation. The same operator opens Admin from Chat to review definitions, drafts, and instances. Admin is configuration for that harness, not a second conversation product.

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
- Browser talks to `/hubs/session` (SignalR + MessagePack by default; `VITE_AGENTCORE_REALTIME_PROTOCOL=json` is a same-hub diagnostic mode). HTTP is for agent/session/history and health, not live chat text.
- A failed assistant turn, failed AgentRun, or admin server error can show one safe diagnostic id for copy. The id is server-owned. Conversation text and stacks stay off that copy.
- Persistence/reconnect restores the active conversation; ended catalog rows open a read-only history without attach; durable sessions use the v2 catalog rail (see [docs/13](../../docs/13-frontend-implementation-spec.md)), not a separate inbox product.

## Capabilities and Constraints

Confirmed MVP UI/product facts (see `/docs`; do not extend here):

- Choose an identity, open or resume a durable session from the catalog rail, or open an ended session as read-only history, and talk in one live conversation at a time.
- Text chat: agent header, scrollable conversation, composer/send, voice control when `voiceAvailable`, connection/error indicator.
- Voice is a **mode transition on the same session**, not a new conversation. Preflight microphone/AudioContext/worklets under user activation; send PCM only after Mode=voice. Pending voice shows Starting voice… and Cancel. After reconnect the user must press Voice again.
- Mute is input-only. The labeled composer stays available during voice (current session still accepts user text). Cancel voice returns to the same conversation’s text composer. Header End keeps `/c/{id}` as read-only history without attach.
- Visible status such as Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting (see [docs/13](../../docs/13-frontend-implementation-spec.md)). Assistant content renders as sanitized Markdown and blocks per docs/13; interrupted/failed entries keep a status label.
- Session catalog rail, attachment picker, and authorized blob fetch for history chips are shipped MVP UI (see [docs/13](../../docs/13-frontend-implementation-spec.md)); Artifact refs resolve canonical Core metadata and show file cards with authenticated, explicit Download and local Retry; retained files work in ended history.
- Synthetic mode must not pretend to transcribe arbitrary speech; it is scripted and hardware-free by default.
- Admin is a shipped route area on the same Ant Design dark UI (`/admin`, definition detail, instance detail). A definition draft edits Skills on the same Form and Advanced JSON candidate. Required capabilities are requirements, not grants. A definition can have no skills. A tool-capable user turn may show one in-progress agent message (“Still working”) before the answer and may pin another Skill from that definition without granting tools. Behavior, including leaving Chat and returning, stays in [docs/13](../../docs/13-frontend-implementation-spec.md).

Explicitly out of this UI foundation and out of MVP product UI:

- a multi-tenant inbox, an enterprise admin theme separate from the shipped Admin, or a second UI framework;
- unsanitized HTML injection, raw provider secrets in the UI, or a second component framework;
- agent workspaces as a full product surface, unrestricted autonomous tooling, or post-MVP proactive policy changes beyond current docs;
- authentication, WebRTC, native speech-to-speech, Ant Design Pro/ProComponents/X.

Stack (existing codebase, not a greenfield choice): React SPA, Vite, pnpm, strict TypeScript, Zustand, Ant Design v6 imported directly in product components, minimal `app.css`, `@microsoft/signalr` + MessagePack. No SSR or Next.js.

## Brand Commitments

- Name: Agent Core.
- Feel: Chat is personal messaging. Admin uses the same dark operate system for configuration, not a separate enterprise dashboard.
- Writing tone: direct, operator-facing, no invented marketing claims.
- Synthetic profile must remain labeled in the developer/demo UI.
- Do not fabricate testimonials, customers, or SLAs.

## Evidence on Hand

- Official docs under `/docs` (source of truth).
- Implemented SPA in `web/` uses Ant Design v6 plus the presentation policy in DESIGN.md (composer Model chip with in-button reasoning level, Spoken inset (first in assistant turn when it differs from display; TTS uses that projection only), compact 8px composer/overlay shells, 8/12/16px rhythm, 8px control inner padding matching session rows). `/docs` still owns behavior.
- Demo narratives in `docs/09-demo-scenarios.md`.
- Current Background Work is Session-first, with shared Run history/details, Files, focus-preserving navigation and inline Continue recovery; see [docs/13](../../docs/13-frontend-implementation-spec.md#background-work-drawer). Component/fixture checks cover 1440/768/390px; the [verification report](../../docs/reports/activation-agent-run-background-sessions-verification.md) records complete backend/browser/Compose and all five hosted gates on `8cec78c5d47a43e0236a5c38f2e312f4e36ce283`. The cutover is closed/frozen.
- The later [Admin UI feedback verification](../../docs/reports/admin-ui-feedback-verification.md) records bounded Synthetic runtime and responsive checks at 1440/768/390px. It is subsequent evidence, not a new hosted freeze claim. Current visual guidance and the Impeccable preview sidecar were synchronized with source `9145e66e` on October 10, 2026. The [shared Admin composition report](../../docs/reports/shared-admin-compositions-verification.md) and [main UI consistency follow-up](../../docs/reports/main-ui-style-consistency-verification.md) record the subsequent local checks. Definition/Instance configuration shares panel shells and nested navigation; screenshot privacy uses the bounded form, and Built-in subscriber/delivery tables use the collection layout. This refresh does not renew hosted acceptance.
- No brand illustration pack, logo lockup, or photography set. Do not invent them.

## Product Principles

1. Conversational presence over feature count.
2. One Session for text and voice; do not split identity or history across screens.
3. Presentation must not own connection, audio, or session semantics.
4. Restrained proactivity and honest Synthetic labeling.
5. Accessibility and keyboard use are part of the product, not polish-later.

Observed P9 Chat behavior, not a new visual system: while a browser tool runs, the existing activity row shows the server progress message `Using browser…`. There is no page panel, screenshot, or click log. `/docs` remains the product specification.

Current Credentials UX supersedes the P9.5 connection surface: global System Credentials and per-instance explicit bindings, masked create/replace with no reveal, independent Browser state reset, and global Connections → Events with per-instance reaction behavior under Automation → Triggers. Events distinguishes active subscribers from total subscriptions and received signals from Automation deliveries; request examples keep clipboard recovery inline. Instance Identity & version → Profile, the persistent Automation save summary and on-demand capability disclosure follow the current [frontend specification](../../docs/13-frontend-implementation-spec.md). Chat has no application connection indicator. Background Work retains text attention labels and quiet completions. Use existing Ant Design v6/layout tokens. `/docs` remains authoritative. Historical P9.5/P9.6 evidence stays unchanged.

Historical P9.6 operator behavior, not a current cutover claim: Admin Memory & automation saves an unattended model and a registration override, and names the effective source. Admin home creates, rotates, and revokes an Event Source credential that is shown once and then cleared. An instance authors an Event Automation for `order.placed` without receiving a store connection. At that stage, Background Work named the source (Automation Schedule/Event/manual) and showed the updated time in the viewer locale. The current Session-first presentation is recorded above and in docs/13. It does not render webhook evidence. P9 stays frozen on `bba1de4`. P10 and P11 were not started. `/docs` remains the product specification.

## Accessibility & Inclusion

Labeled controls, keyboard access, visible focus, and actionable errors are required ([docs/13-frontend-implementation-spec.md](../../docs/13-frontend-implementation-spec.md)). Status must not rely on color or animation alone. Honor `prefers-reduced-motion`. Contrast follows Ant Design defaults and DESIGN.md. Primary supported demo browser is Chromium desktop; do not depend on Chromium-only APIs when standard APIs suffice.

## Automation destinations and requested completion

An Automation chooses when to act and where its work belongs. Conversational reminders and follow-ups can return directly to their exact conversation even when it is closed in the browser. Independent background tasks retain their own history. Reporting to a conversation is a separate explicit choice; quiet recurring work stays in Background Work. Immediate background work reports by default, including with spontaneous Initiative off. An unavailable conversation produces an inspectable reason rather than moving the task elsewhere. Destination, originally created from and completion status have distinct labels; pending is not reported. Behavior stays authoritative in [docs/03](../../docs/03-system-architecture.md#automation-destinations-and-completion-obligations).


## Active background result handoff

A parent can explicitly inspect and use its owned initial child result while answering. A successful durable answer accounts for acknowledged evidence; remaining requested results return after the parent is available. Short duration or child waits retain the same Run and response. Chat and Admin show Ready, In use, Handled in conversation and a distinct Waiting state. Behavior remains in docs/03, docs/12–15.

Current projection authoring distinguishes explicit Always selections from Core-managed initial context. Authorized, eligible Browser v2 bootstrap tools and active Skill requirements can appear without that selection; no permission is granted by disclosure. See [frontend ownership](../../docs/13-frontend-implementation-spec.md) and the [bounded Background Work review](../../docs/reports/background-work-ui-review-verification.md). This records existing behavior, not a new projection policy or hosted closure.


### Built-in Event presets and filters

The existing Instance Automation drawer owns disabled Custom/preset drafts with Schedule or Events mode. Independent collapsible Event rows use stable identities, source type/key, current eligibility, filter/sample tests and dispatch; one grouped searchable picker discovers Built-in and Webhook definitions. Global Events shows safe code-owned Built-in definitions read-only alongside managed Webhooks; private activity stays Instance owned. Preserve separate unsaved mode branches and per-row samples, nested creation into the initiating row, shared Ant Design v6 controls, 640px/full-mobile drawer, reachable footer and focus return. Partial catalog failures retain successful definitions and draft state with retry. Product behavior is owned by docs/13 and docs/14; this is presentation context only.

## Definition Automation permission presentation

The existing Definition Form presents Schedule and Events with nested Built-in Events and Webhook Events using Ant Design v6 checkboxes, mixed parent state, paddingLG nesting and token-based spacing. Child permissions derive from the canonical candidate; a local parent off/on restores restricted choices. Read-only versions retain accurate disabled controls and overall-disabled guidance. Summaries and publish diffs name exact family grants. Backend identifiers and independent authority are unchanged. Product behavior belongs to [Frontend](../../docs/13-frontend-implementation-spec.md#unified-events-catalog-and-automation-editor); no additional visual system or permission state is introduced.
