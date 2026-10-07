---
name: Agent Core
description: Ant Design v6 dark operate UI for Chat and the shipped Admin configuration surface.
colors:
  primary: "#1677ff"
  success: "#52c41a"
  successHover: "#73d13d"
  text: "rgba(255, 255, 255, 0.88)"
  textSecondary: "rgba(255, 255, 255, 0.65)"
  textTertiary: "rgba(255, 255, 255, 0.45)"
  layout: "#000000"
  container: "#141414"
  elevated: "#1f1f1f"
  border: "#303030"
  fill: "rgba(255, 255, 255, 0.08)"
  bubble: "rgba(255, 255, 255, 0.12)"
typography:
  headline:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.3
  title:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: 1.3
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
  label:
    fontFamily: "-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica Neue, Arial, Noto Sans, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
rounded:
  inline: "4px"
  control: "6px"
  surface: "8px"
  chip: "12px"
  bubble: "18px"
  composer: "16px"
spacing:
  compact: "8px"
  default: "12px"
  section: "16px"
  controlInner: "8px"
components:
  antd-control:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    size: "32px"
  user-bubble:
    backgroundColor: "{colors.bubble}"
    textColor: "{colors.text}"
    rounded: "{rounded.bubble}"
    padding: "8px 12px"
  composer-shell:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.composer}"
    padding: "{spacing.compact}"
  composer-control:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    padding: "{spacing.controlInner}"
  file-chip:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "8px 12px"
  queued-messages:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "8px 12px"
  spoken-text:
    backgroundColor: "transparent"
    textColor: "{colors.textSecondary}"
    padding: "8px 0 0"
  operational-drawer-wide:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    width: "400px"
  operational-drawer-narrow:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    width: "320px"
  operational-status-chip:
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "0 8px"
    height: "24px"
  operational-detail-inset:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.surface}"
    padding: "{spacing.default}"
  admin-detail-label:
    textColor: "{colors.textSecondary}"
    width: "12rem"
  admin-version-drawer:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    width: "52rem"
---

# Design System: Agent Core

This file is lightweight MVP **presentation** guidance only. Screens, copy, and behavior stay in `/docs`. If this file conflicts with `/docs`, `/docs` wins.

## Overview

**Creative North Star: "Ant Design operate UI"**

Agent Core is a personal-chat operate surface plus a shipped Admin configuration surface. Both use Ant Design v6. Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT/Codex are UX and hierarchy references for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer with inference controls, in-flow activity)—not a component dependency or pixel clone. Admin uses the same dark roles, type, and 8/12/16 spacing for definitions and instances. It does not borrow a navy enterprise sider or copy Chat's composer onto configuration screens.

The shipped appearance is Ant Design `darkAlgorithm`: black layout, conversation container, elevated composer, and primary blue for Send and Voice-on. Product CSS maps those roles from `:root` custom properties; it does not invert a former light palette or invent a second token catalog.

**Key Characteristics:**

- Ant Design v6 imported directly in product components; `app.css` only sizes the shell, overflow, product layout, previews, and accessibility.
- Compact / default / section spacing (8 / 12 / 16px) owns shells, docks, and sibling `gap`. Text-control inner padding is `{spacing.controlInner}` (8px, Ant Design `paddingXS`, same as a session-row body).
- Composer owns Model (reasoning level inside the Model button when supported) with Attach/Voice and context-sensitive Stop/Queue/Send; header owns identity, Speech locale, compact icon actions, and status metadata.
- Model catalog rows expose enabled vision, reasoning, tools, and structured-output capabilities as compact named icons. Queued drafts remain a compact local work list above the composer.
- Assistant display Markdown stays primary for reading; persisted public speech text is a quieter **Spoken** inset first on the same turn when it differs (the TTS projection).
- Background Work and Schedules share an operational drawer language: quiet headers, semantic execution rows, semantic filled icon-and-text status chips, elevated detail insets, and trailing actions where needed.
- Admin collections use compact tables with local horizontal scrolling; Experience, Schedules and Thoughts expand one record at a time. Read-only details share aligned labels on wider screens and stacked labels on mobile.

- Admin uses contextual creation, sticky draft actions, shared conversation source selection, explicit time-zone previews and inline recovery on the same Ant Design baseline.

## Colors

Dark operate neutrals with one primary accent and one success accent for live microphone state.

