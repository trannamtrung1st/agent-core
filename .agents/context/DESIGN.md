---
name: Agent Core
description: Ant Design v6 dark operate UI for Chat and the shipped Admin configuration surface.
colors:
  primary: "#1668dc"
  primaryBg: "#15325b"
  error: "#dc4446"
  success: "#49aa19"
  successHover: "#6abe39"
  successBg: "#162312"
  text: "rgba(255, 255, 255, 0.85)"
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
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji', 'Segoe UI Symbol', 'Noto Color Emoji'"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.3
  title:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji', 'Segoe UI Symbol', 'Noto Color Emoji'"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: 1.3
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji', 'Segoe UI Symbol', 'Noto Color Emoji'"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
  label:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji', 'Segoe UI Symbol', 'Noto Color Emoji'"
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
    width: "100vw"
  operational-status-chip:
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "0 8px"
    height: "24px"
  background-work-drawer:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    width: "640px"
  background-session-title:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    padding: "0"
  agent-run-details:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
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

This file is lightweight MVP **presentation** guidance only. Screens, copy, and behavior stay in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md); `/docs` wins on conflicts. The current component source, `antdTheme`, `AppShell` and `app.css` supply the visual values recorded here. The [verification report](../../docs/reports/activation-agent-run-background-sessions-verification.md) records completed component, runtime, backend and exact-SHA hosted acceptance on `8cec78c5d47a43e0236a5c38f2e312f4e36ce283`.

## Overview

**Creative North Star: "Ant Design operate UI"**

Agent Core is a personal-chat operate surface plus a shipped Admin configuration surface. Both use Ant Design v6. Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT/Codex are UX and hierarchy references for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer with inference controls, in-flow activity)—not a component dependency or pixel clone. Admin uses the same dark roles, type, and 8/12/16 spacing for definitions and instances. It does not borrow a navy enterprise sider or copy Chat's composer onto configuration screens.

The shipped appearance is Ant Design `darkAlgorithm`: black layout, conversation container, elevated composer, and primary blue for Send and Voice-on. The theme seed remains `#1677ff`; Ant Design resolves the dark primary to `#1668dc`. `AppShell` maps resolved `theme.useToken()` roles to product CSS custom properties and publishes them on the document root so body portals (Dropdown, Drawer and Modal) share the same theme. Product CSS does not maintain independent color or spacing literals.

**Key Characteristics:**

- Ant Design v6 imported directly in product components; `app.css` only sizes the shell, overflow, product layout, previews, and accessibility.
- Compact / default / section spacing (8 / 12 / 16px) owns shells, docks, and sibling `gap`. Text-control inner padding is `{spacing.controlInner}` (8px, Ant Design `paddingXS`, same as a session-row body).
- Composer owns Model (reasoning level inside the Model button when supported) with Attach/Voice and context-sensitive Stop/Queue/Send; header owns identity, Speech locale, compact icon actions, and status metadata.
- Model catalog rows expose enabled vision, reasoning, tools, and structured-output capabilities as compact named icons. Queued drafts remain a compact local work list above the composer.
- Assistant display Markdown stays primary for reading; persisted public speech text is a quieter **Spoken** inset first on the same turn when it differs (the TTS projection).
- Background Work lists Sessions; shared Run details serve both Chat history and Admin inspection. Operational drawers use quiet headers, open rows, filled icon-and-text status chips, bounded reading regions and inline recovery. Automations retain their elevated schedule inset.
- Admin collections use compact tables with local horizontal scrolling; Experience and Automations expand one record at a time. Read-only details share aligned labels on wider screens and stacked labels on mobile.

- Admin uses contextual creation, sticky draft actions, shared conversation source selection, explicit time-zone previews and inline recovery on the same Ant Design baseline.

## Colors

Dark operate neutrals with one primary accent and one success accent for live microphone state.

### Primary
- **Ant Design primary** (`{colors.primary}`): primary actions, Voice-on (`aria-pressed`) and focus rings. Selection tint uses `{colors.primaryBg}`. Keep the transcript visually quiet.
- **Ant Design error** (`{colors.error}`): failure and destructive-action feedback; retain the visible error label and recovery control.

### Secondary
- **Listening green** (`{colors.success}` / `{colors.successHover}`): microphone is actively capturing; `{colors.successBg}` supplies its active fill. Completed operational status uses Ant Design semantic success. Voice-on uses primary.

