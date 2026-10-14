---
name: Agent Core
description: Ant Design v6 dark operate UI for Chat and the shipped Admin configuration surface.
colors:
  primary: "#1668dc"
  primaryBg: "#15325b"
  focus: "#3c89e8"
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
  tight: "4px"
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
  composer-chip:
    backgroundColor: "{colors.bubble}"
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "2px 8px"
  composer-picker:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    rounded: "{rounded.chip}"
    padding: "{spacing.compact}"
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
  admin-configuration-panel:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    typography: "{typography.body}"
    rounded: "{rounded.surface}"
    padding: "{spacing.section}"
  admin-owner-actions:
    backgroundColor: "{colors.container}"
    textColor: "{colors.text}"
    rounded: "{rounded.surface}"
    padding: "{spacing.section}"
  file-preview:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    typography: "{typography.body}"
    rounded: "{rounded.surface}"
    width: "90vw"
  admin-configuration-form:
    textColor: "{colors.text}"
    typography: "{typography.body}"
    width: "48rem"
---

# Design System: Agent Core

This file records the current Ant Design v6 presentation system. Screens, copy and behavior stay in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md); `/docs` wins on conflicts. Resolved values come from `antdTheme`, `AppShell`, `app.css` and the product components, rather than an independent runtime token system.