### Primary
- **Ant Design primary** (`{colors.primary}`): Send, Voice-on (`aria-pressed`), focus rings, and selection tint. Use sparingly so the transcript stays readable.

### Secondary
- **Listening green** (`{colors.success}` / `{colors.successHover}`): microphone is actively capturing. Distinct from Voice mode (primary). Do not use green for Voice-on.

### Neutral
- **Layout** (`{colors.layout}`): rail and page chrome.
- **Container** (`{colors.container}`): conversation pane and header.
- **Elevated** (`{colors.elevated}`): composer shell, code, chips-on-dark.
- **Border** (`{colors.border}`): 1px hairlines (rail edge, header, composer, Spoken separator).
- **Fill** (`{colors.fill}`): session-row hover across the whole row including overflow.
- **Bubble** (`{colors.bubble}`): user message fill only.
- **Text / secondary / tertiary** (`{colors.text}`, `{colors.textSecondary}`, `{colors.textTertiary}`): body, meta/timestamps/Spoken body, Spoken caption and icons.

**The Role Surfaces Rule.** Map these dark roles in product CSS. Do not invert a light palette or add extra brand hues.

## Typography

**Display/Body Font:** system UI stack (San Francisco / Segoe UI / Roboto fallbacks). No self-hosted display face.

**Character:** Ant Design defaults. Status is written in type, not color alone.

### Hierarchy
- **Headline** (600, 28px desktop / 22px below 768px): empty-chat “What do you want to work on?”
- **Title** (600, 16px): Agent Core rail wordmark (line-height 1.4) and session header agent name (line-height 1.3).
- **Body** (400, 14px, line-height 1.57): conversation, composer field, rail titles. Reading column max width 52rem.
- **Label** (400, 12px): timestamps, header subtitle, connection/profile, Spoken caption. Spoken icon matches this size.

**The One Face Rule.** Do not self-host a display face or costume monospace except for code in Markdown.

## Layout

One session: black rail + conversation column + sticky composer. Column is `min(100%, 52rem)` centered with 16px inline padding. Conversation list gap is section (16px); inside a turn, compact (8px) owns sibling stacks (meta → body: speech projection when present, then display).

- Rail: 280px at 1200px+, 240px from 768–1199px, Drawer below 768px. Shared 16px left edge for New chat, Chats, session titles. Sections use title + gap, not dividing rules.
- Header: 56px CSS minimum, container background, compact block padding, 16px inline. At 1024px it renders as one 57px row including its hairline border. At 768px and 390px its Flex rows wrap intentionally according to available width; profile and connection status remain visible rather than being hidden.
- Composer dock: 12px above, 16px below the shell. Toolbar min-height 32px (44px below 768px); wrap with 8px gap.
- Queued drafts sit above the message well in a 12px-radius elevated container. Rows use an icon / truncated content / actions grid, 8px rhythm, and a 12.5rem maximum expanded list height.
- After a user send, leave about half the pane for the incoming reply; shrink as the reply grows. Historical turns stay compact.
- New chat empty: Identity + Speech locale in the intro stack (max 22rem). Model/Reasoning are not duplicated there.
- Paused: Resume replaces the composer. Ended: quiet ended note only. Model selection lives only in the composer, so neither paused nor ended shows a header Model control.
- Operational drawers are 400px wide when the host reports the wide layout and 320px otherwise. Their open rows stay visible rather than becoming nested cards; row and inset relationships use the same 8/12/16px rhythm. On narrow layouts, drawer action buttons have a 40px minimum height.
- Admin: same header height, container background, and 8/12/16 rhythm as Chat. Inventory and definition panels are configuration layouts, not a chat column. Content uses the available viewport width with 16px desktop and 12px mobile outer insets. Definitions, Instances, Event sources, and Credentials use collection tabs; names, identifiers, dates, status, and numeric metadata use separate table columns. Table rows keep one line with compact inline actions, right-aligned counts, and horizontal scrolling inside the table on narrower screens; long names/identifiers use ellipsis with full text available on hover. Definition lifecycle uses Versions and Drafts tabs with a shared base-version selector capped at 20rem and Create draft beside it, shrinking together on mobile. Immutable version inspection uses a right-side drawer with a persistent header, up to 52rem wide and full-width on mobile. Below 768px, admin grids collapse to one column, and the draft tabs wrap onto a second row with an underline on the active tab only. Skill cards use that same field grid after Instructions, with the requirements-not-grants line visible. At about 390px the definition form does not scroll sideways. Operator labels stay product words; do not copy Domain type names into the UI.
- Admin collection spacing has one owner: inventory toolbars use 12px block / 16px inline insets; toolbars inside a padded configuration panel add no second inset. Search fields cap at 28rem. Ant Design owns compact table cell padding; product CSS owns single-line summaries, ellipsis, tabular numerals and table-local overflow. Pagination uses 10/20/50 rows; expanded record content wraps and stays within the visible table width, including while the columns scroll horizontally.
- Admin detail labels use `{components.admin-detail-label.width}` at 768px and above, with top alignment and wrapping content. Below 768px labels stack above their values. The same layout applies to Experience observations/provenance, Schedule and Thought details, effective configuration, policy summaries, validation snapshots and version capabilities. Observation lists retain separate bullets and a 72ch maximum measure.