### Neutral
- **Layout** (`{colors.layout}`): rail and page chrome.
- **Container** (`{colors.container}`): conversation pane and header.
- **Elevated** (`{colors.elevated}`): composer shell, code, chips-on-dark.
- **Border** (`{colors.border}`): 1px hairlines (rail edge, header, composer, Spoken separator).
- **Fill** (`{colors.fill}`): session-row hover across the whole row including overflow.
- **Bubble** (`{colors.bubble}`): user message fill only.
- **Text / secondary / tertiary** (`{colors.text}`, `{colors.textSecondary}`, `{colors.textTertiary}`): body, meta/timestamps/Spoken body, Spoken caption and icons.

**The Role Surfaces Rule.** `AppShell` owns the CSS bridge: primary → `colorPrimary`, success → `colorSuccess`, listening hover → `colorSuccessTextHover`, layout/container/elevated → `colorBgLayout`/`colorBgContainer`/`colorBgElevated`, border → `colorBorderSecondary`, fill → `colorFillTertiary`, bubble → `colorFillSecondary`. Error color uses `colorError`; selection uses `colorPrimaryBg`, and live microphone fill uses `colorSuccessBg`. The values above record the resolved dark theme, not a parallel palette. Map these dark roles in product CSS. Do not invert a light palette or add extra brand hues.

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
- Header: 56px CSS minimum, container background, compact block padding, 16px inline. The minimum height and compact block inset are shared with Admin; content can increase the height when controls wrap. At 768px and 390px its Flex rows wrap intentionally according to available width; profile and connection status remain visible rather than being hidden.
- Composer dock: 12px above, 16px below the shell. Toolbar min-height 32px (44px below 768px); wrap with 8px gap. On mobile, Model occupies the first row of the start group; input actions sit below it, and the trailing Send/Queue/Stop group aligns to that bottom row. Keep 44px composer icon hits.
- Queued drafts sit above the message well in a 12px-radius elevated container. Rows use an icon / truncated content / actions grid, 8px rhythm, and a 12.5rem maximum expanded list height.
- After a user send, leave about half the pane for the incoming reply; shrink as the reply grows. Historical turns stay compact.
- New chat empty: Identity + Speech locale in the intro stack (max 22rem). Model/Reasoning are not duplicated there.
- Paused: Resume replaces the composer. Ended: quiet ended note only. Model selection lives only in the composer, so neither paused nor ended shows a header Model control.
- Automations is 400px wide when the host reports the wide layout and full viewport width otherwise. Background work and Run details use up to 640px on wide layouts and full viewport width on narrow layouts. Their open rows stay visible rather than becoming nested cards; row and inset relationships use the same 8/12/16px rhythm. On narrow layouts, drawer action buttons have a 40px minimum height.
- Admin: same header height, container background, and 8/12/16 rhythm as Chat. Inventory and definition panels are configuration layouts, not a chat column. Content uses the available viewport width with 16px desktop and 12px mobile outer insets. Definitions and Instances use collection tabs; global Connections contains Credentials and Events; names, identifiers, dates, status, and numeric metadata use separate table columns. Prefer compact single-line summaries and inline actions, right-aligned counts, and table-local horizontal scrolling. Summary columns explicitly ellipsize with full-text titles; ordinary cells may wrap long values within their column rather than overlap a neighbor. Action columns reserve enough width for their complete button group. Definition lifecycle uses Versions and Drafts tabs with a shared base-version selector capped at 20rem and Create draft beside it, shrinking together on mobile. Immutable version inspection uses a right-side drawer with a persistent header, up to 52rem wide and full-width on mobile. Below 768px, admin grids collapse to one column, and the draft tabs wrap onto a second row with an underline on the active tab only. Skills use separate compact tables and the shared right-side Skill drawer, with the requirements-not-grants guidance visible. At about 390px the definition form does not scroll sideways. Operator labels stay product words; do not copy Domain type names into the UI.
- Admin collection spacing has one owner: inventory toolbars use 12px block / 16px inline insets; toolbars inside a padded configuration panel add no second inset. Search fields cap at 28rem. Ant Design owns compact table cell padding; product CSS owns bounded cell wrapping, summary ellipsis, tabular numerals, inline action alignment and table-local overflow. Pagination uses 10/20/50 rows; expanded record content wraps and stays within the visible table width, including while the columns scroll horizontally.
- Admin detail labels use `{components.admin-detail-label.width}` at 768px and above, with top alignment and wrapping content. Below 768px labels stack above their values. The same layout applies to Experience observations/provenance, Automation details, effective configuration, policy summaries, validation snapshots and version capabilities. Observation lists retain separate bullets and a 72ch maximum measure.

