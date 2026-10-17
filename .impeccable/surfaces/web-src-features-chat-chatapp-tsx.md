---
version: 1
slug: "web-src-features-chat-chatapp-tsx"
primary_target: "web/src/features/chat/ChatApp.tsx"
related_targets: ["web/src/features/chat/ChatHeader.tsx","web/src/features/chat/Conversation.tsx","web/src/features/chat/ChatMessage.tsx","web/src/features/chat/SpokenText.tsx","web/src/features/chat/Composer.tsx","web/src/features/chat/StructuredMessageEditor.tsx","web/src/features/chat/ComposerChipNode.ts","web/src/features/chat/MessageParts.tsx","web/src/features/chat/composerChoices.ts","web/src/features/chat/ModelPicker.tsx","web/src/features/chat/AgentPicker.tsx","web/src/features/chat/SessionRail.tsx","web/src/features/chat/BackgroundWorkDrawer.tsx","web/src/features/chat/AgentRunDetails.tsx","web/src/features/chat/SessionSandboxDrawer.tsx","web/src/features/chat/DiagnosticDetails.tsx","web/src/features/chat/detailPopover.tsx","web/src/features/chat/SessionArtifacts.tsx","web/src/features/chat/ArtifactView.tsx","web/src/features/chat/AttachmentPreview.tsx","web/src/features/chat/ChatFileCard.tsx","web/src/features/chat/DrawerListFooter.tsx","web/src/app/antdTheme.ts","web/src/features/filePreview/FilePreview.tsx","web/src/app.css"]
---

# ChatApp

Mode: Operate. One identity, one conversation; text and voice are modes of the same session. Session catalog remains a quiet rail, not an inbox.

## Direction contract

THESIS: Conversation-first operate surface. Quiet session rail, centered reading column, right-aligned user bubbles against open assistant Markdown, composer as the primary control (including Model/Reasoning). Refuses HUD/debug chrome and “Transcript · N entries” framing.

OWN-WORLD: Ant Design v6 dark operate UI. ChatGPT/Codex are layout and hierarchy references only—no ChatGPT assets, branding, or second kit. Product CSS is limited to rail width, header, column measure, bubbles, markdown, Spoken inset, composer shell, chips, and breakpoints.

STORY: Choose an identity, then talk. Status lives in the thread. Chats are a quiet list with overflow actions. Synthetic stays named. Public speech text that differs from display Markdown appears as Spoken first on the same assistant turn (TTS projection), then on-screen detail.

FIRST VIEWPORT: Black rail (`#000`) and `#141414` conversation. 280px rail at 1200px+ (240px from 768–1199px; Drawer below 768px) with Agent Core, New chat, Chats — shared 16px left edge, title+gap sections, no divider rules. Compact header: name and timestamp above a secondary role/profile/live-status row; stable trailing Speech locale and icon actions, with a deliberate actions row below 1200px. Long role/status text ellipsizes within the identity column. Direct End retains confirmation. Credential inventory and bindings live in Admin; the Chat header has no application connection row. Centered ~52rem column. User bubbles, open assistant Markdown, optional Spoken first (icon + Spoken label + secondary body, then display), solid Interrupted/Failed tags. After send, ~50% pane remains for the reply so prior turns stay in view. Sticky composer (`#1f1f1f`): Model as one chip that opens a Dropdown (catalog + reasoning footer/slider when supported); reasoning level is secondary text inside the Model button, not a sibling; attach, field, voice (when `voiceAvailable`), send. Composer and overlay use additive inset (8px compact shell + 8px control inner padding, same as session rows) so Message and Model text share one edge. Empty new-chat: “What do you want to work on?” plus Identity and Speech locale Selects and the same composer (Model stays in the composer, not the Identity stack). Paused/ended: composer hidden; no Model control. Paused shows Resume; ended shows a quiet read-only note with an inline New chat action, including on mobile, so request-isolated completion has a visible next step. Mobile empty header reads New chat; `profile` / `connection` stay visible.

FORM: Ant Design operate UI with ChatGPT/Codex conversational structure. Signature interaction: Enter sends on the same session; Voice is a mode transition; Model/Reasoning sit with the message well; activity is transient in-flow.