**The Docs Win Rule.** This file does not own Voice availability, Send/Queue/Stop behavior, or speech persistence. `/docs` does.

**The Compact Rhythm Rule.** Shells, docks, and sibling `gap` use compact/default/section (8 / 12 / 16px). Do not invent extra `--ac-space-*` steps or 2px CSS gaps.

**The Additive Inset Rule.** A padded shell owns the outer inset. Nested text controls keep their own inner padding (`{spacing.controlInner}` = 8px, Ant Design `paddingXS` via `theme.useToken()`, same as `.session-row-body`). Align labels by giving equivalent children the same inner padding. Example: composer `{spacing.compact}` (8px) + field/button `{spacing.controlInner}` (8px) so Message and Model text share one edge, while hover fill still has 8px around the glyph. Apply the same stack to the Model Dropdown overlay (shell 8px + 8px on title, catalog rows, and reasoning footer). Icon-only 32×32 toolbar hits stay padding 0.

Audit: Message placeholder left edge equals the Model name left edge; Model hover fill matches session-row inset and does not overlap the next control. Never zero a text button's padding to force alignment, and never use negative margin to grow a hover fill.

Ownership: `AdminCollectionToolbar` and `useAdminCollectionSearch` share collection search/pagination; `useAdminDetailLayout` shares responsive label alignment. `AdminSessionPicker` shares conversation browsing and manual entry across Memory and Experience; its stack owns compact sibling gaps. Memory owns the compact wrapping scope/action toolbar for IdentityUser and User; Session keeps its scope/source row and separate actions below the picker. `.admin-draft-actions` owns sticky draft chrome, spacing and borders; fields use scroll clearance beneath that bar. Product components compose Ant Design Table and Descriptions directly. These helpers are product layout policies, not a replacement component library.

## Elevation & Depth

Mostly flat tonal layering (layout → container → elevated → bubble/fill). One structural shadow is shared by the composer and its Model overlay.

Operational drawer details use the elevated tone, a border, and an 8px radius to distinguish approval previews, results, or schedule expressions without adding another shadow.

### Shadow Vocabulary
- **Composer lift** (`box-shadow: 0 6px 16px rgba(0, 0, 0, 0.45)`): sticky message well and its anchored Model overlay.

**The Flat-By-Default Rule.** Surfaces are flat at rest. Do not add card shadows inside transcript turns.

## Shapes

- **Inline** (4px): inline Markdown code and compact focus outlines.
- **Control** (6px): AntD buttons, Selects, 32px composer and header icon hits, status tags.
- **Surface** (8px): session rows, code blocks, and image previews.
- **Chip** (12px): attachment/file chips, queued-send container, and 24px operational status/time-zone chips.
- **Composer** (16px): message well and Model overlay.
- **Bubble** (18px): user turns only. Assistant content is unbubbled Markdown.

Hairline 1px `{colors.border}` separators. No colored 2px side rails, no glass.

## Components

### Conversational harness learning