**The Docs Win Rule.** This file does not own Voice availability, Send/Queue/Stop behavior, or speech persistence. `/docs` does.

**The Compact Rhythm Rule.** `AppShell` maps compact/default/section to Ant Design `paddingXS`/`paddingSM`/`padding` (8 / 12 / 16px). Shells, docks, and sibling `gap` consume these roles. Composer action groups also use compact, including Queue/Send/Stop and queued-message actions. Do not invent extra `--ac-space-*` steps or 2px CSS gaps.

**The Additive Inset Rule.** A padded shell owns the outer inset. Nested text controls keep their own inner padding (`{spacing.controlInner}` = 8px, Ant Design `paddingXS` via `theme.useToken()`, same as `.session-row-body`). Align labels by giving equivalent children the same inner padding. Example: composer `{spacing.compact}` (8px) + field/button `{spacing.controlInner}` (8px) so Message and Model text share one edge, while hover fill still has 8px around the glyph. Apply the same stack to the Model Dropdown overlay (shell 8px + 8px on title, catalog rows, and reasoning footer). Icon-only 32×32 toolbar hits stay padding 0.

Inline link-style actions (`Button type="link"`) align flush with surrounding text and table-cell content: no inline padding or inline border. Keep native button semantics for local actions, Ant Design hover/disabled states, the shared primary keyboard focus outline, and existing vertical hit areas (including 40px Admin controls on mobile). This treatment does not apply to padded text controls such as Model options, session rows or toolbar actions. Existing `Typography.Link` navigation anchors retain their `href` and default inline presentation.

Audit: Message placeholder left edge equals the Model name left edge; Model hover fill matches session-row inset and does not overlap the next control. Never zero a text button's padding to force alignment, and never use negative margin to grow a hover fill.

Ownership: `AppShell` owns resolved colors, spacing, type and Ant Design radius aliases. `app.css` owns the product-specific chip/composer/bubble radii (12/16/18px), reading measure and responsive geometry. Inventory and definition panels share one surface/header/title rule; Chat and Admin headers both use compact block padding. A contained Versions collection owns sibling gaps and gives its toolbar no extra inset. `AdminCollectionToolbar` and `useAdminCollectionSearch` share collection search/pagination; `useAdminDetailLayout` shares responsive label alignment. `AdminSessionPicker` shares conversation browsing and manual entry across Memory and Experience; its stack owns compact sibling gaps. Memory owns the compact wrapping scope/action toolbar for IdentityUser and User; Session keeps its scope/source row and separate actions below the picker. `.admin-draft-actions` owns sticky draft chrome, spacing and borders; fields use scroll clearance beneath that bar. BackgroundWorkDrawer owns its 16px body inset; catalog rows own 12px inline padding, 16px block padding and the 8px first-row top inset. SessionRunHistory owns its 16px sibling gap; subsequent history sections own a hairline and 16px top padding. AgentRunDetails owns a 16px content stack and 8px status/action groups, with no catalog-row padding or outer inset. Admin Run inspection reuses that same composition. SessionArtifacts reuses ArtifactView; DrawerListFooter supplies cursor loading/retry states. Product components compose Ant Design Table and Descriptions directly. These helpers are product layout policies, not a replacement component library.

## Elevation & Depth

Mostly flat tonal layering (layout → container → elevated → bubble/fill). One structural shadow is shared by the composer and its Model overlay.

Automation schedule expressions retain an elevated inset with a border, 8px radius, 12px padding and 8px internal gap. Current Run outcomes, confirmed effects and approval previews use open labeled ReadingRegion sections inside AgentRunDetails. They add no card fill, border or outer padding; each text region is independently scrollable, keyboard-focusable and capped at `clamp(8rem, 24dvh, 16rem)`. Short content remains natural height. Run metadata uses direct compact Ant Design Descriptions for Model, Attempt and Updated. The drawer body owns the shared 16px outer inset.

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

Inline/control/surface radii map to Ant Design `borderRadiusSM`/`borderRadius`/`borderRadiusLG` through `AppShell`; product chip/composer/bubble radii have one CSS owner. Hairline 1px `{colors.border}` separators. No colored 2px side rails, no glass.

## Components

### Conversational harness learning