COMPOSER EXTENSION: The same message well uses a headless Lexical plain-text editor with atomic `/Skill` invocation and `@resource` reference chips, ordinary text and existing attachment previews. Add content offers Attach file, Use Skill and Reference resource. Slash/at-sign and menu entry share a caret-anchored elevated picker: one flat list with compact single-line rows: category icon, bounded name, muted metadata and quiet type label aligned to the row’s trailing edge. Names use their natural content width capped at 50% of the row body and do not shrink under metadata pressure; metadata follows the actual name width with an 8px gap and shrinks before the trailing type label; file paths use middle ellipsis preserving their final 12 characters, while other names and metadata use end ellipsis. Accessible labels and hover titles retain full details. Its 12px radius, 8px shell/row insets and 8px section gaps extend the existing rhythm. Width caps to 640px, the composer and viewport; placement chooses above/below, overlapping the caret within viewport bounds when neither side fits controls plus a complete choice. The list scrolls independently; heading, search and inline recovery stay visible; rows keep their 40px minimum height (44px below 768px). Pagination is automatic without a Load more button. Selection, editor/viewport resize and scrolling reposition it. Direct Ant Design v6 controls, Close icon, selected/unavailable states and polite announcements remain. The composer retains its neutral border and borderless editor while focused; choices show active/focus fill and search keeps its Ant Design focus state. Outside click, focus leaving, window blur and Escape dismiss overlays. Choice rows and picker buttons have 44px minimum mobile height. Draft, queue and user history share immutable labels, a 12px chip radius and 8px inline padding; editor/history line height is 1.85, queue previews single-line. Typed tooltips distinguish pending activation from historical invocation. `MessageParts` shares chip presentation with `ComposerChipNode`. Keep the 52rem column, additive 8px composer insets, existing palette/type/spacing and Model/Reasoning control. Behavior and canonical identity remain owned by docs/13 and the backend specification.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance.

## Scope

- Surface: `web/src/features/chat/*` and `web/src/app.css`.
- Preserve testids `connection` and `profile`, accessible names Identity, Speech locale, Model, Reasoning, Send, Voice, Cancel voice, Mute, Unmute, End, Add content, Spoken; the Add content menu retains Attach file.
- Composer stays in voice. End remains the existing session-end control until `/docs` and tests change together.
- Session catalog, attachments, realtime, and voice contracts stay compatible.

PRESENTATION: Compact Chat list/row menus retain danger styling and confirmation. Background Work is a Session catalog with complete wrapping original title headings, initial Run status, readable origin/time and optional progress/unread attention. View original result enters initial Run details and Original task files; expandable Conversation run history exposes follow-ups; Back receives focus and restores the exact opener on return. Continue failures/recovery remain in the drawer; successful live/paused/read-only ended opening reveals the chat. Eligibility remains in docs/13.

REUSE: Background Work and Run details cap at 640px on wide screens and use full viewport width below 768px. The body owns its 16px inset; catalog rows own their 12px inline inset and 8px control groups. AgentRunDetails adds no catalog-row or outer padding and is shared with Admin. Run history owns section separation. Outcome, confirmed effects and approval preview use open labeled, focusable regions with natural short-content height and independent scrolling capped at clamp(8rem, 24dvh, 16rem). Files reuse ArtifactView and the shared bounded-page footer. Cancel/Back stay content-width; selected-Session Continue is primary and fills available width. Narrow drawer controls retain 40px minimum height. Automations keeps its separate 400px wide/full-mobile width, title/subtitle and elevated schedule-expression inset. AppShell publishes resolved theme tokens for portal surfaces.

EVIDENCE: Responsive component previews and keyboard/recovery fixtures passed at 1440/768/390px. Legacy cutover compilation is resolved; actual Synthetic background creation, completion reporting and same-Session continuation are verified. Historical hosted acceptance passed on 8cec78c5d47a43e0236a5c38f2e312f4e36ce283. Documentation sync uses source/token/link checks and does not imply new runtime evidence; see docs/reports/activation-agent-run-background-sessions-verification.md.

DESTINATIONS: Reuse AutomationDestination and CompletionDeliveryStatus across Chat, Admin and Background Work. Exact conversation links disambiguate execution and reporting; creation provenance remains separate. Admin conditionally shows eligible owned conversation selection or unattended model plus report controls. Parent completion stays a normal message with modest child provenance. Pending/admitted does not mean reported; quiet/unavailable results remain inspectable. See docs/reports/automation-targets-background-reportback-verification.md for the bounded 1440/768/390 Synthetic runtime review. Prior hosted evidence remains historical.


HANDOFF: CompletionDeliveryStatus adds Ready, In use and Handled in conversation with exact View handling run. Handled suppresses unread attention; local read state never consumes a result. Shared AgentRunDetails retains readable evidence, deadline/child links and Cancel for Waiting. Responsive widths and insets stay inherited. The successor verification report records Synthetic consumption, reporting, cancellation and wait outcomes.

