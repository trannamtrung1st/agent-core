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
- Voice availability follows the selected Instance and advertised speech transports. The composer uses the server descriptor plus local browser support; current adapter and mode rules belong to [Frontend](../../docs/13-frontend-implementation-spec.md#primary-screens). Do not infer availability from the provider profile label alone.
- Browser talks to `/hubs/session` (SignalR + MessagePack by default; `VITE_AGENTCORE_REALTIME_PROTOCOL=json` is a same-hub diagnostic mode). HTTP is for agent/session/history and health, not live chat text.
- A failed assistant turn, failed AgentRun, or admin server error can show one safe diagnostic id for copy. The id is server-owned. Conversation text and stacks stay off that copy.
- Persistence/reconnect restores the active conversation; ended catalog rows open a read-only history without attach; durable sessions use the v2 catalog rail (see [docs/13](../../docs/13-frontend-implementation-spec.md)), not a separate inbox product.

## Capabilities and Constraints

Confirmed MVP UI/product facts (see `/docs`; do not extend here):

- Choose an identity, open or resume a durable session from the catalog rail, or open an ended session as read-only history, and talk in one live conversation at a time.
- Text chat: agent header, scrollable conversation, composer/send, voice control when `voiceAvailable`, connection/error indicator.
- Voice is a **mode transition on the same session**, not a new conversation. Preflight microphone/AudioContext/worklets under user activation; send PCM only after Mode=voice. Pending voice shows Starting voice… and Cancel. After transport loss capture is disarmed; when the Session remains in Voice, Resume microphone reauthorizes capture without changing mode. See the current mode rules in docs/13.
- Mute is input-only. The labeled composer stays available during voice (current session still accepts user text). Cancel voice returns to the same conversation’s text composer. Header End keeps `/c/{id}` as read-only history without attach.
- Visible status such as Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting (see [docs/13](../../docs/13-frontend-implementation-spec.md)). Assistant content renders as sanitized Markdown and blocks per docs/13; interrupted/failed entries keep a status label.
- Session catalog rail, attachment picker, and authorized blob fetch for history chips are shipped MVP UI (see [docs/13](../../docs/13-frontend-implementation-spec.md)); Artifact refs resolve canonical Core metadata and show file cards with authenticated, explicit Download and local Retry; retained files work in ended history.
- Synthetic mode must not pretend to transcribe arbitrary speech; it is scripted and hardware-free by default.
- Admin is a shipped route area on the same Ant Design dark UI (`/admin`, definition detail, instance detail). A definition draft edits Skills on the same Form and Advanced JSON candidate. Required capabilities are requirements, not grants. A definition can have no skills. A tool-capable user turn may show one in-progress agent message (“Still working”) before the answer and may pin another Skill from that definition without granting tools. Behavior, including leaving Chat and returning, stays in [docs/13](../../docs/13-frontend-implementation-spec.md).
- The implemented Office/file-preview/Drive successor provides shared protected read-only previews for Chat attachments/artifacts, owned home files and resources: raster images, escaped code/text, sanitized Markdown, CSV/TSV, native media and guarded DOCX/XLSX. Existing source-file actions remain the entry points. Optional bound Google Drive connections live in Credentials, with Instance file browsing. Generic source actions expose Preview/Download; authorized agent tools own Drive file mutations and upload-session recovery. Connecting an account does not grant tools or sensitive-action approval; Synthetic stays offline. Behavior and limits belong to [docs/13](../../docs/13-frontend-implementation-spec.md#shared-source-owned-file-preview).

Explicitly out of this UI foundation and out of MVP product UI:

- an enterprise admin theme separate from the shipped Admin, or a second UI framework; the authorized organization-access successor keeps the same dark application;
- unsanitized HTML injection, raw provider secrets in the UI, or a second component framework;
- unrestricted autonomous tooling or post-MVP proactive policy changes beyond current docs;
- WebRTC, native speech-to-speech, Ant Design Pro/ProComponents/X. The original authentication exclusion is superseded by the owner-authorized Account/IAM successor below.

Stack (existing codebase, not a greenfield choice): React SPA, Vite, pnpm, strict TypeScript, Zustand, Ant Design v6 imported directly in product components, minimal `app.css`, `@microsoft/signalr` + MessagePack. No SSR or Next.js.

## Brand Commitments

- Name: Agent Core.
- Feel: Chat is personal messaging. Admin uses the same dark operate system for configuration, not a separate enterprise dashboard.
- Writing tone: direct, operator-facing, no invented marketing claims.
- Synthetic profile must remain labeled in the developer/demo UI.
- Do not fabricate testimonials, customers, or SLAs.

## Evidence on Hand

- The [October 16 design sync](../../docs/reports/design-system-sync-2026-10-16.md) records current State observation, coordinated isolation/Memory/Voice drafts and canonical Workspace presentation. Shared preview details use the existing responsive Admin label layout. This is focused local evidence, with no new milestone or hosted acceptance claim.

- Official docs under `/docs` (source of truth).
- Implemented SPA in `web/` uses Ant Design v6 plus the presentation policy in DESIGN.md (composer Model chip with in-button reasoning level, Spoken inset (first in assistant turn when it differs from display; TTS uses that projection only), compact 8px composer/overlay shells, 8/12/16px rhythm, 8px control inner padding matching session rows). `/docs` still owns behavior.
- Demo narratives in `docs/09-demo-scenarios.md`.
- Current Background Work is Session-first, with shared Run history/details, Files, focus-preserving navigation and inline Continue recovery; see [docs/13](../../docs/13-frontend-implementation-spec.md#background-work-drawer). Component/fixture checks cover 1440/768/390px; the [verification report](../../docs/reports/activation-agent-run-background-sessions-verification.md) records complete backend/browser/Compose and all five hosted gates on `8cec78c5d47a43e0236a5c38f2e312f4e36ce283`. The cutover is closed/frozen.
- The later [Admin UI feedback verification](../../docs/reports/admin-ui-feedback-verification.md) records bounded Synthetic runtime and responsive checks at 1440/768/390px. It is subsequent evidence, not a new hosted freeze claim. At that pass, visual guidance and the Impeccable preview sidecar were synchronized with source `9145e66e` on October 10, 2026. The [shared Admin composition report](../../docs/reports/shared-admin-compositions-verification.md) and [main UI consistency follow-up](../../docs/reports/main-ui-style-consistency-verification.md) record the subsequent local checks. Definition/Instance configuration shares panel shells and nested navigation; screenshot privacy uses the bounded form, and Built-in subscriber/delivery tables use the collection layout. This refresh does not renew hosted acceptance.
- The focused Admin composition was verified against source `9a798360`: Agent Core/Admin branding, focused native-icon navigation, one shared sticky configuration frame, editable inherited settings with preserved field-level source/reset, and icon/More table actions. [Focused configuration evidence](../../docs/reports/admin-focused-configuration-verification.md) supplies current captures and local verification. Documentation sync adds no new acceptance claim.
- The shared preview/Drive geometry was verified against `8e053018` (2026-10-14). The [polish verification](../../docs/reports/file-preview-drive-ui-polish-2026-10-14.md) records five-width/short-screen containment, keyboard recovery, 1,004 unit passes and four Synthetic browser journeys. The [successor ledger](../../docs/reports/office-file-preview-drive-verification-2026-10-13.md) retains the earlier hosted acceptance and separate real Google/native zoom/Office-client gaps. This documentation refresh adds no runtime or acceptance claim.
- No brand illustration pack, logo lockup, or photography set. Do not invent them.

## Product Principles

1. Conversational presence over feature count.
2. One Session for text and voice; do not split identity or history across screens.
3. Presentation must not own connection, audio, or session semantics.
4. Restrained proactivity and honest Synthetic labeling.
5. Accessibility and keyboard use are part of the product, not polish-later.

Observed P9 Chat behavior, not a new visual system: while a browser tool runs, the existing activity row shows the server progress message `Using browser…`. There is no page panel, screenshot, or click log. `/docs` remains the product specification.

Current Credentials UX supersedes the P9.5 connection surface: global System Credentials and per-instance explicit bindings, masked create/replace with no reveal, independent Browser state reset, and global Connections → Events with per-instance reaction behavior under Automation → Triggers. Events distinguishes active subscribers from total subscriptions and received signals from Automation deliveries; request examples keep clipboard recovery inline. Instance Profile, focused configuration/governance pages and the persistent Automation save summary and on-demand capability disclosure follow the current [frontend specification](../../docs/13-frontend-implementation-spec.md). Chat has no application connection indicator. Background Work retains text attention labels and quiet completions. Use existing Ant Design v6/layout tokens. `/docs` remains authoritative. Historical P9.5/P9.6 evidence stays unchanged.

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

Presentation follow-up: section counts, revisions, IDs and status/attention labels follow their titles with the shared compact gap and wrapping. The [frontend specification](../../docs/13-frontend-implementation-spec.md#heading-supporting-text) owns the affected surfaces; [local verification](../../docs/reports/heading-metadata-layout-verification.md) distinguishes live Admin checks from chat drawer component coverage.

## Observed Email Account journey

Operators create/edit/test/rotate protected mail accounts under Connections → Credentials and bind them under Instance → Credentials. Authorized Chat can request a trusted account chooser, explicitly grant one account, then continue in a fresh Run. A separate exact-content approval confirms sender/account/recipients/body before send. Synthetic uses explicit fixture accounts and no live mail connection; Real setup remains deferred. This context describes UI; docs/13, docs/14 and the dated email evidence report own behavior and verification.

Current Gmail owner journey uses one explicit Google consent in global Credentials, protected backend refresh, verified mailbox and connect/check/reconnect/disconnect. Custom mail remains the advanced TLS/password choice. Synthetic clearly identifies offline authorization and no real delivery; binding and exact send approval stay separate. See docs/13 and the OAuth verification report for canonical behavior and acceptance.

Current Drive correctness follow-up: [review verification](../../docs/reports/file-preview-drive-review-2026-10-14.md) records source `0763058d`, 17 focused tests, four Synthetic browser journeys, clear-search restoration and one-upload receipt recovery with locked destinations. This adds no new capability, authority or hosted closure.

Current presentation sync against `d279abfe` (2026-10-14): compact pending uploads, shared sent-file/artifact cards, host-bounded FortuneSheet previews, purpose-based credential creation and exact-target Drive approval tables follow the existing Ant Design baseline. The [recent review](../../docs/reports/recent-git-ui-design-review-2026-10-14.md) records focused local verification and the original XLSX clipping-notice and Drive approval keyboard-rejection findings; [integrated verification](../../docs/reports/opensandbox-integration-verification.md#current-main-integration--2026-10-14) records their corrections. Earlier save-form evidence is historical; generic Save to Drive UI and routes are removed.


## Trusted-local execution tooling context

The approved P10 local-demo cutover adds one Session-owned OpenSandbox environment and a prebuilt coding/data/Office Workbench. Profiles configure environments; Definition/Instance capabilities own authority. One trusted server uses the existing local Docker daemon; Core/workloads receive no socket or canonical host mounts. Canonical /working, durable /home and Artifacts remain distinct. Current Office/Drive/file-preview behavior stays in the shared surfaces. Own execd loopback and absent Docker writable-layer quota are disclosed local-demo limitations; production hardening remains separate. /docs owns all product and acceptance facts.

OCI catalog owner journey: start trusted server infrastructure, register prebuilt digest in Sandboxes, optionally choose a protected Container registry System Credential, verify the saved revision, explicitly authorize on Definition and select on Instance. Startup does not build or seed workloads. One Session generation retains one image; no builder/composition/registry tool UI. See docs/13 for behavior and the OCI ledger for actual evidence.

## State and isolation tooling context

Instance Operations → State shows live versus durable activity and truthful partial/stale resource observations. Configuration → State & Isolation uses ordinary inherited fields and editable Persistent, Session-isolated and Stateless preset drafts, with the existing Save/Discard/Refresh owner. Definition drafts author defaults; publications remain read-only. Preset changes retain historical data and external credentials keep their own authority. [Frontend](../../docs/13-frontend-implementation-spec.md#state-and-isolation-admin-surfaces) owns behavior; the dated state/isolation ledger owns evidence. Existing Ant Design v6 panels and responsive grids are reused.

## Account and organization access tooling context

The owner-authorized Account/IAM successor adds sign-in, invitation acceptance, Account password changes, My Organizations, organization switching, current effective access, Members/Invitations, builtin/custom Roles and audit to the existing Ant Design operate UI. These surfaces reuse AdminShell and shared collection/confirmation/form-feedback owners. Current permissions govern visible actions; human and agent authority remain distinct. `/docs/19` owns the UX contract and the [access UI report](../../docs/reports/access-ui-polish-2026-10-16.md) retains the original foundation evidence.

Subsequent focused checkpoints verify [canonical tenant text Chat](../../docs/reports/tenant-chat-ui-verification-2026-10-17.md), the [durable approval inbox and exact-action inspector](../../docs/reports/tenant-approval-ui-verification-2026-10-17.md), and [shared original-Chat approval links/status and inbox attention](../../docs/reports/tenant-chat-approvals-verification-2026-10-17.md). [Instances and Agent Access](../../docs/reports/tenant-agent-access-ui-verification-2026-10-17.md) and [Instance Connections](../../docs/reports/tenant-agent-connections-ui-verification-2026-10-17.md) also have scoped browser evidence. These extend the existing conversation, review and configuration owners. Tenant voice/rich resources, other resource/effect families and the broader tenant cutover remain open; this context does not claim a complete multi-tenant deployment.


The subsequent tenant Definition extension reuses the same operate shell and shared candidate/configuration compositions. Owned inventory, draft/resource/evaluation and publication lifecycle now have focused browser evidence; [the tenant UI report](../../docs/reports/tenant-definition-ui-verification-2026-10-16.md) owns measured scope and remaining editor/runtime boundaries. Tokens and visual system are unchanged; `/docs` remains authoritative.

### Tenant Instance Profile and Settings tooling context

Canonical Instance navigation now retains Profile, Settings, Access and Connections for the same organization/Instance. Profile reuses the shared four-field persona Form/JSON controls; Settings reuses the existing typed section editor with tenant-owned transport, independent sparse drafts and inherited-source/default/reset hints. Section Save/Discard/Reset remains deliberate and preserves unrelated drafts. Read-only/archived inspection, stale draft retention, explicit reload and lost-access recovery use the shared feedback owners. Configuration guidance and capability eligibility remain separate from execution IAM and approvals.

The [Profile/Settings interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-profile-and-settings-interaction-contract) owns behavior; [focused Synthetic editor verification](../../docs/reports/tenant-instance-editor-ui-verification-2026-10-17.md) owns actual saves, reset/reload/revocation, legacy-consumer recovery and six-width presentation through 320px. This is an ordinary extension of the incumbent Ant Design v6 dark system with no token, CSS or asset change. The execution-controls successor below supplies canonical budgets, management and unattended-model preferences. The Definition version successor below supplies version review/adoption. The Skills successor below supplies scoped inspection/authoring. The Resources successor below supplies scoped file management; tenant Sandbox catalog/effects and the remaining proposal obligations stay open; no full proposal or milestone acceptance is claimed.


### Tenant Instance execution controls tooling context

Canonical Instance navigation adds Execution budgets, Self-management and Unattended model alongside Profile/Settings, Access and Connections. These pages share the current identity/lifecycle/Definition heading with Profile/Settings and reuse existing native Ant Design field, panel, feedback and confirmation owners. Budget classes inherit independently; management retains per-field inheritance and separate saved mode/Frozen status. Reset stages the draft for explicit Save. Freeze/unfreeze acts on the saved policy and preserves unrelated edits. The original-human coherent read supplies the matching scoped projection; unattended-model catalog failure requires read recovery, and unavailable choices remain deliberate repair work. Configuration remains separate from execution IAM and exact approvals.

The [execution-controls interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-execution-controls-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#tenant-instance-execution-controls) own behavior; [focused verification](../../docs/reports/tenant-instance-controls-ui-verification-2026-10-18.md) owns actual interactions and limits. Implementation and finish reviewers inspected 18 current captures covering the three pages through 320px. Loading, denied, archived, error and stale states have no visual acceptance from those captures; separately exercised functional boundaries remain report-scoped. This narrow presentation sync preserves the incumbent dark Ant Design v6 system and historical evidence. The Definition version successor below supplies version review/adoption. The Skills successor below supplies scoped inspection/authoring. The Resources successor below supplies scoped file management. Tenant Sandbox cutover, other resource/effect families, voice/rich delivery, autonomous origins and final obsolete-authority/schema consolidation remain open; no full proposal or milestone acceptance is claimed.


### Tenant Instance Definition version tooling context

Canonical Instance navigation now includes Definition version for the same organization/Instance. The shared heading and bounded panels lead from actual paged source publications to explicit, non-mutating Preview changes and exact-target Adopt confirmation. The shared `InstanceVersionChanges` presentation also serves the existing administrative preview; default labels, preserved choices and inherited keys retain ordinary readable text and wrap through 320px. Partial child access is disclosed without hidden names or counts. Read-only users can inspect a permitted source preview; archived and revoked-source states retain truthful restrictions. Selection/scope changes retire the review, stale facts require Reload and a new preview, and a confirmed save with an unreadable acknowledgement recovers by read without replay. Cancel/Escape restores the Adopt opener; a submitted confirmation returns focus to Reload.

The [version interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-definition-version-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#tenant-instance-definition-version) own behavior; [focused verification](../../docs/reports/tenant-instance-version-ui-verification-2026-10-18.md) owns executed interactions and limits. Implementation and fresh finish reviewers inspected 19 current captures covering the five-width review, native confirmation, legacy shared preview, archived, read-only and revoked-source presentation. Loading, transient errors, malformed acknowledgements and stale presentation have no visual acceptance from these captures; functional checks remain separately report-scoped. This narrow merge preserves the incumbent Ant Design v6 dark system and historical evidence. The Skills successor below supplies scoped inspection/authoring. The Resources successor below supplies scoped file management. Sandbox/other resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open; no full proposal or milestone acceptance is claimed.


### Tenant Instance Skills tooling context

Canonical Instance navigation adds Skills for the same organization/Instance. The shared heading, bounded panel and native Skill Drawer present a searchable, paged collection of readable Built-in, inherited Definition and local procedures. One wrapping row keeps full names, activation, capability requirements, effective/stored enabled facts and source/copy provenance together. Procedure choice and configuration access remain separate from execution IAM. Built-in/inherited content is inspectable; authorized local authoring and exact-target customize/reset/delete use existing fields, feedback and confirmations. Customize creates an independent local copy and disables the inherited source atomically.

Independent exact-item reads supply current witnesses while Reload retains a draft, Discard adopts the latest values and stale saves require a new explicit Save. Acknowledged mutations retire submitted edits before read recovery without replay. Definitive item-read loss closes private content and refreshes the readable collection. Dirty Cancel/navigation, Escape/focus restoration and typed read-only/archived guidance retain shared owners; optional Skill Drawer inputs preserve existing Definition consumers' default behavior.

The [Skills/Resources interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-skills-and-resources-interaction-contract) owns behavior; [focused verification](../../docs/reports/tenant-instance-skills-ui-verification-2026-10-18.md) owns actual journeys and limits. Implementation and fresh finish reviewers individually inspected 18 current captures covering five widths through 320px, editor/confirmation, Built-in/provenance, shared Definition Drawer, archived, read-only and denied views. Native scroll containers leave lower Drawer fields/procedure uncaptured; loading, transient, stale and malformed-acknowledgement states have no visual acceptance. Separately executed functional checks stay report-scoped. This narrow merge preserves the incumbent dark Ant Design v6 system and historical evidence. The Resources successor below supplies scoped file management and rich/bulk adapters. Sandbox/other resource/effect families, voice/rich delivery, autonomous origins, obsolete-authority/schema consolidation and the earlier SQLite preference follow-up remain open; no full proposal or milestone acceptance is claimed.


### Tenant Instance Resources tooling context

Canonical Instance Resources reuses the scoped heading, bounded native panel and shared Instance resource editor/inspector. Search/paging concern readable metadata; full paths, origin/kind/size, readable dependencies, effective/stored enabled choices and copy provenance remain together. File/folder selections stay staged in the existing import review until explicit Save. Local metadata edits preserve bytes unless replaced; inherited files stay read-only, and a deliberate distinct-destination copy retains provenance while preserving the inherited source's enabled choice. Configuration access and file availability grant no execution authority.

Shared read-only previews now support canonical tenant Resource transport, including bounded Word/spreadsheet/image rendering and exact original downloads. Definitive nested read loss retires protected inspection. Reload retains drafts, Discard adopts current facts and acknowledged imports retire individually without replay. Native confirmations, dirty guards, Cancel/Escape and surviving-trigger focus restoration keep their existing owners. The 48rem column, 16px panel/8px group rhythm, 880px import and 640px editor/detail Drawers, full-mobile widths and shared feedback placement are preserved; feature owners retain state, transport and commands.

The [Skills/Resources interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-skills-and-resources-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md) own behavior; [focused verification](../../docs/reports/tenant-instance-resources-ui-verification-2026-10-18.md) owns actual tenant/shared-consumer journeys and exact recovery limits. The fresh finish reviewer inspected all 25 named current captures covering five widths through 320px, import/editor lower regions, copy/provenance, rich previews, empty, read-only, archived and denied presentation. Loading, transient, stale and malformed-acknowledgement states have no visual acceptance from those captures. This narrow merge preserves normative tokens, theme, assets and historical evidence; it performs no runtime verification. It supersedes Resources UI/rich-file/bulk-adapter-pending context above for Instance configuration only. Sandbox, other resource Access/effect families, Governance/platform, voice/rich Chat delivery, autonomous origins, obsolete-authority/schema consolidation and the earlier SQLite preference follow-up remain open; no full proposal or milestone acceptance is claimed.

### Organization Sandbox catalog tooling context

Organization Integrations now has a scoped Sandbox profile collection and addressable current/history inspector. `TenantSandboxes` reuses `AdminShell`, the collection toolbar, native guarded access Drawers and shared `SandboxProfileFields`, `SandboxProfileDetails` and evidence. Full wrapping names, revision, enabled intent and named availability lead the collection; saved image/configuration detail stays in the inspector. Authoring, verification and agent execution authority remain separate. The existing platform catalog consumes the same fields while retaining its own transport, identity and source bounds.

The [catalog interaction contract](../../docs/19-multi-tenant-iam-ux.md#organization-sandbox-catalog-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#organization-sandbox-profile-catalog) own behavior. [Focused verification](../../docs/reports/tenant-sandbox-catalog-ui-verification-2026-10-18.md) records actual Synthetic create/edit/disable, immutable history, stale-draft recovery, one-time malformed-acknowledgement recovery and keyboard draft/Cancel focus restoration; controlled verification/access and shared-platform projections remain separately identified. A fresh finish reviewer inspected 29 captures and returned SHIP for that scope. Collection and Drawers fit 1440/1024/768/390/320px without document overflow. Loading, empty/search-empty, transient, stale, malformed acknowledgement, history and confirmation lack dedicated visual acceptance; shared platform lower fields and mobile CPU/memory remain visually unverified.

This narrow context merge preserves the Ant Design v6 dark system, native 24px Drawer inset, 16px section rhythm, existing field grid and historical sidecar fingerprints. It performs no application verification or sidecar regeneration. It supersedes catalog-UI-pending context above only for organization profile authoring/inspection. Instance Sandbox configuration/verification, Session Sandbox controls UI, the remaining proposal obligations and live registry/SDK qualification remain open; no full proposal or milestone acceptance is claimed.


### Tenant Instance Sandbox tooling context

Canonical Instance navigation now includes Sandbox for the same organization/Instance, separate from generic Settings. The original-human setup read supplies the matching name, lifecycle, Definition version, readable profile choices and saved setup witnesses. The existing 48rem form, shared heading and native `InstanceSettingsSection` Collapse retain sparse allowed/default choices, bounded limits/storage, initialization, inherited-source/ceiling hints and staged reset. Generic Settings links to the dedicated destination without reading its protected choices. Saving configuration grants no execution authority and runs no initialization.

Saved setup verification follows the fields as a separate, independently authorized task. A readable exact-profile selector leads to saved source/image/revision facts, current accepted observation and latest/exact retained attempts. Native confirmation names organization, agent and saved revisions before disposable ordered Profile/Instance initialization. Dirty or incoherent setup and pending cleanup block a new attempt. Stale saves retain deliberate drafts until explicit Reload; acknowledged malformed saves retire submitted edits before read recovery. Failed verification responses require retained-evidence reads and never replay initialization. Partial-catalog, read-only, archived and denied states preserve truthful boundaries; restricted settings do not hide independently permitted verification.

The [interaction contract](../../docs/19-multi-tenant-iam-ux.md#instance-sandbox-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#tenant-instance-sandbox-configuration-and-verification) own behavior. [Focused verification](../../docs/reports/tenant-instance-sandbox-ui-verification-2026-10-18.md) owns actual Synthetic saved setup/recovery and disabled-host verification, separately from controlled verification/access projections and live SDK qualification. The fresh finish reviewer returned SHIP for the inspected scope. Settings and verification were captured at 1440/1024/768/390/320px, shared Settings at 1440/320px, and controlled read-only/partial/archived/denied views at 320px. Corrected read-only and partial captures include the shared heading; denied reads intentionally clear protected identity. Controlled permission presentation is not backend authorization proof; independent verification authority has separate two-store test evidence.

This bounded documentation merge samples existing captures and performs no app checks. It preserves normative tokens, theme, fonts, CSS, assets, previews and historical sidecar fingerprints. It supersedes Instance Sandbox configuration/verification-pending context above only for this page. Session Sandbox controls UI, remaining proposal work and live SDK qualification stay open; no full proposal or milestone acceptance is claimed.

### Tenant Chat Session Sandbox tooling context

The existing Chat header now opens a canonical organization/Session Sandbox inspector independently from Chat editing or administrative Instance updates. `TenantSessionSandboxDrawer` consumes one original-human controls read for stored observation, current Instance revision, readable allowed profiles and independent reset/Stop/selection hints. Stored initialization, latest command, exact historical receipt and pending cleanup remain distinct from provider readiness, command authority and confirmed effects. Exact-context native confirmations explain preserved canonical files and fresh Review; read recovery never repeats a mutation. The [interaction contract](../../docs/19-multi-tenant-iam-ux.md#session-sandbox-controls-interaction-contract) and [Frontend](../../docs/13-frontend-implementation-spec.md#tenant-chat-session-sandbox-inspector) own behavior.

`SessionSandboxFrame` shares the incumbent dark Ant Design v6 640px/full-mobile Drawer, native body inset, section spacing and opener focus with the existing consumer. Responsive Descriptions, wrapping native actions and bounded escaped `SandboxEvidence` retain their presentation owners; background command output explicitly says combined. [Focused verification](../../docs/reports/tenant-session-sandbox-ui-verification-2026-10-18.md) separates actual Synthetic sign-in, Chat send/reply and scoped empty reads from controlled populated/control/permission adapters. The fresh finish reviewer returned SHIP without findings for all 21 current captures at `/private/tmp/agent-core-session-sandbox-ui-iSeerp`, including five-width ready/controls, pending, confirmation, exact receipt, read-only, denied, empty, Chat header and shared legacy views. Functional focus/recovery evidence remains report-scoped. Loading, hover, other control confirmations, stale and malformed-acknowledgement presentation lack dedicated visual acceptance; controlled permission views do not establish backend authorization.

This narrow successor merge performs no app checks and preserves normative tokens, theme, CSS, fonts, assets, previews and historical sidecar fingerprints. It supersedes Session Sandbox controls-UI-pending context above only for this supported adapter. Remaining proposal families, final authority/schema consolidation, the earlier SQLite preference reliability follow-up and live SDK/provider/Docker qualification remain open; no full proposal or milestone acceptance is claimed.