- Teach durable knowledge and procedures in Chat. Automatic receipts say “saved for future conversations” and disclose relevant verification limits; current conversation pins remain unchanged.
- Reuse the existing centered Chat approval Modal for Assisted changes, instructions and tool proposals. Use “Save this harness change?”, complete escaped semantic change, source, version/policy context and future-Session semantics. When supplied, show Applies to in an Ant Design info notice and active version/policy revision in labeled compact tags above the review. Change uses a quiet fill inset, 12px padding and Ant Design large radius; supplied colon-ended headings are emphasized and dash lines use hanging indentation without rewriting text. One focusable Approval details region scrolls within 40vh for all remaining fields; title/context/footer stay outside. Semantic proposals cap at 720px; ordinary sensitive actions at 520px. The container caps to the dynamic viewport with 16px clearance; review content shrinks on short screens, and body scrolling retains access if context itself is oversized. Each approval identity resets review scroll to the opening line. The list owns 16px section gaps, each detail owns its 8px label/value gap, and the footer owns an 8px wrapping action gap. Preserve keyboard Enter/Escape and exact Approve/Reject decisions; narrow footer buttons have 40px minimum targets.
- Admin is governance and inspection: active version, Manual/Assisted/Managed, independent scopes, save policy and freeze. Keep recent changes/evidence collapsed. No primary purpose/source/eligible/preparation/publication form. An unfinished legacy candidate may be discarded through shared confirmation.
- Distinguish Core checks, Agent assessments, tested revisions, stale evidence and external limitations in inspection. Preserve published evidence after freeze. Use existing safe errors/DiagnosticDetails and do not replay stale approvals.
- Reuse established 8/12/16 spacing, 72ch evidence measure, wrapping action groups and incumbent dark Ant Design v6 tokens. Desktop, 768px and 390px cover approval, automatic receipt, failed/rejected change and frozen inspection.

### Keyboard and control states

Ant Design owns control hover, pressed, disabled, loading and semantic states. Product text controls (Model chip/options, session links/actions and header icons) retain a shared 2px primary `:focus-visible` outline inside their hit area, including when hover resets remove default outlines. The composer shell shows a primary border while a nested control has focus. Disabled controls do not gain active or focus styling. State meanings stay visible in text or accessible labels; color does not carry them alone.

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
- Compact Chat list and row action menus omit unnecessary separators. Destructive items retain Ant Design danger styling and their existing confirmation. The Model overlay keeps its meaningful reasoning-footer boundary.
- Session row hover, keyboard focus-within, and active state fill the whole row including overflow (24px icon, inside row padding, common right edge).
- Header: identity (agent name, 12px timestamp, and role), Speech locale, compact icon actions, and status metadata. The 32×32px actions use a 6px radius and `{colors.fill}` hover. Background Work uses an 18px inbox icon plus an attention badge; Automations, Admin, and the direct confirmed End action use 16px icons. End opens the shared centered confirmation. Profile and conversation status remain visible. Credential inventory and binding controls belong in Admin; Chat has no application connection row. Header actions stay on the identity row. No Model appears in the live header.