- Teach durable knowledge and procedures in Chat. Automatic receipts say “saved for future conversations” and disclose relevant verification limits; current conversation pins remain unchanged.
- Reuse the existing centered Chat approval Modal for Assisted changes, instructions and tool proposals. Use “Save this harness change?”, complete escaped semantic change, source, version/policy context and future-Session semantics. When supplied, show Applies to in an Ant Design info notice and active version/policy revision in labeled compact tags above the review. Change uses a quiet fill inset, 12px padding and Ant Design large radius; supplied colon-ended headings are emphasized and dash lines use hanging indentation without rewriting text. One focusable Approval details region scrolls within 40vh for all remaining fields; title/context/footer stay outside. Semantic proposals cap at 720px; ordinary sensitive actions at 520px. The container caps to the dynamic viewport with 16px clearance; review content shrinks on short screens, and body scrolling retains access if context itself is oversized. Each approval identity resets review scroll to the opening line. The list owns 16px section gaps, each detail owns its 8px label/value gap, and the footer owns an 8px wrapping action gap. Preserve keyboard Enter/Escape and exact Approve/Reject decisions; narrow footer buttons have 40px minimum targets.
- Admin is governance and inspection: active version, Manual/Assisted/Managed, independent scopes, save policy and freeze. Keep recent changes/evidence collapsed. No primary purpose/source/eligible/preparation/publication form. An unfinished legacy candidate may be discarded through shared confirmation.
- Distinguish Core checks, Agent assessments, tested revisions, stale evidence and external limitations in inspection. Preserve published evidence after freeze. Use existing safe errors/DiagnosticDetails and do not replay stale approvals.
- Reuse established 8/12/16 spacing, 72ch evidence measure, wrapping action groups and incumbent dark Ant Design v6 tokens. Desktop, 768px and 390px cover approval, automatic receipt, failed/rejected change and frozen inspection.

### Buttons
- **Shape:** 6px radius; header icon actions are 32×32px. Composer Send/Attach/Voice are 32×32px and become 44px below 768px.
- **Primary:** Send/Queue and Voice-on use `{colors.primary}`.
- **Ghost/text:** Header actions, Attach, overflow, New chat, Model, catalog rows; hover uses `{colors.fill}` only. Suppress Ant Design text-button `::before`/`::after` rings so hover does not flash a border. Selected catalog row is a checkmark, not a persistent fill.
- **Success:** live microphone control uses `{colors.success}`.

### Chips
- Interrupted/failed: solid Ant Design Tag, not a full-width banner. Default tag marks the catalog default model in the Model menu.
- Operational state: filled Ant Design Tag with an icon and text, 24px minimum height, 8px inline padding, and 12px radius. Semantic color reinforces the label; it never replaces the icon and text.

### Cards / Containers
- Composer shell: elevated fill, 16px radius, compact 8px padding, 8px inner gap, 1px border, composer shadow.
- Queued-send container: elevated fill, 12px radius, 1px border; compact rows divide with a softened border and keep Steer/Remove actions at the trailing edge.
- File chip: elevated fill, 12px radius, 1px border, 8px × 12px padding. Image previews fit within 240×180px with an 8px radius.
- Do not nest a card inside each assistant message.

### Inputs / Fields
- Message: borderless textarea inside the composer; placeholder secondary text. Follow **The Additive Inset Rule** (shell `{spacing.compact}` + `{spacing.controlInner}` on the field).
- Model in the composer: one Ant Design `Dropdown` (not Modal, not a pair of Selects). The chip is a single Model text button: model name, optional Default tag, optional reasoning level as secondary text inside the same control (`aria-label="Reasoning"`, Codex-style, not a sibling button), then the chevron. The overlay lists models with compact, named capability icons for vision, reasoning, tools, and structured output; the default model carries a Default tag and the active row carries a check. When supported, a footer shows Reasoning, the current level, and a dotted slider. Overlay chrome matches the composer shell; title, rows, and footer use `{spacing.controlInner}` (8px, same as session rows). Model appears only in the composer; paused and ended sessions show no Model control.
- Identity and Speech locale: labeled AntD Selects.

### Navigation
- Session row hover, keyboard focus-within, and active state fill the whole row including overflow (24px icon, inside row padding, common right edge).
- Header: identity (agent name, 12px timestamp, and role), Speech locale, compact icon actions, and status metadata. The 32×32px actions use a 6px radius and `{colors.fill}` hover. Background Work uses an 18px inbox icon plus an attention badge; Schedules, Admin, and the direct confirmed End action use 16px icons. End opens the shared centered confirmation. Profile and conversation status remain visible. Credential inventory and binding controls belong in Admin; Chat has no application connection row. Header actions stay on the identity row. No Model appears in the live header.