ORIGINAL RESULT: Background Work preserves original title/status/result/attempts/attention/files across continued Chat. Continue in chat becomes Open chat with modest Continued in chat text; delivery accounting remains separate. Unknown historical file ownership is explained, and missing initial Runs remain explicitly unavailable. Reuse AntD Collapse for conversation Run history and existing wrapping/reading-region rules. See docs/reports/background-original-result-verification.md for this successor’s evidence; earlier closure is historical.

BOUNDED REVIEW: October 9, 2026 inspection confirms the current Background Work catalog/detail layout with long original titles, attention/quiet outcomes, keyboard inspection/Back focus and 1440/768/390px sizing. No additional visual change was needed. Await settled drawer geometry after breakpoint transitions before capturing. See docs/reports/background-work-ui-review-verification.md; this is local verification and does not renew hosted acceptance.

## Heading metadata sync (2026-10-10)

Background Work initial Run status and Automations status follow the task title in a left-aligned wrapping row with an 8px compact gap. Background task titles use their natural width and wrap without growing to push status away. See [local verification](../../docs/reports/heading-metadata-layout-verification.md): live Admin checks at 1440/768/767/390px; chat drawer component coverage only. This sync adds no runtime run or hosted acceptance.

## Composer design synchronization (2026-10-11)

The presentation guide, schema-v2 preview sidecar and this brief use the current working-tree composer source. See [composer verification](../../docs/reports/composer-mention-polish-verification.md) for the existing 28 focused unit passes, 21 distinct browser scenarios across the final run and scoped rerun, runtime captures and limitations. This documentation refresh does not repeat application or hosted CI verification.

## Single-row composer synchronization (2026-10-11)

Names, metadata and types share one compact row across resource references and Skill invocations. The guide and static preview match the current row geometry, middle/end ellipsis and full accessible/hover details. The [successor verification](../../docs/reports/composer-single-row-verification.md) records 25 browser scenarios, 25 focused unit tests and the build from the implementation pass; this documentation follow-up verifies the preview and links without repeating application acceptance. Earlier two-line screenshots remain historical.

## Frontend design review (2026-10-12)

The [current review](../../docs/reports/frontend-design-system-review.md) inspects natural-width titles capped at 50%, trailing category alignment, neutral borderless chips and shared theme/spacing ownership. Synthetic interactions cover selection, sending, history restoration and exhausted search; responsive row measurements cover 1440/768/767/390/320px. A representative Admin inventory shares the same tokens and contains its table at desktop/mobile widths. The P2 Tab-focus ambiguity is corrected through the shared product outline policy, using a resolved Ant Design focus role. Regression coverage checks focus on a different row from selection and insertion of the focused choice. This is local refinement and verification, not milestone acceptance.

## Header status synchronization (2026-10-12)

The [header review](../../docs/reports/chat-header-status-review.md) verifies stable action coordinates through generation and reconnect at 1440/1200/1199/768/767/390/320px, long identity/error containment, keyboard actions, reload, pending Voice and paused-session recovery. DESIGN.md and the schema-v2 header excerpt now match role/profile/status grouping, 8/12px row gaps, 1200px layout transition and 24px Speech locale Select. This is local Synthetic verification, not hosted or physical-device acceptance.

## Drive credentials successor (2026-10-14)

Artifact cards now use one centered horizontal row with file icon, shrinking filename/type/size and two tooltip-equipped Preview/Download icon actions. Actions never shrink, keep full accessible filenames and retain 40px mobile targets; metadata/download failures keep local Retry. Generic Artifact and Workspace rows have no provider-specific export affordance. Existing FilePreview owns the same source authentication, read-only renderer canvas and focus/cancellation.

Credentials owns one Add credential purpose selector with reused Drive/Gmail/Custom/General fields and immutable-purpose specialist edits. Saved connection Tags remain separate from Active/Disabled status. Instance bindings own app-authorized Drive browsing; conversational tools own mutations. The shared ApprovalModal uses trusted file/name/version details, a full-width narrow-screen list, action-specific Trash/Rename/Move wording, recoverable-subset copy and the existing keyboard/Reject/footer behavior. Behavior remains owned by docs/13 and docs/14.

The [successor verification ledger](../../docs/reports/drive-credentials-ux-verification-2026-10-14.md) owns current local evidence and exact source identity. Earlier reports are historical; Real Trash requires an authorized disposable account scope and remains unverified.

## P10 local sandbox extension