### Admin collections and record details
- The enclosing collection owns search-to-content gaps and outer insets. `AdminCollectionToolbar` adds no padding inside section stacks or already padded panels; inventory cards alone give their direct search toolbar 12px block / 16px inline padding. Skills actions, search and grouped panels share the same content edge and 16px sibling gap. The mobile scroll hint keeps its own 8px gap below the search field.
- Prefer one-line table action groups using direct Ant Design `Flex`, the shared `admin-table-actions` class and `paddingXS` gap. Buttons and tags do not shrink; each feature owns adequate action-column and scroll widths. This applies to Skills, Definition versions, Events, Credentials and Workspace. Form, expanded-record and drawer actions still wrap when needed.
- Desktop Skill names, IDs, descriptions, provenance and capability summaries ellipsize with native full-text titles. Inspection drawers retain complete content. Below 768px, metadata remains in the Skill cell, Skill ID stays separate, and the table scrolls locally; those readable detail rows may be taller. Missing-capability warnings remain visible.
- Ordinary non-ellipsis cells use normal whitespace and anywhere wrapping; table links stay bounded to their cells. Automation Last run reserves 240px for the usual status/outcome label. No blanket nowrap rule may force text into adjacent columns.
- Wrapped Automation timing field rows own their 16px sibling gap and external separation; their Form.Item children add no bottom margin. Effective model source describes saved configuration, while selection controls may contain an unsaved draft. Reasoning selectors always have a visible associated label.
- Leading panel messages use the existing 16px body inset without extra top margin. Secondary text following a direct sibling keeps the default 12px separation. Short timing/unit controls, scope filters, base-version toolbar choices, pagination and table model editors intentionally stay compact; draft and instance grids keep their field columns. Event choices flex and wrap beside content-width actions.
- Policies & models separates Harness management, Execution defaults and Advanced registrations using the existing Admin panel composition. Record-editing forms (authoring/model, Automation, Experience checkpoint, Event and Credential metadata) share a 48rem maximum width; selects fill their field columns; evidence and registration tables retain available width. Save and Freeze share a wrapping action row, model fields have visible labels, and advanced review belongs in its section header.
- Admin form actions use content-width buttons in wrapping rows on desktop and mobile. Fields and upload targets may fill their container; action buttons do not stretch with them. The Admin content container owns a 40px minimum button height below 768px, including disabled and loading states.
- Collection introductions and creation actions follow the selected collection: New definition for Definitions, New instance for Instances, and the New Event drawer within global Connections → Events. Keep the shared header and tab structure.
- Draft Save / Publish / Delete actions stay in one sticky bar above the editor tabs, with a container-tone background, hairline separators and compact gaps. Its existing top inset is section spacing; the bottom inset is compact spacing. Keep wrapping action groups and the existing 40px minimum narrow-screen targets. Focused fields scroll clear of the bar.
- Memory and Experience reuse `AdminSessionPicker`, a product composition of direct Ant Design Select, Input, Button, Flex and Alert. Browse by readable conversation title/date; keep manual Session ID entry, refresh and older-page actions near the selector. Source identifiers wrap below the field. For IdentityUser and User, Memory scope, Load items and Reset scope share a control baseline on wider screens; the action pair wraps beneath the scope selector on narrow screens. Session scope keeps top-aligned scope/source labels and a separate action row below the conversation picker.
- Schedule run/end instants use Ant Design date/time pickers with the viewer time zone stated below the field and a readable preview below the timing group. Recurring end dates use date-only pickers; local time and named time zone remain separate fields. Interval fields reserve enough label width. Keep compact controls and section gaps; use native picker focus and keyboard affordances.
- Unavailable reads use an inline Ant Design error Alert with Retry and safe diagnostic details rather than an empty-result presentation. Keep failed-copy guidance inside the one-time credential dialog, near the selectable value. Visible field labels must be included in accessible control names.
- Visited instance tabs retain drafts while hidden polling pauses; returning refreshes the visible section. Preserve the existing tabs rather than remounting a form to stop requests. Eligibility, UTC conversion, ownership checks and polling behavior remain specified in `/docs`.
- Definitions, Instances, Events and versions use compact tables with searchable labels, column filters/sorting where offered, and shared pagination. Learned-memory and automation tables reuse the density and scrolling; their existing scope/load controls remain the entry point.
- Experience and Automations use a searchable summary table with one expanded record at a time. Goal/name links expose the full title and expanded state; long summaries ellipsize. The narrow search toolbar includes a horizontal-scroll hint. Its clear affordance and existing no-match/empty states remain visible.
- Expanded records use bordered single-column Descriptions and wrapping trailing action groups, separated by the enclosing token gap. Observation bullets remain distinct. Provenance is a separate grid with the same label edge. Do not join observations into a dense punctuation-separated paragraph.
- Version inspection stays in the right-side drawer (`{components.admin-version-drawer.width}` maximum; viewport width on mobile), with 16px body padding, its own scroll container and persistent close control. Read-only values remain readable and copyable; select/switch/number controls retain their disabled presentation. Closing returns focus to the version link.
- Effective configuration and validation use the same responsive label/value layout. Long identifiers and fingerprints wrap inside their cells. Keep status names and diagnostics readable; semantic tags reinforce text rather than replacing it.
- Preserve the established forms, revisioned actions and confirmations described in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md). This section documents presentation, not operation eligibility or API limits.