### Admin collections and record details
- Wrapped Schedule/Thought field rows own their 16px sibling gap and external separation; their Form.Item children add no bottom margin. Effective model source describes saved configuration, while selection controls may contain an unsaved draft. Reasoning selectors always have a visible associated label.
- Leading panel messages use the existing 16px body inset without extra top margin. Secondary text following a direct sibling keeps the default 12px separation. Short timing/unit controls, scope filters, base-version toolbar choices, pagination and table model editors intentionally stay compact; draft and instance grids keep their field columns. Event source choices flex and wrap beside content-width actions.
- Policies & models separates Harness management, Execution defaults and Advanced registrations using the existing Admin panel composition. Record-editing forms (authoring/model, Schedule, Thought, Experience checkpoint, Event source and Credential metadata) share a 48rem maximum width; selects fill their field columns; evidence and registration tables retain available width. Save and Freeze share a wrapping action row, model fields have visible labels, and advanced review belongs in its section header.
- Admin form actions use content-width buttons in wrapping rows on desktop and mobile. Fields and upload targets may fill their container; action buttons do not stretch with them. The Admin content container owns a 40px minimum button height below 768px, including disabled and loading states.
- Collection introductions and creation actions follow the selected collection: New definition for Definitions, New instance for Instances, and the existing creation form within Event sources. Keep the shared header and tab structure.
- Draft Save / Publish / Delete actions stay in one sticky bar above the editor tabs, with a container-tone background, hairline separators and compact gaps. Its existing top inset is section spacing; the bottom inset is compact spacing. Keep wrapping action groups and the existing 40px minimum narrow-screen targets. Focused fields scroll clear of the bar.
- Memory and Experience reuse `AdminSessionPicker`, a product composition of direct Ant Design Select, Input, Button, Flex and Alert. Browse by readable conversation title/date; keep manual Session ID entry, refresh and older-page actions near the selector. Source identifiers wrap below the field. For IdentityUser and User, Memory scope, Load items and Reset scope share a control baseline on wider screens; the action pair wraps beneath the scope selector on narrow screens. Session scope keeps top-aligned scope/source labels and a separate action row below the conversation picker.
- Schedule run/end instants use Ant Design date/time pickers with the viewer time zone stated below the field and a readable preview below the timing group. Recurring end dates use date-only pickers; local time and named time zone remain separate fields. Interval fields reserve enough label width. Keep compact controls and section gaps; use native picker focus and keyboard affordances.
- Unavailable reads use an inline Ant Design error Alert with Retry and safe diagnostic details rather than an empty-result presentation. Keep failed-copy guidance inside the one-time credential dialog, near the selectable value. Visible field labels must be included in accessible control names.
- Visited instance tabs retain drafts while hidden polling pauses; returning refreshes the visible section. Preserve the existing tabs rather than remounting a form to stop requests. Eligibility, UTC conversion, ownership checks and polling behavior remain specified in `/docs`.
- Definitions, Instances, Event sources and versions use compact tables with searchable labels, column filters/sorting where offered, and shared pagination. Learned-memory and automation tables reuse the density and scrolling; their existing scope/load controls remain the entry point.
- Experience, Schedules and Thoughts use a searchable summary table with one expanded record at a time. Goal/task/prompt links expose the full title and expanded state; long summaries ellipsize. The narrow search toolbar includes a horizontal-scroll hint. Its clear affordance and existing no-match/empty states remain visible.
- Expanded records use bordered single-column Descriptions and wrapping trailing action groups, separated by the enclosing token gap. Observation bullets remain distinct. Provenance is a separate grid with the same label edge. Do not join observations into a dense punctuation-separated paragraph.
- Version inspection stays in the right-side drawer (`{components.admin-version-drawer.width}` maximum; viewport width on mobile), with 16px body padding, its own scroll container and persistent close control. Read-only values remain readable and copyable; select/switch/number controls retain their disabled presentation. Closing returns focus to the version link.
- Effective configuration and validation use the same responsive label/value layout. Long identifiers and fingerprints wrap inside their cells. Keep status names and diagnostics readable; semantic tags reinforce text rather than replacing it.
- Preserve the established forms, revisioned actions and confirmations described in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md). This section documents presentation, not operation eligibility or API limits.