Synchronized against source `d279abfe` (2026-10-14). The [recent Git and UI review](../../docs/reports/recent-git-ui-design-review-2026-10-14.md) covers the 22 commits since the preview polish baseline `8e053018`, shared attachment/artifact cards, compact pending uploads, FortuneSheet previews, Drive approvals and credential creation. It records focused local checks and the original XLSX clipping-notice and Drive keyboard-rejection findings; the [integrated verification](../../docs/reports/opensandbox-integration-verification.md#current-main-integration--2026-10-14) records both corrections. Historical reports retain their original source, hosted acceptance and manual verification gaps; this refresh adds no milestone acceptance.

## Overview

**Creative North Star: "Ant Design operate UI"**

Agent Core is a personal-chat operate surface plus a shipped Admin configuration surface. Both use Ant Design v6. Impeccable refines layout, spacing, composition, hierarchy, and polish while preserving AntD primitives. ChatGPT/Codex are UX and hierarchy references for the conversational experience (quiet session rail, centered reading column, user bubbles, open assistant Markdown, bottom composer with inference controls, in-flow activity)—not a component dependency or pixel clone. Admin uses the same dark roles, type, and 8/12/16 spacing for definitions and instances. It does not borrow a navy enterprise sider or copy Chat's composer onto configuration screens.

The shipped appearance is Ant Design `darkAlgorithm`: black layout, conversation container, elevated composer, and primary blue for Send and Voice-on. The theme seed remains `#1677ff`; Ant Design resolves the dark primary to `#1668dc`. `AppShell` maps resolved `theme.useToken()` roles to product CSS custom properties and publishes them on the document root so body portals (Dropdown, Drawer and Modal) share the same theme. Product CSS does not maintain independent color or spacing literals.

Shared source-owned previews inherit the same dark surfaces, typography and spacing; the document remains the focus. Source collections own their action rows, the preview owns filename/metadata/toolbar and a locally scrolling canvas, and bounded dialogs keep Download, Close and local recovery reachable on short screens. Document paper stays white inside the isolated Word renderer. No parallel file-management shell, theme or UI kit is introduced.

**Key Characteristics:**

- Ant Design v6 imported directly in product components; `app.css` only sizes the shell, overflow, product layout, previews, and accessibility.
- Compact / default / section spacing (8 / 12 / 16px) owns shells, docks, and sibling `gap`. Text-control inner padding is `{spacing.controlInner}` (8px, Ant Design `paddingXS`, same as a session-row body).
- Composer owns Model (reasoning level inside the Model button when supported) with Attach/Voice and context-sensitive Stop/Queue/Send; header groups role, provider profile and live status beside identity, with Speech locale and compact icon actions in a stable trailing group.
- Model catalog rows expose enabled vision, reasoning, tools, and structured-output capabilities as compact named icons. Queued drafts remain a compact local work list above the composer.
- Assistant display Markdown stays primary for reading; persisted public speech text is a quieter **Spoken** inset first on the same turn when it differs (the TTS projection).
- Background Work lists Sessions; shared Run details serve both Chat history and Admin inspection. Operational drawers use quiet headers, open rows, filled icon-and-text status chips, bounded reading regions and inline recovery. Automations retain their elevated schedule inset.
- Admin collections use compact tables with local horizontal scrolling; Experience and Automations expand one record at a time. Read-only details share aligned labels on wider screens and stacked labels on mobile.

- Admin uses contextual creation, one shared sticky configuration action frame, shared conversation source selection, explicit time-zone previews and inline recovery on the same Ant Design baseline.
- `AdminShell` and the typed registry own focused icon-and-label pages. Shared panels retain one inset; exact-owner controllers retain drafts across pages. Immutable inspectors keep their local read-only controls.
- Structured composer choices use category icons and quiet type labels in one flat list; draft, queue and history share immutable chip labels, with pending and historical tooltips distinguished.
- Shared file previews keep code/grids locally scrollable, mobile controls at 40px, worksheet selection stable through resizing and full metadata accessible. Drive connections and browsing reuse existing Credentials and Instance owners; file mutations remain authorized agent capabilities.

## Colors

Dark operate neutrals with one primary accent and one success accent for live microphone state.

### Primary
- **Ant Design primary** (`{colors.primary}`): primary actions and Voice-on (`aria-pressed`). Selection tint uses `{colors.primaryBg}`. Product text controls and picker choices use `{colors.focus}` (Ant Design `colorPrimaryTextHover`) for their shared inset keyboard-focus outline. Keep the transcript visually quiet.
- **Ant Design error** (`{colors.error}`): failure and destructive-action feedback; retain the visible error label and recovery control.

### Secondary
- **Listening green** (`{colors.success}` / `{colors.successHover}`): microphone is actively capturing; `{colors.successBg}` supplies its active fill. Completed operational status uses Ant Design semantic success. Voice-on uses primary.

### Neutral
- **Layout** (`{colors.layout}`): rail and page chrome.
- **Container** (`{colors.container}`): conversation pane and header.
- **Elevated** (`{colors.elevated}`): composer shell, code, chips-on-dark.
- **Border** (`{colors.border}`): 1px hairlines (rail edge, header, composer, Spoken separator).
- **Fill** (`{colors.fill}`): session-row hover across the whole row including overflow.
- **Bubble** (`{colors.bubble}`): user message fill. Structured chips and active picker choices consume the same Ant Design `colorFillSecondary` role directly.
- **Text / secondary / tertiary** (`{colors.text}`, `{colors.textSecondary}`, `{colors.textTertiary}`): body, meta/timestamps/Spoken body, Spoken caption and icons.

**The Role Surfaces Rule.** `AppShell` owns the CSS bridge: primary → `colorPrimary`, success → `colorSuccess`, listening hover → `colorSuccessTextHover`, layout/container/elevated → `colorBgLayout`/`colorBgContainer`/`colorBgElevated`, border → `colorBorderSecondary`, fill → `colorFillTertiary`, bubble → `colorFillSecondary`. Error color uses `colorError`; selection uses `colorPrimaryBg`, and live microphone fill uses `colorSuccessBg`. The values above record the resolved dark theme, not a parallel palette. Map these dark roles in product CSS. Do not invert a light palette or add extra brand hues.

## Typography

**Display/Body Font:** system UI stack (San Francisco / Segoe UI / Roboto fallbacks). No self-hosted display face.

**Character:** Ant Design defaults. Status is written in type, not color alone.

### Hierarchy
- **Headline** (600, 28px desktop / 22px below 768px): empty-chat “What do you want to work on?”. Admin task headings use a local 28px / 24px responsive variant, without changing the Chat role.
- **Title** (600, 16px): Agent Core rail wordmark (line-height 1.4) and session header agent name (line-height 1.3). Admin branding uses 16px on wide screens and 14px below 768px, beside a secondary label-sized Admin marker.
- **Body** (400, 14px, line-height 1.57): conversation, composer field, rail titles. Structured editor/history text uses the local 1.85 line-height variant to accommodate wrapped inline chips; queue previews remain single-line. Reading column max width 52rem.
- **Label** (400, 12px): timestamps, header subtitle, connection/profile, Spoken caption. Spoken icon matches this size.

**The One Face Rule.** Do not self-host a display face or costume monospace except for code in Markdown.

## Layout

One session: black rail + conversation column + sticky composer. Column is `min(100%, 52rem)` centered with 16px inline padding. Conversation list gap is section (16px); inside a turn, compact (8px) owns sibling stacks (meta → body: speech projection when present, then display).

- Rail: 280px at 1200px+, 240px from 768–1199px, Drawer below 768px. Shared 16px left edge for New chat, Chats, session titles. Sections use title + gap, not dividing rules.
- Header: 56px CSS minimum, container background, compact block padding and 16px inline padding (12px below 768px). Name and timestamp lead; role, provider profile and live status share one secondary row with compact gaps. At 1200px and above, a shrinking identity column sits beside an intrinsic-width actions column with a default 12px gap. Below 1200px, actions occupy a deliberate second row aligned to the end. Changing status text never determines control placement. The minimum height and compact block inset remain shared with Admin.
- Composer dock: 12px above, 16px below the shell. Toolbar min-height 32px (44px below 768px); wrap with 8px gap. On mobile, Model occupies the first row of the start group; input actions sit below it, and the trailing Send/Queue/Stop group aligns to that bottom row. Keep 44px composer icon hits.
- Queued drafts sit above the message well in a 12px-radius elevated container. Rows use an icon / truncated content / actions grid, 8px rhythm, and a 12.5rem maximum expanded list height.
- After a user send, leave about half the pane for the incoming reply; shrink as the reply grows. Historical turns stay compact.
- New chat empty: Identity + Speech locale in the intro stack (max 22rem). Model/Reasoning are not duplicated there.
- Paused: Resume replaces the composer. Ended: quiet ended note only. Model selection lives only in the composer, so neither paused nor ended shows a header Model control.
- Automations is 400px wide when the host reports the wide layout and full viewport width otherwise. Background work and Run details use up to 640px on wide layouts and full viewport width on narrow layouts. Their open rows stay visible rather than becoming nested cards; row and inset relationships use the same 8/12/16px rhythm. On narrow layouts, drawer action buttons have a 40px minimum height.
- Admin: shared dark header and 8/12/16 rhythm. Global and contextual sidebar links replace collection-wide tabs; immutable inspectors retain their local read-only controls. Main content uses 16px desktop and 12px mobile outer insets. One inventory page heading contains its count and creation action; the collection owns search, compact rows and local scrolling. Numeric metadata stays in its column; ordinary cells wrap safely and bounded summaries retain full-text titles. Definition lifecycle uses Versions/Drafts destinations and a 20rem base-version selector beside Create draft. Immutable inspection remains in a 52rem/full-mobile Drawer.
- Admin collection spacing has one owner: inventory toolbars use 12px block / 16px inline insets; toolbars inside a padded configuration panel add no second inset. Search fields cap at 28rem. Ant Design owns compact table cell padding; product CSS owns bounded cell wrapping, summary ellipsis, tabular numerals, inline action alignment and table-local overflow. Pagination uses 10/20/50 rows; expanded record content wraps and stays within the visible table width, including while the columns scroll horizontally.
- Admin detail labels use `{components.admin-detail-label.width}` at 768px and above, with top alignment and wrapping content. Below 768px labels stack above their values. The same layout applies to Experience observations/provenance, Automation details, effective configuration, policy summaries, validation snapshots and version capabilities. Observation lists retain separate bullets and a 72ch maximum measure.

**The Docs Win Rule.** This file does not own Voice availability, Send/Queue/Stop behavior, or speech persistence. `/docs` does.

**The Compact Rhythm Rule.** `AppShell` maps compact/default/section to Ant Design `paddingXS`/`paddingSM`/`padding` (8 / 12 / 16px). Shells, docks, and sibling `gap` consume these roles. Composer action groups also use compact, including Queue/Send/Stop and queued-message actions. The existing tight role maps to native `paddingXXS` (4px) for the narrow Admin brand gap, header/action gaps below 360px, and the shared preview metadata relationship between source/size and MIME. It does not replace normal shell, panel or control spacing. Do not invent further spacing steps or 2px CSS gaps.

**The Additive Inset Rule.** A padded shell owns the outer inset. Nested text controls keep their own inner padding (`{spacing.controlInner}` = 8px, Ant Design `paddingXS` via `theme.useToken()`, same as `.session-row-body`). Align labels by giving equivalent children the same inner padding. Example: composer `{spacing.compact}` (8px) + field/button `{spacing.controlInner}` (8px) so Message and Model text share one edge, while hover fill still has 8px around the glyph. Apply the same stack to the Model Dropdown overlay (shell 8px + 8px on title, catalog rows, and reasoning footer). Icon-only 32×32 toolbar hits stay padding 0.

Inline link-style actions (`Button type="link"`) align flush with surrounding text and table-cell content: no inline padding or inline border. Keep native button semantics for local actions, Ant Design hover/disabled states, the shared primary keyboard focus outline, and existing vertical hit areas (including 40px Admin controls on mobile). This treatment does not apply to padded text controls such as Model options, session rows or toolbar actions. Existing `Typography.Link` navigation anchors retain their `href` and default inline presentation.

Audit: Message placeholder left edge equals the Model name left edge; Model hover fill matches session-row inset and does not overlap the next control. Never zero a text button's padding to force alignment, and never use negative margin to grow a hover fill.

Ownership: `AppShell` owns resolved colors, spacing, type and Ant Design radius aliases. `app.css` owns the product-specific chip/composer/bubble radii (12/16/18px), reading measure and responsive geometry. `AgentConfigurationPanel` owns recurring Admin section shells, wrapping heading actions and optional token-based body stacks; inventory panels share its surface/header/title rule; Chat and Admin headers both use compact block padding. A contained Versions collection owns sibling gaps and gives its toolbar no extra inset. `AdminCollectionToolbar` and `useAdminCollectionSearch` share collection search/pagination; `useAdminDetailLayout` shares responsive label alignment. `AdminSessionPicker` shares conversation browsing and manual entry across Memory and Experience; its stack owns compact sibling gaps. Memory owns the compact wrapping scope/action toolbar for IdentityUser and User; Session keeps its scope/source row and separate actions below the picker. `.admin-owner-actions` owns the shared sticky Definition/Instance frame: container fill, 1px border, surface radius, section inset and compact sibling gap. Feature action groups wrap within it; fields use the existing focus/anchor clearance. BackgroundWorkDrawer owns its 16px body inset; catalog rows own 12px inline padding, 16px block padding and the 8px first-row top inset. SessionRunHistory owns its 16px sibling gap; subsequent history sections own a hairline and 16px top padding. AgentRunDetails owns a 16px content stack and 8px status/action groups, with no catalog-row padding or outer inset. Admin Run inspection reuses that same composition. SessionArtifacts reuses ArtifactView; DrawerListFooter supplies cursor loading/retry states. Product components compose Ant Design Table and Descriptions directly. These helpers are product layout policies, not a replacement component library.

### Heading supporting text

Supporting heading text follows its title in the same left-aligned, wrapping row with the compact 8px gap (Ant Design `paddingXS` / `--ac-space-compact`). Counts, revisions, copyable draft IDs and status/attention labels stay beside the title; long content wraps in document order. Heading action groups retain their existing separate placement. Numeric table cells retain their column alignment.

Admin inventory/draft counts, Persona/Lifecycle metadata, draft title/ID, Settings markers and attention labels reuse this arrangement. Background Work and Automations place their Run/status label after the wrapping task title. The row owns the gap; titles do not grow to push metadata to the opposite edge. Existing panel heading/body insets remain unchanged.

The [heading metadata verification](../../docs/reports/heading-metadata-layout-verification.md) records local Admin interactions at 1440/768/767/390px and focused component checks. Chat drawers have component-test evidence only for this change; historical hosted acceptance remains separate.

## Elevation & Depth

Mostly flat tonal layering (layout → container → elevated → bubble/fill). One structural shadow is shared by the composer and its Model overlay. The structured choice picker consumes Ant Design `boxShadowSecondary` for overlay elevation.

Automation schedule expressions retain an elevated inset with a border, 8px radius, 12px padding and 8px internal gap. Current Run outcomes, confirmed effects and approval previews use open labeled ReadingRegion sections inside AgentRunDetails. They add no card fill, border or outer padding; each text region is independently scrollable, keyboard-focusable and capped at `clamp(8rem, 24dvh, 16rem)`. Short content remains natural height. Run metadata uses direct compact Ant Design Descriptions for Model, Attempt and Updated. The drawer body owns the shared 16px outer inset.

### Shadow Vocabulary
- **Composer lift** (`box-shadow: 0 6px 16px rgba(0, 0, 0, 0.45)`): sticky message well and its anchored Model overlay.

**The Flat-By-Default Rule.** Surfaces are flat at rest. Do not add card shadows inside transcript turns.

## Shapes

- **Inline** (4px): inline Markdown code and compact focus outlines.
- **Control** (6px): AntD buttons, Selects, 32px composer and header icon hits, status tags.
- **Surface** (8px): session rows, code blocks, and image previews.
- **Chip** (12px): structured `/Skill` and `@resource` chips, attachment/file chips, queued-send container, and 24px operational status/time-zone chips.
- **Composer** (16px): message well and Model overlay.
- **Bubble** (18px): user turns only. Assistant content is unbubbled Markdown.

Inline/control/surface radii map to Ant Design `borderRadiusSM`/`borderRadius`/`borderRadiusLG` through `AppShell`; product chip/composer/bubble radii have one CSS owner. Hairline 1px `{colors.border}` separators. No colored 2px side rails, no glass.

## Components

### Conversational harness learning

- Teach durable knowledge and procedures in Chat. Automatic receipts say “saved to this Instance for its next Run” and disclose relevant verification limits; current Run pins remain unchanged.
- Reuse the existing centered Chat approval Modal for Assisted changes, instructions and tool proposals. Use “Save this harness change?”, complete escaped semantic change, source, version/policy context and next-Run semantics (including the same Session). When supplied, show Applies to in an Ant Design info notice and active version/policy revision in labeled compact tags above the review. Change uses a quiet fill inset, 12px padding and Ant Design large radius; supplied colon-ended headings are emphasized and dash lines use hanging indentation without rewriting text. One focusable Approval details region scrolls within 40vh for all remaining fields; title/context/footer stay outside. Semantic proposals cap at 720px; ordinary sensitive actions at 520px. The container caps to the dynamic viewport with 16px clearance; review content shrinks on short screens, and body scrolling retains access if context itself is oversized. Each approval identity resets review scroll to the opening line. Semantic review lists own 16px section gaps, each stacked detail owns its 8px label/value gap, and the footer owns an 8px wrapping action gap. Ordinary sensitive actions reuse small one-column Ant Design Descriptions: secondary 6rem labels align complete wrapping plain-text values; empty supplied fields show a muted dash with the accessible name Not provided. This compact variant fits the 520px dialog without hiding recipients or changing the larger semantic review. Preserve keyboard Enter/Escape and exact Approve/Reject decisions; narrow footer buttons have 40px minimum targets.
- Admin is governance and inspection: active version, Manual/Assisted/Managed, independent scopes, save policy and freeze. Keep recent changes/evidence collapsed. No primary purpose/source/eligible/preparation/publication form. An unfinished legacy candidate may be discarded through shared confirmation.
- Distinguish Core checks, Agent assessments, tested revisions, stale evidence and external limitations in inspection. Preserve published evidence after freeze. Use existing safe errors/DiagnosticDetails and do not replay stale approvals.
- Reuse established 8/12/16 spacing, 72ch evidence measure, wrapping action groups and incumbent dark Ant Design v6 tokens. Desktop, 768px and 390px cover approval, automatic receipt, failed/rejected change and frozen inspection.

### Keyboard and control states

Ant Design owns control hover, pressed, disabled, loading and semantic states. Product text controls (Model chip/options, session links/actions and header icons) retain a shared 2px primary `:focus-visible` outline inside their hit area, including when hover resets remove default outlines. The composer shell retains its neutral border and the editor has no nested focus outline. Picker choices use the visible active/focus fill; the focused editor or picker search identifies the active choice. Ant Design search retains its normal focus state. Disabled controls do not gain active or focus styling. State meanings stay visible in text or accessible labels; color does not carry them alone.

### Chat header

The existing `ChatHeader` owns identity and action composition; `ChatApp` supplies the projected profile/status and Admin action. Name and timestamp remain the primary row. Role, profile and the labeled status dot use 12px secondary type and compact 8px sibling gaps beneath it. The role takes its natural width up to 40% of that row; status consumes remaining space. Long role/status labels ellipsize with full-text tooltips instead of pushing actions away. Missing role still leaves profile/status visible. Speech locale uses the existing small Ant Design Select; Background work, Automations, End and Admin retain the shared 32px icon hits, hover fill and inset keyboard outline.

The header shell owns outer insets; the identity/actions grid owns the 12px major gap; secondary and action rows own their 8px gaps. Product CSS owns the fixed 1200px transition, shrinking columns and text containment. Preserve this stable grouping for empty, live, reconnecting, paused and ended chats; action availability remains governed by the frontend specification. [Header verification](../../docs/reports/chat-header-status-review.md) records local Synthetic interactions and 320–1440px geometry checks.

### Buttons
- **Shape:** 6px radius; header icon actions are 32×32px. Composer Send/Attach/Voice are 32×32px and become 44px below 768px.
- **Primary:** Send/Queue and Voice-on use `{colors.primary}`.
- **Ghost/text:** Header actions, Attach, overflow, New chat, Model, catalog rows; hover uses `{colors.fill}` only. Suppress Ant Design text-button `::before`/`::after` rings so hover does not flash a border. Selected catalog row is a checkmark, not a persistent fill.
- **Success:** live microphone control uses `{colors.success}`.

### Chips
Skill invocation and resource chips use the same readable text token and subtle neutral fill, without decorative borders. The shared `.composer-chip` owns this foreground across the Lexical draft, queue and user history; previews retain the same role. Normal-state measured text contrast is 8.75:1 in draft/queue and 7.06:1 in history after background compositing.

- Interrupted/failed: solid Ant Design Tag, not a full-width banner. Default tag marks the catalog default model in the Model menu.
- Operational state: filled Ant Design Tag with an icon and text, 24px minimum height, 8px inline padding, and 12px radius. Semantic color reinforces the label; it never replaces the icon and text.

### Cards / Containers
- Composer shell: elevated fill, 16px radius, compact 8px padding, 8px inner gap, 1px border, composer shadow.
- Queued-send container: elevated fill, 12px radius, 1px border; compact rows divide with a softened border and keep Steer/Remove actions at the trailing edge.
- File chip: elevated fill, 12px radius, 1px border, 8px × 12px padding. Image previews fit within 240×180px with an 8px radius.
- Do not nest a card inside each assistant message.

### Inputs / Fields
- Message: borderless headless Lexical plain-text editor inside the composer, with a labeled multiline textbox and Ant Design placeholder tone. Follow **The Additive Inset Rule** (shell `{spacing.compact}` + `{spacing.controlInner}` on the field). The editor keeps a 40px minimum and 224px maximum height with local scrolling; it introduces no second UI kit or rich-text formatting toolbar.
- Model in the composer: one Ant Design `Dropdown` (not Modal, not a pair of Selects). The chip is a single Model text button: model name, optional Default tag, optional reasoning level as secondary text inside the same control (`aria-label="Reasoning"`, Codex-style, not a sibling button), then the chevron. The overlay lists models with compact, named capability icons for vision, reasoning, tools, and structured output; the default model carries a Default tag and the active row carries a check. When supported, a footer shows Reasoning, the current level, and a dotted slider. Overlay chrome matches the composer shell; title, rows, and footer use `{spacing.controlInner}` (8px, same as session rows). Model appears only in the composer; paused and ended sessions show no Model control.
- Identity and Speech locale: labeled AntD Selects.

### Structured composer parts and choices

The same message well mixes ordinary text with atomic `/Skill` invocation and `@resource` reference chips. Inline chips use the existing 12px chip radius, Ant Design neutral fill/text roles and wrapping text; `/` and `@` distinguish invocation and reference chips. Drafts, queued drafts and user history reuse one product chip presentation and immutable labels. Chips keep 8px inline padding, no border and 1.85 line height in the editor/history so wrapped labels do not crowd neighboring lines; queued previews remain single-line. Typed tooltips identify resources and distinguish pending activation from historical invocation. Pending attachments remain in the composer shell as compact icon/filename tiles with full filename tooltips, accessible icon-only Remove and Retry, and local progress/errors. The responsive grid scrolls vertically within 30vh/12rem; pending thumbnails are not generated. Existing Ant Design controls and 8px spacing roles own the tile/list insets. Sent files and artifacts reuse the shared `ChatFileCard` presentation below; authenticated source reads and original downloads retain their existing ownership.

Slash/at-sign input and the Add content menu's Use Skill / Reference resource actions share one caret-anchored picker. Its elevated Ant Design surface has a 12px radius, 8px shell inset, viewport-bounded width up to 640px, capped to the composer and available viewport. It chooses above or below the caret according to available space; when neither side fits the controls plus a complete choice, it overlaps the caret within viewport bounds. The list owns local scrolling while heading, search and recovery controls remain visible; rows retain their 40px minimum height (44px below 768px). Selection, editor resize, viewport resize and scrolling reposition the overlay. Direct Ant Design Input, Button, Spin, Empty and Alert provide search, loading, empty and inline Retry states. Pagination is automatic as the choice list reaches the bottom; no Load more button is shown. Choice rows keep the name and muted metadata on one line, with the category label consistently aligned to the row’s trailing edge. Names use their natural content width capped at 50% of the row body and do not shrink under metadata pressure. Metadata follows the actual name width with an 8px gap and shrinks first within the remaining space before the category. A logical auto margin anchors the category even when metadata is absent. File paths use middle ellipsis preserving their last 12 characters, including the extension; other names and metadata use end ellipsis. Full details remain available through accessible option names and hover titles. The picker stack owns the 8px gap between heading, search, list and recovery states; each row owns its 8px inner inset. Resources share one flat list with category icons, quiet type labels aligned to the trailing edge and independently retained source cursors; slash Skills reuse the same rows; Close uses the Ant Design icon. The choice list exposes selected/unavailable states, arrow-key and Enter selection, outside-click, focus-leave, window-blur and Escape dismissal and polite result/loading announcements. The editor and composer border remain neutral while focused, without nested blue outlines. Picker buttons and choice rows have 44px minimum heights below 768px.

The [single-row verification](../../docs/reports/composer-single-row-verification.md) records this successor layout and its local Synthetic checks. Earlier two-line captures remain historical evidence. This is a local extension of the Operate system. Existing palette, typography, spacing primitives and 52rem reading column remain unchanged. Picker geometry does not create a new global spacing or typography scale. Product meaning, immutable part identity and send/queue behavior remain owned by `/docs`.

The [frontend design review](../../docs/reports/frontend-design-system-review.md) checks the current natural-width names, trailing category labels and borderless chips against the running Synthetic app. Keyboard focus on a choice is distinguished from the active selection by the same 2px inset outline used by product text controls. AppShell publishes Ant Design `colorPrimaryTextHover` as the shared focus role, keeping the outline legible on selected dark rows. The editor's neutral focus treatment applies only to the editing well. The preview mirrors the corrected choice outline.

Composer picker accessibility follows [Frontend](../../docs/13-frontend-implementation-spec.md#unified-structured-composer): the focused editor or plus search owns the active-option association, and empty results do not name a missing option. Known metadata-only files use the existing muted choice-description style. A bounded search with remaining pages gives continuation guidance before showing an exhausted empty state; no additional visual tokens are introduced.

Structured chips preserve immutable presentation labels across draft, queue and history. Skill-only sending, trigger deletion, title semantics and historical activation boundaries remain governed by [Frontend](../../docs/13-frontend-implementation-spec.md#unified-structured-composer) and [Backend](../../docs/12-backend-implementation-spec.md); presentation labels never replace canonical identities. [MessageParts](../../web/src/features/chat/MessageParts.tsx) owns the shared chip presentation, and [ComposerChipNode](../../web/src/features/chat/ComposerChipNode.ts) applies it to the atomic editor node.

### Navigation
- Compact Chat list and row action menus omit unnecessary separators. Destructive items retain Ant Design danger styling and their existing confirmation. The Model overlay keeps its meaningful reasoning-footer boundary.
- Session row hover, keyboard focus-within, and active state fill the whole row including overflow (24px icon, inside row padding, common right edge).
- Header: identity (agent name, 12px timestamp, and role), Speech locale, compact icon actions, and status metadata. The 32×32px actions use a 6px radius and `{colors.fill}` hover. Background Work uses an 18px inbox icon plus an attention badge; Automations, Admin, and the direct confirmed End action use 16px icons. End opens the shared centered confirmation. Profile and conversation status remain visible. Credential inventory and binding controls belong in Admin; Chat has no application connection row. At 1200px+, actions share the identity row; below 1200px, they use the stable trailing second row. No Model appears in the live header.

### Definition permission hierarchy

The existing Definition Form presents Schedule and Events with nested Built-in Events and Webhook Events using Ant Design v6 checkboxes, mixed parent state, paddingLG nesting and token-based spacing. Child permissions derive from the canonical candidate; a local parent off/on restores restricted choices. Read-only versions retain accurate disabled controls and overall-disabled guidance. Summaries and publish diffs name exact family grants. Backend identifiers and independent authority are unchanged. Product behavior belongs to [Frontend](../../docs/13-frontend-implementation-spec.md#unified-events-catalog-and-automation-editor); no additional visual system or permission state is introduced.

### Shared Admin configuration compositions

`AgentConfigurationPanel` in [AgentConfigurationLayout.tsx](../../web/src/features/admin/AgentConfigurationLayout.tsx) is the recurring section shell for Definition authoring and immutable inspection, Instance configuration and global Connections. Its container uses `{components.admin-configuration-panel.backgroundColor}`, a 1px structural border and `{components.admin-configuration-panel.rounded}` corners. The heading and body each own one `{components.admin-configuration-panel.padding}` inset. Headings have a 56px minimum height, a 16px title, 12px secondary description and compact gap; title/actions wrap within the same edge.

**The Single Stack Owner Rule.** Use `bodyGap="compact"`, `"default"` or `"section"` for token-owned sibling gaps (8/12/16px). Direct Typography children have no block margin inside these stacks. Leave `bodyGap` unset for a single table or a form that owns its layout. Add heading actions through `extra`; feature owners retain accessible labels, busy/disabled states, errors and commands. A child toolbar inside the padded body adds no second outer inset.

The typed Admin registry and `AdminShell` own workspace navigation. Desktop uses a 248px container-tone sidebar at 1024px+, independent sidebar/main scrolling, a shared header, readable breadcrumb and one main page title. Below 1024px a labeled menu control opens the 280px Ant Design Drawer; Escape and dismissal restore its opener. Context replaces global links with grouped exact-owner destinations and explicit collection/back links. Native anchors use current-page fill, visible focus and subtle text for unsaved state. Search Admin has a bounded local destination index, scoped/global ownership copy and an accessible empty state. No horizontal page-wide tabs duplicate the rail. `RetainedAdminPage` lazily mounts then retains private editor state for the same owner; immutable version inspectors retain their local read-only tab compositions.

`AdminPageHeading` owns the single task title, adjacent inventory count and wrapping creation actions. `AdminOwnerHeading` keeps selected Instance/Definition identity secondary: the name uses Ant Design fontSizeLG (16px), metadata uses fontSizeSM (12px), and status/actions share the compact row. Full owner names remain in the main area; breadcrumbs ellipsize the owner with its full title available. On narrow screens, the header uses two deliberate rows: Agent Core/Admin beside 40px Menu/Search/Chat/Return icon controls above the single-line scope breadcrumb. Current task text remains in the main page title. All three trailing actions have zero inner padding at mobile widths and retain their full accessible names. The navigation Drawer body adds no second inset: navigation owns 12px outside, links own 8px inside, and group/link gaps use the existing 8/16px roles. Overview destinations are grouped by the same registry categories. Search results name the exact current owner once, then identify Global Admin separately; item descriptions show the destination rather than repeating the owner on every row.

Full-width `AgentConfigurationPanel` surfaces keep one 16px heading/body inset. Ordinary forms are bounded to 48rem; operating instructions fills its panel. Field grids pair related controls at 768px+ and stack below. Execution budgets and Unattended model each have their own focused edit page; Models and Providers remain separate. Shared setting drafts survive page navigation and expose source/unsaved copy. Browser privacy uses a global Platform panel and the same bounded form, never a second editor inside Instance Effective configuration. Use 8/12/16px sibling gaps, 40px narrow controls (including header/navigation/close actions), local table scrolling, wrapped IDs/status text, the shared sticky configuration action frame and focus/anchor scroll clearance. Check 1440/1024/768/767/390/320px as one batched rendered pass; fix findings together and perform at most one visual confirmation round. Runtime behavior checks remain separate. See [shared composition guidance](../../web/src/features/admin/README.md) and [Frontend Implementation](../../docs/13-frontend-implementation-spec.md) for ownership.

Resource collections reuse `ResourceSelectionToolbar` for a wrapping compact selection/action row and `ResourceDeletionContent` for the bounded, scrollable path list in the shared confirmation dialog. `ResourceImportFields` owns the shared import table: one Path / Kind / Size / Status heading row, vertically centered desktop cells, and stacked rows below 768px. Instance Add allows an 880px drawer; edit/inspection retain 640px, each capped to the viewport with a persistent footer and 16px body inset. Keep untouched validation neutral and visible recovery inline according to the frontend specification. Format policy remains in Backend Implementation.

### Admin collections and record details

Definition Versions and Drafts use a 16px parent stack between creation guidance, controls and the active collection. Collection headings align with the controls; their own 12px stack separates heading, search and table. Draft editor sections use parent gaps without extra horizontal rules or section-top padding around already bordered panels. Capabilities owns its panel and does not render the unrelated candidate/settings accordion.
- The enclosing collection owns search-to-content gaps and outer insets. `AdminCollectionToolbar` adds no padding inside section stacks or already padded panels; inventory cards alone give their direct search toolbar 12px block / 16px inline padding. Skills actions, search and grouped panels share the same content edge and 16px sibling gap. The mobile scroll hint keeps its own 8px gap below the search field.
- Prefer one-line table action groups using direct Ant Design `Flex`, the shared `admin-table-actions` class and `paddingXS` gap. Buttons and tags do not shrink; each feature owns adequate action-column and scroll widths. This applies to Skills, Definition versions, Credentials and Workspace. Events uses the same compact gap for icon View and More actions, a 112px action column and a 950px table scroll width; its secondary operations stay in the direct Ant Design Dropdown. Form, expanded-record and drawer actions still wrap when needed.
- Desktop Skill names, IDs, descriptions, provenance and capability summaries ellipsize with native full-text titles. Inspection drawers retain complete content. Below 768px, metadata remains in the Skill cell, Skill ID stays separate, and the table scrolls locally; those readable detail rows may be taller. Missing-capability warnings remain visible.
- Ordinary non-ellipsis cells use normal whitespace and anywhere wrapping; table links stay bounded to their cells. Automation Last run reserves 240px for the usual status/outcome label. No blanket nowrap rule may force text into adjacent columns.
- Wrapped Automation timing field rows own their 16px sibling gap and external separation; their Form.Item children add no bottom margin. Effective model source describes saved configuration, while selection controls may contain an unsaved draft. Reasoning selectors always have a visible associated label.
- Leading panel messages use the existing 16px body inset without extra top margin. In a panel with `bodyGap`, the parent owns the compact/default/section gap and direct Typography margins are cleared. In an unstacked panel, secondary text following a direct sibling keeps the default 12px separation. Short timing/unit controls, scope filters, base-version toolbar choices, pagination and table model editors intentionally stay compact; draft and instance grids keep their field columns. Event choices flex and wrap beside content-width actions.
- Self-management owns Agent management authority and reviewed shared changes; Automations, Trigger restrictions and Unattended model have separate destinations using the existing Admin panel composition. Record-editing forms (authoring/model, Automation, Experience checkpoint, Event, Credential metadata and screenshot privacy) use the shared `{components.admin-configuration-form.width}` maximum width. The `admin-config-form` layout fills available width up to that bound, gives Form.Item fields 16px bottom spacing and supporting Typography a 72ch maximum measure. Selects fill their field columns; evidence and registration tables retain available width. Pending configuration uses the shared global actions; consequential Freeze/Unfreeze remains an explicit confirmed command. Model fields have visible labels, and advanced review belongs in its section header.
- Admin form actions use content-width buttons in wrapping rows on desktop and mobile. Fields and upload targets may fill their container; action buttons do not stretch with them. The Admin content container owns a 40px minimum button height below 768px, including disabled and loading states.
- Collection introductions and creation actions follow the selected collection: New definition for Definitions, New instance for Instances, and the New Event drawer within global Connections → Events. Keep the shared header and canonical sidebar structure.
- Definition Save / Publish / Delete and Instance Save / Discard / Refresh use the same sticky panel above the editor pages. `.admin-owner-actions` owns a container-tone fill, 1px border, surface radius, one section inset on every side and compact gaps. Action groups wrap at natural button widths and retain 40px narrow targets; status/recovery stays readable below or beside them. Focused fields scroll clear of the panel.
- Memory and Experience reuse `AdminSessionPicker`, a product composition of direct Ant Design Select, Input, Button, Flex and Alert. Browse by readable conversation title/date; keep manual Session ID entry, refresh and older-page actions near the selector. Source identifiers wrap below the field. For IdentityUser and User, Memory scope, Load items and Reset scope share a control baseline on wider screens; the action pair wraps beneath the scope selector on narrow screens. Session scope keeps top-aligned scope/source labels and a separate action row below the conversation picker.
- Schedule run/end instants use Ant Design date/time pickers with the viewer time zone stated below the field and a readable preview below the timing group. Recurring end dates use date-only pickers; local time and named time zone remain separate fields. Interval fields reserve enough label width. Keep compact controls and section gaps; use native picker focus and keyboard affordances.
- Unavailable reads use an inline Ant Design error Alert with Retry and safe diagnostic details rather than an empty-result presentation. Keep failed-copy guidance inside the one-time credential dialog, near the selectable value. Visible field labels must be included in accessible control names.
- Visited Instance pages retain drafts while hidden polling pauses; returning refreshes the visible section. Stable exact-owner workspaces retain forms while the route controls visibility. Eligibility, UTC conversion, ownership checks and polling behavior remain specified in `/docs`.
- Definitions, Instances, Events and versions use compact tables with searchable labels, column filters/sorting where offered, and shared pagination. Learned-memory and automation tables reuse the density and scrolling; their existing scope/load controls remain the entry point.
- Experience and Automations use a searchable summary table with one expanded record at a time. Goal/name links expose the full title and expanded state; long summaries ellipsize. The narrow search toolbar includes a horizontal-scroll hint. Its clear affordance and existing no-match/empty states remain visible.
- Expanded records use bordered single-column Descriptions and wrapping trailing action groups, separated by the enclosing token gap. Observation bullets remain distinct. Provenance is a separate grid with the same label edge. Do not join observations into a dense punctuation-separated paragraph.
- Version inspection stays in the right-side drawer (`{components.admin-version-drawer.width}` maximum; viewport width on mobile), with 16px body padding, its own scroll container and persistent close control. Read-only values remain readable and copyable; select/switch/number controls retain their disabled presentation. Closing returns focus to the version link.
- Effective configuration and validation use the same responsive label/value layout. Long identifiers and fingerprints wrap inside their cells. Keep status names and diagnostics readable; semantic tags reinforce text rather than replacing it.
- Preserve the established forms, revisioned actions and confirmations described in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md). This section documents presentation, not operation eligibility or API limits.

### Operational drawers
- The [October 9 bounded review](../../docs/reports/background-work-ui-review-verification.md) confirms the existing original-result composition at 1440/768/390px; retain it rather than adding another polish layer. Read preserved original titles/status/results/files separately from continued Chat and completion delivery. Settled drawer sizing is 640px on desktop/tablet and full viewport on mobile, with one 16px body inset.
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

Instance contextual navigation uses focused destinations with native Ant Design outline icons: Profile, Definition version, Instructions, Conversation, Behavior, Initiative, Voice, Models, Providers, Execution budgets, Skills, Resources, Workspace, Capabilities, Credential bindings, Self-management, Trigger restrictions, Memory policy, Automations, Unattended model, Memory, Experience, Sessions, Runs, Lifecycle and Effective configuration. Draft pages share the focused configuration destinations with one candidate, Identity & goals, Metadata, Test & Publish and Advanced JSON. Immutable version inspectors retain their own local read-only tabs. Visited pages retain exact-owner form drafts. Configuration is a searchable compact collection with concise Name, Destination, readable When, lifecycle, next activation, last Run, model and creation provenance. Schedule and Event variants share Name/Instructions and explicit Run in controls. Existing conversations use the shared owned eligible Session picker and pinned model; separate background Sessions use unattended model controls and a distinct None/Selected conversation report picker. Tools/Vision requirements stay explicit. Expanded record details retain the shared top-aligned desktop labels and stacked mobile layout.

Activity defaults to Sessions with meaningful titles, secondary copyable IDs, origin, lifecycle and last activity. Sessions and Runs share the Admin collection toolbar/table, cursor footer and token-owned 8px row gaps; search names its loaded-page scope. Runs remains an independent collection with readable activation, secondary Run/Session IDs, Created, Status and Updated. Created means admission, not execution start. Continuity retains Memory and Experience. Run links open the exact AgentRun in the right-side Run details drawer, using the shared AgentRunDetails composition. The current collection does not show an Automation-name or admitted-Instructions column. Tables scroll locally while long drawer IDs/results wrap. Source controls expand/focus the exact configuration or Experience checkpoint without discarding drafts. Save returns focus to its source; deletion returns it to New automation. Close/Escape/mask restore the Run opener. Existing action groups wrap and retain at least 40px height below 768px. Exact Session reads gate inspection actions; cached list rows never hide missing or failed detail reads. Loading uses the incumbent Ant Design Spin. Detail failures show one inline Alert with an exact-session retry and no lingering spinner. Background detail reuses BackgroundWorkDrawer with the same 640px maximum/full-mobile width and 16px body inset. Safe errors and explicit retry stay inside the affected panel/drawer. No execution-authoring controls belong in Runs.

### Message file and artifact cards

Sent attachments and published artifacts share `ChatFileCard`. File collections own aligned stacked rows capped at 32rem; file-bearing user bubbles reserve available width and image thumbnails remain above their named card. Filenames expose their complete text in hover/keyboard tooltips. Published file cards reuse the file-chip elevated fill, 1px border, 12px radius and 8px × 12px padding. The enclosing block owns available width; the card caps at 32rem. One vertically centered horizontal row contains the leading file icon, a shrinking metadata column with filename ellipsis and secondary type/size, then tooltip-equipped Preview and Download icon-only Ant Design buttons. Complete filenames remain in accessible names/titles. Trailing actions use a non-shrinking group with the existing tight gap; mobile hit targets retain 40px. Loading and failed metadata/downloads keep safe local Retry. Generic Artifact and Workspace actions contain no provider-specific export button; integration mutation belongs to authorized conversational tools. No extra shadow or workspace browsing surface is added.

### Admin Event and Automation compositions
- Global Connections → Events shares the collection toolbar and pagination. The unified catalog combines Built-in and Webhook rows with All / Built-in / Webhook filtering. Its Subscriptions column reads active / total for Webhooks and Instance-scoped for Built-in definitions; Webhook details retain separate labeled counts. Event details separate Received signals from Automation deliveries, with distinct truthful empty states. Use the existing responsive Descriptions and local table scroll; payloads and protected credentials are absent from resting details.
- Built-in definition inspection uses the same 640px/full-mobile drawer, responsive Descriptions and read-only schema/example fields. `BuiltinEventSubscribers` owns an 8px heading/selector/table stack. Its compact collection table scrolls locally at 500px, with a 280px ellipsis name column and 220px wrapping state column. Full names remain in titles and accessible button text; links reuse the shared focus and 40px mobile hit policy. Built-in delivery diagnostics reuse the collection table wrapping and tabular numerals with a 650px local scroll width. Definition details remain visible beside independent subscriber-read recovery.
- Keep View as the primary row inspection action. More actions contains Rename, Rotate credential or Reactivate Event, and Revoke as applicable. Consequential operations use the shared centered confirmation. Reactivate Event copy explains the new credential and future signals; no previously skipped delivery is implied to replay.
- `WebhookRequestExample` composes direct Ant Design Tabs, read-only Input.TextArea, Button and Alert. JSON body and cURL request share the same copyable region, with compact gaps and a wrapping copy action. Keep the selectable text available when clipboard access fails, show recovery inline, and use an illustrative body and `<secret>` placeholder. Optional UTC `occurredAt` guidance stays beside the example. No new code-editor styling or credential display is introduced.
- New/Edit Automation keeps the existing 640px maximum drawer, full width below 768px, and 16px body inset. The persistent footer owns a 12px gap between Automation summary and the Save row; the summary owns an 8px gap between its labeled lines. Summarize the selected trigger and bounds, destination and requested report; show disabled or incomplete-selection guidance in place. These are secondary text lines rather than another card. Save remains content-width with the existing 40px mobile minimum.
- Instance navigation uses Profile, Definition version, Lifecycle, Automations and Unattended model; the typed registry owns canonical URLs, active states and page headings. Capability overview uses the Authorized / Always selected count line without a duplicate capability disclosure. Knowledge source and saved scenario counts sit immediately beside their headings with the shared compact gap. The Always count describes explicit selections, not the complete initial model context; supporting copy acknowledges Core-managed browser bootstrap and active Skill requirements. Authorization and eligibility remain authoritative in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md).

### Automation destination and delivery presentation

Use the shared AutomationDestination and CompletionDeliveryStatus composition. Destination and Reports to are separate compact secondary lines with exact Conversation links; Originally created from remains provenance. Chat Automations links to the exact Admin record. Background Work shows Not requested, Ready, In use, Handled in conversation, Pending · message queued, Reported, Could not report or Not reported and keeps unavailable/quiet results inspectable. Parent callback provenance is modest inline Background work completed metadata and an owned child link on the ordinary assistant turn. Preserve 8/12/16px rhythm, Ant Design theme, 16px drawer body, persistent Admin editor footer and 40px narrow actions. Long title/status/action groups wrap; collection tables scroll locally.

A bounded Synthetic Playwright MCP review at 1440×900, 768×900 and 390×844 covers the conditional report picker, exact source link, Initiative-off report and truthful quiet child status. Resize capture waits for the existing drawer animation; full-mobile Background Work is confirmed at x=0,width=390. See [enhancement evidence](../../docs/reports/automation-targets-background-reportback-verification.md). Previous milestone evidence remains historical.


### Result handoff and wait presentation

Reuse CompletionDeliveryStatus and AgentRunDetails in Chat/Admin. Ready and In use are accounting states; Handled in conversation links the exact parent Run with View handling run and removes unread attention. Waiting uses a clock plus typed condition/deadline and Cancel, separate from retry/approval. Keep existing 640px drawers, full width below 768px, 16px inset and wrapping controls. Synthetic MCP review covers 1440×900, 768×900, 390×844; evidence is in docs/reports/assets/durable-completion-inbox.

Shared Event authoring retains the established Admin drawer/footer and one-time credential dialog. Event details use the existing responsive descriptions layout and genuine subscriber/delivery tables; detail action groups wrap while the collection keeps View and More actions inline. Automation’s existing drawer separates General, Trigger, Execution and applicable Completion; Advanced collapses model/effort and tool/vision requirements. Nested Event management preserves the Automation draft and returns focus to its initiating control. Searchable catalog and timezone selectors use direct Ant Design grouping/filtering, with the same category order for Authorized and Always available capabilities.


### Scoped Instance settings and resources

Inherited sparse settings are immediately editable through direct Ant Design controls. The global configuration panel exposes Save changes, Discard and Refresh across retained visited editors. Changed fields disclose saved or unsaved override/source state through the existing named tooltip icons; per-field Reset remains beside its label. `.instance-settings-field-label` reserves a 32px minimum at wide widths and 40px below 768px, with compact wrapping gaps and non-shrinking icon controls. Long labels may grow; adding a source icon does not shift the input. The shared 48rem form bound owns the two-column field grid, stacked below 768px, bounded short controls, horizontal switch rows and full-width instructions/multi-value fields. Section Reset remains confirmed; field inheritance and sequential-save/partial-error semantics stay in `/docs`. Scoped resource collections retain quiet stable identity and written enablement beside compact switches. Inspection uses the shared 12rem desktop detail-label owner, stacked mobile labels, one 16px drawer inset and content-width actions.


### Model reasoning presentation

Reuse the existing Chat Model dropdown and Admin ExecutionModelFields composition. Preserve token-owned shell/control insets and mobile wrapping. All reasoning controls present supported intensity in `none → minimal → low → medium → high → xhigh → max` order through the shared utility; unknown/adaptive modes use discrete choices outside the slider. Slider value text names the visible effort. Model switching preserves supported effort and selects the new configured default only when needed. Catalog names/capabilities come from backend descriptors; unavailable saved model keys remain explicit. No automatic routing controls or extra model-tier badges.


### Built-in Event presets and filters

The existing Instance Automation drawer owns disabled Custom/preset drafts with Schedule or Events mode. Independent collapsible Event rows use stable identities, source type/key, current eligibility, filter/sample tests and dispatch; one grouped searchable picker discovers Built-in and Webhook definitions. Global Events shows safe code-owned Built-in definitions read-only alongside managed Webhooks; private activity stays Instance owned. Preserve separate unsaved mode branches and per-row samples, nested creation into the initiating row, shared Ant Design v6 controls, 640px/full-mobile drawer, reachable footer and focus return. Partial catalog failures retain successful definitions and draft state with retry. Product behavior is owned by docs/13 and docs/14; this is presentation context only.

Browser operational budgets reuse the existing Admin Effective configuration Browser provider Descriptions, responsive detail labels and natural wrapping. Keep units and scope beside values, and the host-restart applicability in the same section. These are effective-value disclosures, with no separate settings layout or custom controls. Behavior and defaults remain owned by docs/13 and docs/15.

### Browser provider and screenshot privacy

The existing Browser provider section in Effective configuration owns provider readiness, operational limits and screenshot privacy. Effective/saved policy Descriptions retain the full section width, shared 12rem desktop label edge and stacked labels below 768px. The nested privacy editor uses a 12px content stack and the shared bounded configuration form; its enclosing panel owns the outer inset. Supporting copy stays within 72ch. Save and Reload are content-width actions with an 8px wrapping gap and 40px mobile minimum height. Loading uses Spin; read/write recovery and saved-revision feedback use inline Alert at the affected editor. Unmasked exposure review uses the existing centered danger confirmation. Mode/revision, deployment constraints and activation behavior stay in [Frontend Implementation](../../docs/13-frontend-implementation-spec.md#browser-privacy-editor).

### Agent self-management presentation

Reuse the single Self-management authority page and existing bounded configuration form. Off/Assisted/Managed, per-field inheritance/reset, allowed areas and explicitly delegated automatic categories must remain legible at 1440/768/390. Freeze is an Instance-local lock with explicit Admin unfreeze; retain choices/content and ordinary tasks. Definition Self-management provides defaults through the shared draft/read-only editor. Built-in · Read-only · On demand management procedures use existing Skill rows/drawers, without content authoring controls; distinguish stored enabled choice from master suppression. Exact Chat approvals show target, complete before/source/after, revisions and next new Run timing; freeze states immediate revocation. Preserve stale drafts, explicit refresh, Escape, focus return and shared diagnostics. Behavioral authority remains docs/13 and docs/14; the successor report records runtime evidence and remaining acceptance.

### Email Account presentation

Observed Gmail account forms reuse the Credentials panel/table, expanded safe details and bounded 640px Admin dialog. The shared Add credential dialog selects Google Drive, Gmail, Custom IMAP/SMTP or General purpose, then reuses the appropriate specialist fields. Gmail has display name and concise read/send consent guidance without password, editable sender or endpoints. Verified mailbox, independent credential/connection status and semantic icon/text tags remain in the same Ant Design v6 Operate world. Synthetic says no Google connection or delivery. Custom retains its TLS/password editor and incoming/outgoing tests. Reconnect/Test access/Disconnect use wrapping 8px action gaps and explicit all-agent confirmation; shared 8/12/16 spacing and 40px mobile controls remain. Callback reopens the authoritative account; retry guidance stays local. Decorative button icons are hidden from accessible names. The explicit Chat chooser and exact From/To/Cc/Bcc/content approval remain separate. Current responsive interaction evidence at 1440/768/390/320 lives in docs/reports/gmail-oauth-verification-2026-10-12.md; `/docs` owns behavior and acceptance.

### Admin branding, navigation and configuration actions

Brand the header Agent Core with a secondary Admin marker; a visible 24px vertical separator distinguishes desktop breadcrumbs. At 320px the secondary marker stays fully visible beside the wordmark; header/brand gaps alone may use native paddingXXS (4px). The regular 8/12/16 roles remain unchanged. Contextual navigation has direct collection/back links and no Global integrations & platform shortcut. Icons are native Ant Design outline icons, hidden from accessible names.

Use one bordered, rounded sticky actions frame with a single 16px inset for Definition draft Save/Publish/Delete and Instance configuration Save/Discard/Refresh. The configuration bar coordinates existing CAS commands, reports partial writes and preserves remaining edits. Inherited settings are editable immediately; only edits create sparse overrides and per-field reset keeps its existing semantics. Minimum 32px desktop / 40px narrow label rows prevent override/restriction/reset buttons from moving controls. Tables use tooltipped icon buttons for familiar actions, or a More menu with native icon-before-label choices; returning from a dialog restores its surviving trigger or the creation control. Behavior and publication gates remain owned by docs/13 and docs/14.

### Email Monitoring presentation

Expanded Email Account details add Monitoring below connection/transport details using direct AntD Flex, Switch, Select, Tag, Button, Alert, Descriptions and diagnostic Collapse. Shared table inline-size containment keeps Credential detail controls within the visible viewport; inventory columns still scroll locally. Use token-owned 8px action gaps, 12px groups and 16px section separation; new prose is bounded to 75ch. Saved versus dirty choices are explicit, with Save/Cancel/Reload, queued checks and local safe recovery. No kit/token changes or modal redesign. The shared Credential detail stack owns 16px separation between metadata, connection/transport and Monitoring. A quiet divider separates Monitoring; its 16px title shares a wrapping row with live semantic status, header copy uses an 8px stack, and settings/actions use a 12px stack. Reload is a text action. Subscriber guidance stays beside configuration links. Mobile interval controls and labels use 40px minimum height. Saved feedback clears on new drafts or Cancel; next-check text distinguishes Off, Paused and operator unavailability. Diagnostics stay collapsed, status combines icon and text, and the three-option Select renders accessible real options. `/docs/13` and `/docs/14` own behavior; the monitoring verification report owns observed responsive/interaction evidence and remaining gates.

Email/main integration keeps Monitoring inside the contextual Admin Credentials surfaces. Shared canonical route helpers own Automations and Credential bindings links; the focused Credential bindings page supersedes the earlier combined Capabilities & credentials destination. Instance monitoring drafts participate in retained workspace dirty status and departure protection. Account refreshes retain mounted details. Containment uses the available content width beside the 248px sidebar at 1024px and above, with shared navigation Drawer below. Integration captures and observed journeys are recorded in docs/reports/email-monitoring-main-integration-2026-10-13.md.

### File preview presentation

The read-only `FilePreview` composition inherits the shared Ant Design dark presentation roles. Source collections retain their own action spacing; the preview shell owns filename/actions, 12px sibling gaps and the bounded content canvas. Desktop uses 90vw capped at 1440px; below 768px, the shared Modal fills the viewport. Document-level horizontal overflow is forbidden; grids/code scroll locally. Mobile preview controls, worksheet inputs/tabs, checkboxes and dialog close have 40px minimum targets. The metadata group keeps source/size readable and truncates its MIME line with complete title/tooltip disclosure; worksheet tabs use the parent’s 12px gap without a second built-in bottom margin. Spreadsheet controls stay subordinate to the document; the range field grows into remaining mobile toolbar space. Word zoom shows a stable, tabular percentage beside its slider.

CSV defaults to Grid, with a quiet Grid/Text control and optional header checkbox. Word pages sit on the neutral canvas inside a sanitized, scriptless iframe; the document's own paper/text formatting stays within that frame. XLSX and CSV Grid share the lazy read-only FortuneSheet canvas with compact source-coordinate/window navigation, local scrolling, range Copy and zoom; XLSX retains worksheet tabs and a separate formula/cached-value readout. Backend effective policy owns limits; oversized/structural/fetch/renderer failures retain original Download and local recovery. Original document ink/fills stay on the sheet canvas, with Ant Design tokens owning the surrounding controls. Clipped merges are explicitly disclosed. Shortened XLSX cell/window text has an explicit native information notice and smaller-range/original Download guidance. A cell-length ceiling can still shorten a one-cell range; only the original download preserves every value. The [integrated correction](../../docs/reports/opensandbox-integration-verification.md#current-main-integration--2026-10-14) records browser recovery evidence. Loading, unsupported/corrupt/permission/stale/codec states use existing AntD controls. Source-owner changes abort and clear old content. Closing keeps native Modal focus restoration; Word frame Tab/Escape is handled by trusted parent listeners. No generic kit, palette or app-level Files sidebar is added.

The [successor ledger](../../docs/reports/office-file-preview-drive-verification-2026-10-13.md) owns original implementation/hosted acceptance. The [original polish verification](../../docs/reports/file-preview-drive-ui-polish-2026-10-14.md) records source `8e053018`, five-width containment, 40px mobile input/close measurements, short-screen save, destination recovery and responsive worksheet stability. Its [confirmation captures](../../docs/reports/file-preview-drive-ui-polish-2026-10-14.md#confirmation-captures) record the preceding presentation. The [FortuneSheet ledger](../../docs/reports/fortune-sheet-preview-verification-2026-10-14.md) owns subsequent shared-canvas verification and captures. Real Google, actual native 200% zoom, physical touch/screen-reader and installed Office-client fidelity remain separate and unverified.

Drive account connections remain in existing Credentials; Instance alias details open a bounded app-authorized file modal using the shared preview canvas. Drive search retains a wrapping control row and mobile hint for locally scrolling details/actions. Its named Clear control uses the emitted empty search value to restore the unfiltered list. Loading and retry stay local to the account/file collection. Generic Artifact, sent-file and Workspace actions expose Preview/Download; no destination form or Save to Drive control remains. Upload-session continuation and exact source/destination receipts belong to authorized agent capabilities, with mutation review through the existing ApprovalModal. Artifact and sent-file actions stay together in the non-shrinking card action group, keeping a readable filename at 320px. Synthetic identity remains visibly distinct from Google delivery. Word pages retain original document ink on a white page within the neutral product canvas; generated styles are sanitized inside a scriptless frame.

Dynamic single-line Chat header, Session rail and file labels use CSS truncation with complete-text Ant Design tooltips/titles. The shared presentation owner avoids measured Typography resize feedback while retaining the established dimensions, color roles and action grouping.

The [FilePreview surface brief](../../.impeccable/surfaces/web-src-features-filepreview-filepreview-tsx.md) records its Read mode and renderer composition; Chat/Admin briefs retain their Operate modes. Surface modes are not global design tokens.

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
- **Do** distinguish keyboard focus on picker choices from their selected/hover fill; the neutral editor focus treatment applies only to the editing well.
- **Do** keep the labeled composer available while voice is live until `/docs` and tests change together.
- **Do** treat Admin as the same dark product: shared spacing tokens, configuration panels, and operator copy. Behavior stays in `/docs`.
- **Do** use tooltipped, accessibly named icon buttons for familiar table actions. Use More for larger or text-dependent action sets, with a native decorative icon before each menu label. Keep adequate column width, compact gaps and table-local scrolling; dialog close restores its surviving trigger or creation control. Forms and expanded details still wrap.
- **Do** reuse `AgentConfigurationPanel` and the shared configuration panels and canonical sidebar for repeated Admin sections; let one body stack own spacing and keep feature state/commands in their existing owners.
- **Do** reuse Admin collection search/pagination and responsive detail alignment; keep table summaries compact, expanded content contained, and mobile labels stacked.
- **Do** apply the shared bounded form and collection table layouts to incoming screens, including nested privacy editors and Built-in subscriber/delivery tables. Keep one outer inset, complete accessible names and table-local scrolling.
- **Do** use the shared centered Ant Design confirmation dialog (`confirmAction` in `web/src/app/confirmAction.ts`) for destructive or consequential actions across Chat, Admin, Background Work, and Automations (session delete, Admin definition/instance delete, deprecation, run cancellation, approval decisions, memory reset, automation revoke, and similar). Prefer a stable `dialog` surface for tests and keyboard focus.
- **Do** keep header identity, Speech locale, compact icon actions, profile, and connection status visible. Use 32×32px, 6px-radius fill-hover actions; keep Background Work at an 18px inbox icon with attention badge and Automations/Admin/End at 16px.
- **Do** keep ended history on the same reading column with a quiet ended note, not a disabled input.
- **Do** reuse AgentRunDetails across Chat history and Admin inspection, keeping one outer drawer inset, semantic filled status chips, open bounded reading regions and content-width consequential controls. Automations retains its elevated schedule inset.
- **Do** reuse the global Admin collection and per-instance binding layouts for System Credentials. Mask transient protected input with no reveal control; safe metadata and grant aliases use existing forms and tables. Keep browser state reset in its own section and event reactions in the shared Automation collection. Attention results use the words Needs attention plus an icon. Quiet completions do not change the header count.
- **Do** format schedule occurrences for the viewer locale in the schedule’s named time zone; keep the zone identifier visible beside the readable time.
- **Do** show updated timestamps with the shared chat time formatter and a `time` element. Background Session origins use readable labels (Immediate task, Automation, Application event, Manual task); Run turn kinds use Chat turn, Scheduled task, Completion report and the other implemented labels. Keep transport values internal.
- **Do** keep the unattended model and its effective-source sentence on Unattended model. Show a webhook credential once in a dialog, then clear it on Done or Escape. The resting Event collection keeps public keys and status only; System Credential projections keep only safe metadata and policy.

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

### Credential creation and Drive approval presentation

One primary Add credential opens the existing bounded Admin Modal with a purpose Select and reused specialist fields. Purpose changes discard transient form/secret state; immutable-purpose edits open their specialist dialog directly. Saved Gmail/Drive connection Tags use the same semantic tokens/icons beside a separate Active/Disabled tag; Synthetic is explicit. Metadata keeps verified identity and provider/scope without duplicating Connected. The shared ApprovalModal uses action-specific Drive titles and destructive Trash CTAs, an 880px maximum viewport-bounded dialog and compact native Ant Design table for exact file/name/version targets. Reuse admin-collection-table cell wrapping, tabular numerals and native 8px padding with fixed layout. Name/File ID/Version columns use 36%/52%/12% on desktop so typical IDs fit on one line; longer values still wrap. Below 768px the exact 12px ID sits beneath the filename with a native 4px Flex gap, and Version remains separate. Metadata wraps completely without pagination, selection or ellipsis. Drive review content may use 60vh and shrinks inside the existing viewport-bounded container; other approvals retain 40vh. Titles reserve 32px for the native close control; footer, keyboard focus and Reject/Escape behavior remain. Composer Send/Stop tooltips are unmounted while an approval is pending so a popup behind the modal cannot consume its first Escape; normal hover help resumes after the decision. Product behavior is owned by docs/13-frontend-implementation-spec.md.

## Session sandbox presentation (P10 local demo)

Platform Sandboxes reuses retained Admin configuration panels, AntD vertical Form and existing control-width/navigation-guard rules. Global state and safe limitations precede editable profiles; Save/Discard/Verify share the compact action gap. Definition and Instance selections reuse the shared Sandbox configuration panels and profile facts; command grants remain in Capabilities. Published versions expose read-only Sandbox inspection.

Chat adds a quiet Code icon beside existing header actions. Session sandbox uses the existing 640px desktop/full-width mobile Drawer pattern, bordered descriptions, wrapping status/exit/duration metadata, copyable code and AntD Collapse for output. Foreground stdout/stderr stay distinct; background output is labeled combined. Recovery errors retain prior evidence and consequential actions require the shared confirmation with stale-selection fencing. Files stay in shared Artifact/file-preview surfaces from current main; no second viewer or kit is added.

One bounded local review inspected Admin and Chat at 1440/1024/768/390/320px after drawer transitions settled, with no horizontal overflow. Runtime Save/Discard/navigation guarding, real coding/Office downloads, cancel/reset and Core restart were exercised. The merged 1,021 frontend tests and build passed; final runner/hosted acceptance remains in the verification ledger. This records local evidence, not production or physical-device acceptance.

### Sandbox recovery clarity

Retain native Ant Design Alert, wrapping metadata, the existing 640px/full-width Drawer and compact parent gaps. Setup failure names the correction and reset; expiry explains retained Workspace/Artifacts. Confirmed file commit with uncertain command completion and unknown file commit use distinct warning copy. Unknown never means not committed. Admin Verify profile tests the saved image and initialization; its timestamp/message is an observation of the unchanged configuration revision. Backend/product documents own these semantics.

## Sandbox configuration and observable setup

Operate mode preserves the dark Ant Design v6 Admin shell. Instance → Sandbox sits beside Execution budgets and owns inherited Profile selection, sparse reduced resource/lifecycle/storage overrides and the optional Instance supplement script. Reuse shared configuration Save/Discard/Refresh/CAS controls, inherited-value tooltips, field grid and Reset to inherited. Existing `execution#sandboxPolicy` links resolve to the new owner. Effective setup remains visibly saved configuration; an existing generation keeps its pinned revisions until expiry/reset.

Definition drafts place Sandbox beside Execution budgets, matching Instance navigation. The shared configuration panel owns outer insets and section gaps; stacked fields own 8px label/control gaps. Default selection uses the existing 20rem settings control cap; multiple allowed profiles wrap across the available field width. Shared SandboxProfileDetails and responsive descriptions keep current Global image/resources/lifecycle/storage/setup facts aligned with Instance effective setup. Published-version Sandbox inspection has no editable controls. Inventory failures preserve saved IDs, fence both selectors and offer Retry profiles; unavailable IDs remain visible. Capabilities links to the policy owner, and legacy `capabilities#sandboxPolicy` draft links resolve to Sandbox.

Global Sandboxes separates Check image readiness from Verify profile. Instance setup verification tests both ordered scripts and displays exact saved configuration pins. Bounded escaped stdout/stderr uses the shared collapsed SandboxEvidence component, with exit/duration/truncation/diagnostic evidence separate from Profile fields. The Session drawer exposes Initialization plus created/activity/idle-or-active-reservation/maximum-lifetime and cleanup reason/retry state. Failed or uncertain initialization requires explicit reset even after cleanup; unknown filesystem commits require inspection and never imply absent changes. Existing confirmations and surviving-trigger focus restoration apply. Verify affected configuration/drawer layouts at 1440/768/390/320; keep controls wrapping and long images/scripts/diagnostics within the shared content width.