### Operational drawers
- Background Work has a single title, Background work; selecting a Session changes the title to its original task name. Introductory copy belongs in the body. The Automations drawer retains its own title/subtitle composition.
- Catalog rows are semantic list items for Sessions. A wrapping level-five heading presents the complete Session name beside its initial Run’s filled status chip, aligned with row content without button padding or hover fill. Origin, Updated and bounded file/attempt count share a wrapping metadata row. The two-line result/failure preview uses normal text color; progress/unread attention and a wrapping 8px action row follow.
- View original result is the single explicit action that enters Session detail; the name is not interactive. All background Sessions is a compact, start-aligned Back control with 8px inner padding. History navigation moves focus to Back, then restores View original result on return; polling does not move focus.
- Source Automation navigation uses an inline Typography.Link in the action group. The selected Session shows origin, optional report-back copy, Continue in chat, View Automation when applicable, original file count, original Run details, expandable Conversation run history and Original task files. Continued in chat is secondary text; Open chat replaces Continue when ChatList is present. Continue in chat is the full-width primary action in this detail view; catalog actions wrap at their natural width. Unavailable Sessions or archived owners keep history available with continuation disabled and a readable explanation.
- Continue failures stay in an inline Ant Design Alert with recovery guidance. Loading disables repeated continuation. A successful live, paused or read-only ended opening closes the drawer to reveal the conversation. Eligibility and same-Session behavior remain owned by `/docs`.
- AgentRunDetails is shared by SessionRunHistory and Admin Run inspection. It shows status/turn kind/attention, optional progress, Model/Attempt/Updated, retry time, outcome, confirmed effects, exact approval preview and safe diagnostics. Labels name the displayed outcome: Response, Result, No action or Needs attention. A quiet No action retains readable copy without an attention badge.
- Outcome, confirmed effects and approval preview retain complete escaped, wrapping text in separate labeled focusable regions. Each uses natural short-content height, a `clamp(8rem, 24dvh, 16rem)` scroll cap, 72ch text measure and the shared primary focus outline. Keep these sections open; schedule-expression insets remain specific to Automations.
- Approve action and Reject action share a wrapping 8px control row; expiry has visible copy and disables both decisions. Cancel run stays content-width and start-aligned. Consequential actions use the shared centered confirmation, retain inline stale/error feedback and expose loading/disabled states.
- Run history and Files retain their own heading, loading, empty, inline error/retry and bounded-page footer. Files reuse the same ArtifactView Download/Retry card as conversation history. Failed older-page loads keep the existing content visible.
- Background Work and Run details cap at 640px on wide layouts and use full viewport width below 768px. Their body owns 16px padding; narrow drawer buttons retain at least 40px height. Automations remains 400px on wide layouts and full viewport width below 768px with its existing schedule rows and elevated expression inset.
- Schedule metadata keeps its time-zone chip and formats the next occurrence for the viewer locale in the schedule's named zone, with a readable fallback.

### Conversation turns
- User: right-aligned bubble (`8px 12px`, 18px radius).
- Live user transcript: the same bubble at 92% opacity with a restrained dashed border; pulse only when reduced motion is not requested.
- Assistant: when public `speechText` meaningfully differs from display, show **Spoken** first (the TTS projection), then open sanitized Markdown, blocks, and files. Spoken is the same list item: speaker icon (decorative) + visible “Spoken” label (tertiary via CSS), body secondary with `pre-wrap`; 8px stack gap; 8px padding below a 1px border before on-screen detail. Not a second bubble, avatar, or timestamp.
- Application message: in the same reading column, before the assistant entry that shares its response. Agent speaker, visible “Still working” in the tertiary label color, Markdown body in secondary text, `data-role="applicationMessage"`, `aria-live="polite"`. Not a card, not Spoken, and not the activity row.

### Composer toolbar
- Left: Model (reasoning level inside the Model button when supported), Attach, Voice, microphone.
- Right: context-sensitive Stop / Queue / Send. With queued work above the shell, Steer and Remove stay row actions rather than joining the toolbar.
- Accessible names stay Model, Reasoning, Attach, Voice, Send, Stop.

### Instance automation and execution navigation

Instance tabs separate Identity & version, Skills, Continuity, Automation, Runs, Credentials and Effective configuration. Nested sections use the same URL-backed Ant Design tab composition and retain visited form drafts. Configuration is a searchable compact collection with concise Name, Destination, readable When, lifecycle, next activation, last Run, model and creation provenance. Schedule and Event variants share Name/Instructions and explicit Run in controls. Existing conversations use the shared owned eligible Session picker and pinned model; separate background Sessions use unattended model controls and a distinct None/Selected conversation report picker. Tools/Vision requirements stay explicit. Expanded record details retain the shared top-aligned desktop labels and stacked mobile layout.