### Operational drawers
- Background Work and Schedules use a two-line title: a strong title above a 12px secondary subtitle.
- Use open semantic execution rows and direct Ant Design schedule rows with 16px block padding and an 8px first-row top inset. The primary row heading and filled status chip share the top line and tolerate wrapped content.
- Detail insets use the elevated surface, a 1px border, 8px radius, 12px padding, and an 8px internal gap. Use them for implemented detail types such as approval previews, completed results, and schedule expressions; do not force every row field into an inset.
- Schedule metadata keeps the time-zone identifier in a compact chip and renders the next occurrence with `Intl.DateTimeFormat` using the viewer locale and the schedule time zone. Fall back to a readable local string when the named zone cannot be formatted.
- When a row has actions, rely on the row stack gap before trailing controls; align them to the trailing edge and allow wrapping. Current narrow-layout drawer actions use a 40px minimum target height.

### Conversation turns
- User: right-aligned bubble (`8px 12px`, 18px radius).
- Live user transcript: the same bubble at 92% opacity with a restrained dashed border; pulse only when reduced motion is not requested.
- Assistant: when public `speechText` meaningfully differs from display, show **Spoken** first (the TTS projection), then open sanitized Markdown, blocks, and files. Spoken is the same list item: speaker icon (decorative) + visible “Spoken” label (tertiary via CSS), body secondary with `pre-wrap`; 8px stack gap; 8px padding below a 1px border before on-screen detail. Not a second bubble, avatar, or timestamp.
- Application message: in the same reading column, before the assistant entry that shares its response. Agent speaker, visible “Still working” in the tertiary label color, Markdown body in secondary text, `data-role="applicationMessage"`, `aria-live="polite"`. Not a card, not Spoken, and not the activity row.

### Composer toolbar
- Left: Model (reasoning level inside the Model button when supported), Attach, Voice, microphone.
- Right: context-sensitive Stop / Queue / Send. With queued work above the shell, Steer and Remove stay row actions rather than joining the toolbar.
- Accessible names stay Model, Reasoning, Attach, Voice, Send, Stop.

## Do's and Don'ts

### Do:
- **Do** import `antd` in feature files; ConfigProvider uses `darkAlgorithm`. Keep Sider `theme="light"` so chat surfaces stay black.
- **Do** use compact/default/section (8/12/16px) for shells and sibling `gap`; use Ant Design `paddingXS` (8px, `{spacing.controlInner}`) via `theme.useToken()` for text-control inner padding so it matches session-row inset; align Spoken and composer toolbar to that rhythm.
- **Do** disclose a failed turn, work item, or admin error with one Ant Design popover, Error details, and a keyboard-reachable Copy control. Keep the Failed label. Show the popover only when a diagnostic id exists. Space the id and the copy control with `paddingXS`.
- **Do** put Model in the composer when the composer is shown (Reasoning level inside the Model button when supported); keep Identity/Speech locale in the new-chat intro; keep Speech locale in the live header.
- **Do** show enabled model capabilities as compact tooltip-backed icons in catalog rows; preserve the Default tag and selected-row check as separate signals.
- **Do** keep queued drafts above the composer in a compact local work list, with truncation, bounded expansion, and trailing Steer/Remove actions.
- **Do** compose Model/catalog rows with Ant Design `Button type="text"` and `Flex`; keep `app.css` for shell chrome, overflow, and ring suppression only.
- **Do** render Spoken only for assistant public `speechText` that differs after whitespace normalization; keep it first in the turn as the TTS projection, with Markdown as on-screen display below.
- **Do** honor `prefers-reduced-motion`; keep labeled errors, visible focus, and testids `connection` and `profile`.
- **Do** keep the labeled composer available while voice is live until `/docs` and tests change together.
- **Do** treat Admin as the same dark product: shared spacing tokens, configuration panels, and operator copy. Behavior stays in `/docs`.
- **Do** reuse Admin collection search/pagination and responsive detail alignment; keep table summaries compact, expanded content contained, and mobile labels stacked.
- **Do** use the shared centered Ant Design confirmation dialog (`confirmAction` in `web/src/app/confirmAction.ts`) for destructive or consequential actions across Chat, Admin, Background Work, and Automations (session delete, Admin definition/instance delete, deprecation, work cancel, approval decisions, memory reset, automation revoke, and similar). Prefer a stable `dialog` surface for tests and keyboard focus.
- **Do** keep header identity, Speech locale, compact icon actions, profile, and connection status visible. Use 32×32px, 6px-radius fill-hover actions; keep Background Work at an 18px inbox icon with attention badge and Automations/Admin/End at 16px.
- **Do** keep ended history on the same reading column with a quiet ended note, not a disabled input.
- **Do** reuse the operational drawer language for Background Work and Automations: quiet headers, semantic execution rows, semantic filled icon-and-text status chips, elevated 8px detail insets, and trailing actions only where the row exposes an operation.
- **Do** reuse the global Admin collection and per-instance binding layouts for System Credentials. Mask transient protected input with no reveal control; safe metadata and grant aliases use existing forms and tables. Keep browser state reset in its own section and event reactions in the shared Automation collection. Attention results use the words Needs attention plus an icon. Quiet completions do not change the header count.
- **Do** format schedule occurrences for the viewer locale in the schedule’s named time zone; keep the zone identifier visible beside the readable time.
- **Do** show run timestamps with the shared chat time formatter and a `time` element. Use readable origin labels (`Automation · Schedule`, `Automation · Event`, `Automation · Manual`) while retaining transport values internally.
- **Do** keep the unattended model and its effective-source sentence in Automation → Policies & models. Show a webhook credential once in a dialog, then clear it on Done or Escape. The resting Event source collection keeps public keys and status only; System Credential projections keep only safe metadata and policy.