Platform profiles and the Session sandbox drawer preserve the incumbent Ant Design operate surface and shared Office/Drive file preview. Theme tokens own spacing; output uses Collapse/copyable text and existing confirmations. A bounded 1440/1024/768/390/320px runtime review found no horizontal overflow after transitions settled. Save/Discard/navigation protection, actual artifacts, cancellation/reset and Core restart were exercised. Canonical docs and the OpenSandbox verification ledger own scope/remaining acceptance; this is local evidence, not production hardening.

### Session sandbox diagnostic grouping (2026-10-15)

Command diagnostics and Initialization diagnostics are visible native icon-and-text actions in their owning metadata rows, before output. Keep the 8px wrapping heading/metadata rhythm and 12px command-result stack; clear command/output paragraph bottom margins so the stack owns spacing. Missing IDs omit the action. The shared DiagnosticDetails label also names its popover, allowing neutral command/setup diagnostics while error notices keep their existing Error details context. Safe IDs and Session scope retain Copy; command diagnostics preserves the environment-ID fallback. Narrow drawer actions and shared diagnostic Copy buttons use 40px minimum height. Escape closes the popup and restores the trigger before another Escape closes its owner. See [focused local verification](../../docs/reports/sandbox-diagnostics-polish-verification-2026-10-15.md) for five-width grouping, Copy, failure cases and shared Admin recovery checks; no hosted acceptance is renewed.

## Composer attachment tiles and binary intake (2026-10-14)

Pending uploads use a compact responsive icon/filename grid inside the incumbent composer with complete hover/keyboard filenames, icon-only Remove/Retry, upload progress and local errors. Its 30vh/12rem vertical cap keeps the editor available; the parent list and each tile own separate native 8px insets. No pending thumbnails are generated. All file types are stored inertly within existing quotas; bounded Office extraction supports DOCX/XLSX. Sent previews/downloads retain their source-owned behavior. `/docs` owns upload authority and processing limits.

SANDBOX SELECTION/RECOVERY (2026-10-15): Chat keeps Session profile/image evidence, human-readable lifecycle/cancellation, bounded diagnostics and safe next actions in the existing drawer. Inspect status is read-only. Reset survives an ordinary poll while changed Session/generation/execution identity stays fenced. Shared Ant Design v6 controls were exercised at 1440/1024/768/390/320. See [the successor ledger](../../docs/reports/sandbox-selection-recovery-verification-2026-10-15.md) for exact observed scenarios and hardware limits.

A cleanly terminated Sandbox with a retained profile choice still offers confirmed Reset to return to the effective default; once cleared, a settled environment disables redundant Reset. Confirmation names that selection effect and keeps exact generation/execution fencing.

GENERIC LINUX RUNTIME (2026-10-15): Chat shows command exit/files independently from generic container recycling. A fully resolved Completed/committed recycle is informational, with Lifecycle evidence collapsed. Pending retirement/cleanup and unknown commits retain expanded truth and diagnostics; remote absence does not imply staging deletion. Explicit reset does not promise retained selection. Five-width runtime checks, Copy, Escape/focus, retained evidence during a 503 and Retry supplement actual real-SDK recycle and profile-switch commands. [Current evidence and coverage limits](../../docs/reports/generic-linux-sandbox-runtime-verification-2026-10-15.md).

FINAL SANDBOX FOLLOW-UP (2026-10-15): Separate file recovery from termination cleanup in the existing responsive Descriptions. NeedsResync retains staging explicitly and does not say No pending cleanup. Five-width controlled Synthetic inspection/recovery and Real disposable exact-file readback passed; no new styles or controls. [Evidence](../../docs/reports/sandbox-final-polish-acceptance-2026-10-15.md).


## Tenant Chat direction contract (2026-10-17)

THESIS: Extend the existing personal conversation with explicit organization scope and actual human permissions. Chat remains the primary task.

OWN-WORLD: Preserve the Ant Design v6 dark shell, shared transcript/composer, native status semantics and existing 8/12/16px spacing owners. No new palette or component kit.

STORY: Sign in, choose an organization and authorized agent, converse, recover a connection and inspect saved history. Authority loss clears private live state; a switch retires its document.

FIRST VIEWPORT: Existing Session rail beside one reading column; identity/profile/status stay at the header start, organization/account actions at its end. Message remains the primary action. Compact screens use the existing Chats drawer.

FORM: Code-led established Chat extension; the pinned incumbent composition supplies the form, so no new concept seed or comp applies. Signature interaction: switch organization without carrying its transcript, draft or transport into the next scope. Existing native motion remains.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