Runs uses the bounded compact Ant Design collection with Run ID, readable Turn, Status and Updated. Run links open the exact AgentRun in the right-side Run details drawer, using the shared AgentRunDetails composition. The current collection does not show an Automation-name or admitted-Instructions column. Tables scroll locally while long drawer IDs/results wrap. Source controls expand/focus the exact configuration or Experience checkpoint without discarding drafts. Save returns focus to its source; deletion returns it to New automation. Close/Escape/mask restore the Run opener. Existing action groups wrap and retain at least 40px height below 768px. Safe errors and explicit retry stay inside the affected panel/drawer. No execution-authoring controls belong in Runs.

### Artifact cards

Published file cards reuse the file-chip elevated fill, 1px border, 12px radius and 8px × 12px padding. The enclosing block owns available width; the card caps at 32rem. A shrinking metadata column owns filename ellipsis and the secondary type/size line. The Ant Design Download/Retry button keeps its normal hit area, focus and busy state. Loading and failure use safe text; the complete filename remains accessible. No preview, extra shadow or workspace browsing surface is added.

## Do's and Don'ts

### Do:
- **Do** import `antd` in feature files; ConfigProvider uses `darkAlgorithm`. Keep Sider `theme="light"` so chat surfaces stay black.
- **Do** use compact/default/section (8/12/16px) for shells and sibling `gap`; use Ant Design `paddingXS` (8px, `{spacing.controlInner}`) via `theme.useToken()` for text-control inner padding so it matches session-row inset; align Spoken and composer toolbar to that rhythm.
- **Do** disclose a failed turn, AgentRun, or admin error with one Ant Design popover, Error details, and a keyboard-reachable Copy control. Keep the Failed label. Show the popover only when a diagnostic id exists. Space the id and the copy control with `paddingXS`.
- **Do** put Model in the composer when the composer is shown (Reasoning level inside the Model button when supported); keep Identity/Speech locale in the new-chat intro; keep Speech locale in the live header.
- **Do** show enabled model capabilities as compact tooltip-backed icons in catalog rows; preserve the Default tag and selected-row check as separate signals.
- **Do** keep queued drafts above the composer in a compact local work list, with truncation, bounded expansion, and trailing Steer/Remove actions.
- **Do** compose Model/catalog rows with Ant Design `Button type="text"` and `Flex`; keep `app.css` for shell chrome, overflow, and ring suppression only.
- **Do** render Spoken only for assistant public `speechText` that differs after whitespace normalization; keep it first in the turn as the TTS projection, with Markdown as on-screen display below.
- **Do** honor `prefers-reduced-motion`; keep labeled errors, visible focus, and testids `connection` and `profile`.
- **Do** keep the labeled composer available while voice is live until `/docs` and tests change together.
- **Do** treat Admin as the same dark product: shared spacing tokens, configuration panels, and operator copy. Behavior stays in `/docs`.
- **Do** keep table action buttons together with sufficient column width and local scrolling; retain wrapping for forms and expanded details.
- **Do** reuse Admin collection search/pagination and responsive detail alignment; keep table summaries compact, expanded content contained, and mobile labels stacked.
- **Do** use the shared centered Ant Design confirmation dialog (`confirmAction` in `web/src/app/confirmAction.ts`) for destructive or consequential actions across Chat, Admin, Background Work, and Automations (session delete, Admin definition/instance delete, deprecation, run cancellation, approval decisions, memory reset, automation revoke, and similar). Prefer a stable `dialog` surface for tests and keyboard focus.
- **Do** keep header identity, Speech locale, compact icon actions, profile, and connection status visible. Use 32×32px, 6px-radius fill-hover actions; keep Background Work at an 18px inbox icon with attention badge and Automations/Admin/End at 16px.
- **Do** keep ended history on the same reading column with a quiet ended note, not a disabled input.
- **Do** reuse AgentRunDetails across Chat history and Admin inspection, keeping one outer drawer inset, semantic filled status chips, open bounded reading regions and content-width consequential controls. Automations retains its elevated schedule inset.
- **Do** reuse the global Admin collection and per-instance binding layouts for System Credentials. Mask transient protected input with no reveal control; safe metadata and grant aliases use existing forms and tables. Keep browser state reset in its own section and event reactions in the shared Automation collection. Attention results use the words Needs attention plus an icon. Quiet completions do not change the header count.
- **Do** format schedule occurrences for the viewer locale in the schedule’s named time zone; keep the zone identifier visible beside the readable time.
- **Do** show updated timestamps with the shared chat time formatter and a `time` element. Background Session origins use readable labels (Immediate task, Automation, Application event, Manual task); Run turn kinds use Chat turn, Scheduled task, Completion report and the other implemented labels. Keep transport values internal.
- **Do** keep the unattended model and its effective-source sentence in Automation → Policies & models. Show a webhook credential once in a dialog, then clear it on Done or Escape. The resting Event collection keeps public keys and status only; System Credential projections keep only safe metadata and policy.