- **Do** reuse AdminSessionPicker across Memory and Experience, align scope/source labels at the top, keep actions in a wrapping row, and preserve sticky draft actions with field scroll clearance. Date/time fields disclose the viewer zone; errors and copy recovery stay on their affected surface.

### Don't:
- **Don't** reproduce Pixel Dialogue Field, Obsidian Mint, Martian Mono, field textures, presence plates, or a custom Select.
- **Don't** add generic wrappers, Ant Design Pro/ProComponents/X, or another CSS framework.
- **Don't** use Layout.Sider `theme="dark"` (navy admin sider) or restyle Admin as a separate enterprise dashboard.
- **Don't** give equivalent Admin detail grids independent label widths, duplicate toolbar insets inside padded panels, or let wide tables expand the page.
- **Don't** treat this file as behavioral authority over `/docs`.
- **Don't** use a blocking Modal or a second Select/button for model or effort; keep one Dropdown anchored to the Model chip, with effort in the overlay footer.
- **Don't** render queued drafts as transcript turns or move pending attachments into a separate dock.
- **Don't** zero a text control’s padding, use negative margin, or stack extra child padding to fake alignment with a sibling.
- **Don't** show Spoken as another conversational turn or from internal generated tails.
- **Don't** wrap every operational List row in a card or generalize drawer actions to rows that do not expose an operation.
- **Don't** present schedule occurrence timestamps as raw transport strings when they parse as dates.
- **Don't** use transient `Popconfirm` overlays for lifecycle, deletion, approval, cancellation, reset, or revoke actions when `confirmAction` is available; reserve Popconfirm for low-risk inline affordances only.
- **Don't** add a browser panel, iframe, screenshot, or click log. Browser progress stays on the existing activity row as `Using browser…`.

### Instance automation and execution navigation

Instance tabs separate Identity & version, Continuity, Automation, Runs, Connections and Effective configuration. Nested sections use the same URL-backed Ant Design tab composition and retain visited form drafts. Configuration is a searchable compact collection with concise Name, readable When, lifecycle, next activation, last Run, model and creation provenance. Schedule and Event variants share the editor's Name/Instructions/model fields. Expanded record details retain the shared top-aligned desktop labels and stacked mobile layout.

Runs uses the compact Ant Design collection with Automation name, admitted Instructions, Run ID, Status/attention and Updated. Full text appears in native titles and the right-side Run details drawer; later configuration edits do not alter this historical display. Tables scroll locally while long drawer IDs/results wrap. Source controls expand/focus the exact configuration or Experience checkpoint without discarding drafts. Save returns focus to its source; deletion returns it to New automation. Close/Escape/mask restore the Run opener. Existing action groups wrap and retain at least 40px height below 768px. Safe errors and explicit retry stay inside the affected panel/drawer. No execution-authoring controls belong in Runs.

## Artifact cards

Published file cards reuse the file-chip elevated fill, 1px border, 12px radius and 8px × 12px padding. The enclosing block owns available width; the card caps at 32rem. A shrinking metadata column owns filename ellipsis and the secondary type/size line. The Ant Design Download/Retry button keeps its normal hit area, focus and busy state. Loading and failure use safe text; the complete filename remains accessible. No preview, extra shadow or workspace browsing surface is added.