- **Do** preserve keyboard focus across Background Session history navigation and keep continuation failures/recovery visible inside the drawer.
- **Do** reuse AdminSessionPicker across Memory and Experience, align scope/source labels at the top, keep actions in a wrapping row, and preserve sticky draft actions with field scroll clearance. Date/time fields disclose the viewer zone; errors and copy recovery stay on their affected surface.

### Don't:
- **Don't** reproduce Pixel Dialogue Field, Obsidian Mint, Martian Mono, field textures, presence plates, or a custom Select.
- **Don't** add generic wrappers, Ant Design Pro/ProComponents/X, or another CSS framework.
- **Don't** use Layout.Sider `theme="dark"` (navy admin sider) or restyle Admin as a separate enterprise dashboard.
- **Don't** let unbounded table text overlap adjacent cells or stack action buttons merely because an action column is too narrow.
- **Don't** give equivalent Admin detail grids independent label widths, duplicate toolbar insets inside padded panels, or let wide tables expand the page.
- **Don't** treat this file as behavioral authority over `/docs`.
- **Don't** use a blocking Modal or a second Select/button for model or effort; keep one Dropdown anchored to the Model chip, with effort in the overlay footer.
- **Don't** render queued drafts as transcript turns or move pending attachments into a separate dock.
- **Don't** zero a text control’s padding, use negative margin, or stack extra child padding to fake alignment with a sibling.
- **Don't** show Spoken as another conversational turn or from internal generated tails.
- **Don't** wrap every operational row in a card, apply catalog-row padding inside shared Run details, truncate Background Session names with Admin table styles, or generalize actions to rows without an operation.
- **Don't** present schedule occurrence timestamps as raw transport strings when they parse as dates.
- **Don't** use transient `Popconfirm` overlays for lifecycle, deletion, approval, cancellation, reset, or revoke actions when `confirmAction` is available; reserve Popconfirm for low-risk inline affordances only.
- **Don't** add a browser panel, iframe, screenshot, or click log. Browser progress stays on the existing activity row as `Using browser…`.

### Automation destination and delivery presentation

Use the shared AutomationDestination and CompletionDeliveryStatus composition. Destination and Reports to are separate compact secondary lines with exact Conversation links; Originally created from remains provenance. Chat Automations links to the exact Admin record. Background Work shows Not requested, Ready, In use, Handled in conversation, Pending · message queued, Reported, Could not report or Not reported and keeps unavailable/quiet results inspectable. Parent callback provenance is modest inline Background work completed metadata and an owned child link on the ordinary assistant turn. Preserve 8/12/16px rhythm, Ant Design theme, 16px drawer body, persistent Admin editor footer and 40px narrow actions. Long title/status/action groups wrap; collection tables scroll locally.

A bounded Synthetic Playwright MCP review at 1440×900, 768×900 and 390×844 covers the conditional report picker, exact source link, Initiative-off report and truthful quiet child status. Resize capture waits for the existing drawer animation; full-mobile Background Work is confirmed at x=0,width=390. See [enhancement evidence](../../docs/reports/automation-targets-background-reportback-verification.md). Previous milestone evidence remains historical.


## Result handoff and wait presentation

Reuse CompletionDeliveryStatus and AgentRunDetails in Chat/Admin. Ready and In use are accounting states; Handled in conversation links the exact parent Run with View handling run and removes unread attention. Waiting uses a clock plus typed condition/deadline and Cancel, separate from retry/approval. Keep existing 640px drawers, full width below 768px, 16px inset and wrapping controls. Synthetic MCP review covers 1440×900, 768×900, 390×844; evidence is in docs/reports/assets/durable-completion-inbox.

Shared Event authoring retains the established Admin drawer/footer and one-time credential dialog. Event details use the existing responsive descriptions layout and genuine subscriber/delivery tables; detail action groups wrap while collection actions remain inline. Automation’s existing drawer separates General, Trigger, Execution and applicable Completion; Advanced collapses model/effort and tool/vision requirements. Nested Event management preserves the Automation draft and returns focus to its initiating control. Searchable catalog and timezone selectors use direct Ant Design grouping/filtering, with the same category order for Authorized and Always available capabilities.
